// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.PowerShell;

/// <summary>
/// Executes one <see cref="PowerShellRequest"/> on a pooled runspace (ADR-0006).
///
/// Parameters bind through <see cref="global::System.Management.Automation.PowerShell.AddParameter(string, object)"/> in both request
/// kinds -- there is deliberately no code path here that concatenates a parameter
/// value into command text, which is what keeps secrets out of script strings
/// (security.md control 2) and command injection unrepresentable at this layer.
///
/// Every PowerShell output stream is forwarded, as it arrives, into the batched
/// redacting <see cref="IJobLogBuffer"/> as a <c>job.log</c> event with a severity
/// field (information/warning/error/verbose/debug). Ordering note: order is
/// preserved within one stream (each is an ordered collection); interleaving
/// *across* streams follows event arrival, which SMA does not totally order --
/// good enough for a live log view, and the only order SMA can give without
/// merging streams in the pipeline itself.
///
/// Containment: a request that outlives its timeout gets <c>Stop()</c>; a pipeline
/// that ignores Stop past <see cref="PowerShellOptions.StopGracePeriod"/> has its
/// runspace poisoned (replaced by the pool, never reused) and the invocation
/// reports <c>TimedOut</c>. A terminating error or non-zero native exit code fails
/// the result; non-terminating errors keep PowerShell's own convention (pipeline
/// completes, <c>HadErrors</c> is set).
/// </summary>
public sealed partial class PowerShellExecutor : IPowerShellExecutor
{
	private readonly WaypointRunspacePool _pool;
	private readonly IJobLogBuffer _logBuffer;
	private readonly IOptions<PowerShellOptions> _options;
	private readonly ILogger<PowerShellExecutor> _logger;

	public PowerShellExecutor(
		WaypointRunspacePool pool, IJobLogBuffer logBuffer, IOptions<PowerShellOptions> options, ILogger<PowerShellExecutor> logger)
	{
		ArgumentNullException.ThrowIfNull(pool);
		ArgumentNullException.ThrowIfNull(logBuffer);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_pool = pool;
		_logBuffer = logBuffer;
		_options = options;
		_logger = logger;
	}

	public async Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);

		TimeSpan timeout = request.Timeout ?? _options.Value.DefaultInvocationTimeout;
		using WaypointRunspacePool.RunspaceLease lease = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
		using global::System.Management.Automation.PowerShell powershell = global::System.Management.Automation.PowerShell.Create();
		powershell.Runspace = lease.Runspace;

		if (request.Kind == PowerShellRequestKind.Script)
		{
			powershell.AddScript(request.Command);
		}
		else
		{
			powershell.AddCommand(request.Command);
		}

		foreach ((string name, object? value) in request.Parameters ?? new Dictionary<string, object?>())
		{
			powershell.AddParameter(name, value);
		}

		WireStreamCapture(powershell, request);

		bool timedOut = false;
		string? failureReason = null;
		PSDataCollection<PSObject> output = [];

		try
		{
			Task invocation = powershell.InvokeAsync<PSObject, PSObject>(input: null, output);
			Task winner = await Task.WhenAny(invocation, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
			if (winner != invocation)
			{
				cancellationToken.ThrowIfCancellationRequested();
				timedOut = true;
				failureReason = string.Format(CultureInfo.InvariantCulture, "Invocation exceeded its {0} timeout and was stopped.", timeout);

				// StopAsync itself can hang on the same wedged pipeline, so it gets its
				// own grace budget; a pipeline that outlives it forfeits the runspace.
				Task stop = powershell.StopAsync(callback: null, state: null);
				Task stopWinner = await Task.WhenAny(stop, Task.Delay(_options.Value.StopGracePeriod, CancellationToken.None)).ConfigureAwait(false);
				if (stopWinner != stop)
				{
					LogPipelineIgnoredStop(request.Command, timeout, _options.Value.StopGracePeriod);
				}
				else
				{
					// Stop succeeded; observe the (expected) PipelineStoppedException so
					// the invocation task is not left unobserved.
					await invocation.ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
				}
			}
			else
			{
				await invocation.ConfigureAwait(false);
			}
		}
		catch (RuntimeException exception)
		{
			// A terminating error: the scriptblock threw, or a cmdlet raised a
			// pipeline-terminating error. The message has already been redacted on its
			// way into job.log via the error stream; here it feeds the result only.
			failureReason = exception.ErrorRecord?.ToString() ?? exception.Message;
		}

		int? nativeExitCode = ReadNativeExitCode(lease);
		bool succeeded = !timedOut && failureReason is null && nativeExitCode is null or 0;
		if (succeeded is false && failureReason is null && nativeExitCode is int code)
		{
			failureReason = string.Format(CultureInfo.InvariantCulture, "Native command exited with code {0}.", code);
		}

		LogInvocationFinished(request.Command, succeeded, timedOut, powershell.HadErrors);
		return new PowerShellExecutionResult(
			succeeded,
			Output: [.. output.Select(Unwrap)],
			HadErrors: powershell.HadErrors,
			TimedOut: timedOut,
			FailureReason: failureReason,
			NativeExitCode: nativeExitCode);
	}

	/// <summary>Forwards each PowerShell stream into <c>job.log</c> as records arrive. Severity names follow the stream names.</summary>
	private void WireStreamCapture(global::System.Management.Automation.PowerShell powershell, PowerShellRequest request)
	{
		powershell.Streams.Information.DataAdded += (sender, args) =>
			Emit(request, "information", ((PSDataCollection<InformationRecord>)sender!)[args.Index].ToString());
		powershell.Streams.Warning.DataAdded += (sender, args) =>
			Emit(request, "warning", ((PSDataCollection<WarningRecord>)sender!)[args.Index].Message);
		powershell.Streams.Error.DataAdded += (sender, args) =>
			Emit(request, "error", ((PSDataCollection<ErrorRecord>)sender!)[args.Index].ToString());
		powershell.Streams.Verbose.DataAdded += (sender, args) =>
			Emit(request, "verbose", ((PSDataCollection<VerboseRecord>)sender!)[args.Index].Message);
		powershell.Streams.Debug.DataAdded += (sender, args) =>
			Emit(request, "debug", ((PSDataCollection<DebugRecord>)sender!)[args.Index].Message);
	}

	private void Emit(PowerShellRequest request, string severity, string message)
	{
		// Serialization (not interpolation) builds the payload, so a message containing
		// quotes/backslashes cannot break the JSON -- and the buffer redacts tracked
		// secrets (raw and JSON-escaped spellings) before the row is written.
		string payload = JsonSerializer.Serialize(new { severity, line = message });
		_logBuffer.TryEnqueue(JobEventTypes.JobLog, request.JobId, request.RunId, payload);
	}

	/// <summary>
	/// A [pscustomobject]'s note properties live on the PSObject wrapper, not on its
	/// BaseObject (a property-less PSCustomObject sentinel) -- unwrapping those loses
	/// the data. CLR-backed objects (strings, ints, real .NET types) unwrap cleanly.
	/// </summary>
	private static object? Unwrap(PSObject? item)
	{
		return item?.BaseObject is PSCustomObject ? item : item?.BaseObject;
	}

	private static int? ReadNativeExitCode(WaypointRunspacePool.RunspaceLease lease)
	{
		// $LASTEXITCODE only exists after a native command ran; module functions leave
		// it unset. Read defensively -- a poisoned/broken runspace must not throw here.
		try
		{
			return lease.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int code ? code : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "PowerShell pipeline for '{Command}' exceeded {Timeout} and ignored Stop() for {StopGrace}; runspace poisoned")]
	private partial void LogPipelineIgnoredStop(string command, TimeSpan timeout, TimeSpan stopGrace);

	[LoggerMessage(Level = LogLevel.Debug, Message = "PowerShell invocation '{Command}' finished: succeeded={Succeeded}, timedOut={TimedOut}, hadErrors={HadErrors}")]
	private partial void LogInvocationFinished(string command, bool succeeded, bool timedOut, bool hadErrors);
}
