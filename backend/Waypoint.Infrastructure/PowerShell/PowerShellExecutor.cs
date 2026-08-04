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

		// NOT `using`: disposing a PowerShell whose pipeline ignored Stop() blocks the
		// caller for the life of the hang (PR #157 round 1 measured 13.6s of a 15s hang
		// spent inside Dispose). A poisoned invocation abandons the object to the same
		// background-disposal fate as its runspace; the finally below disposes inline
		// only when the pipeline ended cooperatively.
		bool abandonPowerShell = false;
		Action? unwireStreams = null;
		PSDataCollection<PSObject>? output = null;
		global::System.Management.Automation.PowerShell powershell = global::System.Management.Automation.PowerShell.Create();
		try
		{
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

			unwireStreams = WireStreamCapture(powershell, request);
			ResetNativeExitCode(lease);

			bool timedOut = false;
			string? failureReason = null;
			output = [];

			try
			{
				Task invocation = powershell.InvokeAsync<PSObject, PSObject>(input: null, output);
				Task winner = await Task.WhenAny(invocation, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
				if (winner != invocation)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						// Cancellation gets the same containment as a timeout (round-1
						// finding 2): stop with a grace budget, poison-and-abandon a
						// pipeline that ignores it, THEN propagate the cancellation --
						// never dispose a wedged pipeline on the caller path.
						abandonPowerShell = await StopWithGraceAsync(powershell, lease, invocation, request.Command, timeout).ConfigureAwait(false);
						cancellationToken.ThrowIfCancellationRequested();
					}

					timedOut = true;
					failureReason = string.Format(CultureInfo.InvariantCulture, "Invocation exceeded its {0} timeout and was stopped.", timeout);
					abandonPowerShell = await StopWithGraceAsync(powershell, lease, invocation, request.Command, timeout).ConfigureAwait(false);
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

			// SessionStateProxy calls wait for pipeline availability -- on an abandoned
			// (still-running) pipeline that wait IS the hang, so a poisoned invocation
			// skips the read; its runspace is being replaced anyway.
			int? nativeExitCode = abandonPowerShell ? null : ReadNativeExitCode(lease);
			bool succeeded = !timedOut && failureReason is null && nativeExitCode is null or 0;
			if (succeeded is false && failureReason is null && nativeExitCode is int code)
			{
				failureReason = string.Format(CultureInfo.InvariantCulture, "Native command exited with code {0}.", code);
			}

			LogInvocationFinished(request.Command, succeeded, timedOut, powershell.HadErrors);

			// ReadAll, not enumeration: PSDataCollection's enumerator BLOCKS while the
			// collection is still open (an abandoned pipeline's is, for the life of the
			// hang -- round-1 finding 1's second face). ReadAll drains whatever has
			// arrived without waiting; for a completed pipeline that is everything.
			return new PowerShellExecutionResult(
				succeeded,
				Output: [.. output.ReadAll().Select(Unwrap)],
				HadErrors: powershell.HadErrors,
				TimedOut: timedOut,
				FailureReason: failureReason,
				NativeExitCode: nativeExitCode);
		}
		finally
		{
			if (abandonPowerShell)
			{
				// Detach stream capture first: the abandoned pipeline keeps producing
				// records, and its job has already reported its outcome (#160).
				unwireStreams?.Invoke();
				AbandonPowerShell(powershell, output);
			}
			else
			{
				powershell.Dispose();
				output?.Dispose();
			}
		}
	}

	/// <summary>
	/// Stops a pipeline with the configured grace budget. Returns true when the
	/// pipeline ignored Stop() -- the lease is poisoned and the caller must abandon
	/// the PowerShell object rather than dispose it inline.
	/// </summary>
	private async Task<bool> StopWithGraceAsync(
		global::System.Management.Automation.PowerShell powershell,
		WaypointRunspacePool.RunspaceLease lease,
		Task invocation,
		string command,
		TimeSpan timeout)
	{
		// StopAsync itself can hang on the same wedged pipeline, so it gets its own
		// grace budget; a pipeline that outlives it forfeits the runspace.
		Task stop = powershell.StopAsync(callback: null, state: null);
		Task stopWinner = await Task.WhenAny(stop, Task.Delay(_options.Value.StopGracePeriod, CancellationToken.None)).ConfigureAwait(false);
		if (stopWinner != stop)
		{
			lease.Poison();
			LogPipelineIgnoredStop(command, timeout, _options.Value.StopGracePeriod);
			ObserveFaults(stop);
			ObserveFaults(invocation);
			return true;
		}

		// Stop succeeded; observe the (expected) PipelineStoppedException so the
		// invocation task is not left unobserved.
		await invocation.ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
		return false;
	}

	private static void ObserveFaults(Task task)
	{
		_ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
	}

	private static void AbandonPowerShell(global::System.Management.Automation.PowerShell powershell, PSDataCollection<PSObject>? output)
	{
		// Same rationale as the pool's background runspace discard: Dispose of a
		// PowerShell whose pipeline ignored Stop() blocks until the pipeline ends,
		// which may be never -- that wait cannot happen on the caller path. The output
		// collection is disposed on the same background path, deliberately AFTER the
		// PowerShell object: its Dispose blocks until the pipeline actually ended, so
		// the collection is no longer being written to when it is torn down (#160's
		// second criterion -- the collection is not leaked with the abandoned object).
		_ = Task.Run(() =>
		{
			try
			{
				powershell.Dispose();
			}
			catch (Exception)
			{
				// The object is abandoned either way; there is nothing left to do.
			}
			finally
			{
				output?.Dispose();
			}
		});
	}

	/// <summary>
	/// Forwards each PowerShell stream into <c>job.log</c> as records arrive. Severity
	/// names follow the stream names. Returns the unwire action: an abandoned pipeline
	/// keeps running after its job already reported TimedOut, and without detaching
	/// these handlers its late records would keep landing in <c>job.log</c> under a
	/// finished job (#160).
	/// </summary>
	private Action WireStreamCapture(global::System.Management.Automation.PowerShell powershell, PowerShellRequest request)
	{
		EventHandler<DataAddedEventArgs> onInformation = (sender, args) =>
			Emit(request, "information", ((PSDataCollection<InformationRecord>)sender!)[args.Index].ToString());
		EventHandler<DataAddedEventArgs> onWarning = (sender, args) =>
			Emit(request, "warning", ((PSDataCollection<WarningRecord>)sender!)[args.Index].Message);
		EventHandler<DataAddedEventArgs> onError = (sender, args) =>
			Emit(request, "error", ((PSDataCollection<ErrorRecord>)sender!)[args.Index].ToString());
		EventHandler<DataAddedEventArgs> onVerbose = (sender, args) =>
			Emit(request, "verbose", ((PSDataCollection<VerboseRecord>)sender!)[args.Index].Message);
		EventHandler<DataAddedEventArgs> onDebug = (sender, args) =>
			Emit(request, "debug", ((PSDataCollection<DebugRecord>)sender!)[args.Index].Message);

		powershell.Streams.Information.DataAdded += onInformation;
		powershell.Streams.Warning.DataAdded += onWarning;
		powershell.Streams.Error.DataAdded += onError;
		powershell.Streams.Verbose.DataAdded += onVerbose;
		powershell.Streams.Debug.DataAdded += onDebug;

		return () =>
		{
			powershell.Streams.Information.DataAdded -= onInformation;
			powershell.Streams.Warning.DataAdded -= onWarning;
			powershell.Streams.Error.DataAdded -= onError;
			powershell.Streams.Verbose.DataAdded -= onVerbose;
			powershell.Streams.Debug.DataAdded -= onDebug;
		};
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

	/// <summary>
	/// Clears a previous invocation's <c>$LASTEXITCODE</c> before running (round-1
	/// finding 3): the variable persists on the reused runspace, so without the reset
	/// a clean module-function call after a failed native command re-reported the
	/// stale non-zero code as its own failure.
	/// </summary>
	private static void ResetNativeExitCode(WaypointRunspacePool.RunspaceLease lease)
	{
		try
		{
			lease.Runspace.SessionStateProxy.SetVariable("LASTEXITCODE", null);
		}
		catch (Exception)
		{
			// A broken runspace fails the invocation itself soon after; the reset must
			// not be the thing that throws.
		}
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
