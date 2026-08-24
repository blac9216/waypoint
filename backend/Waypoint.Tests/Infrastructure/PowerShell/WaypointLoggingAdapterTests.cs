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

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #579 acceptance criteria for the shared <c>WaypointLogging</c> adapter
/// module (Get-LogSplat/Write-Log), run through
/// <c>Assets/WaypointLoggingCallerStub/WaypointLoggingCallerStub.psm1</c> -- an
/// invented caller that uses the adapter exactly the way the imported
/// vmware-stig-docker transport files do (`$WriteLogParams = Get-LogSplat $Source;
/// Write-Log ... @WriteLogParams`), so these tests pin the adapter's real calling
/// convention rather than a hand-picked one. Covers: every severity lands as a
/// job.log event, source is preserved, an Error-severity log line never becomes a
/// terminating job failure (only an actual `throw` does), and nothing routed through
/// the adapter bypasses the redacting job.log capture path.
/// </summary>
public sealed class WaypointLoggingAdapterTests : IDisposable
{
	private sealed class RecordingLogBuffer : IJobLogBuffer
	{
		public ConcurrentQueue<(string EventType, Guid? JobId, Guid? RunId, string Payload)> Events { get; } = new();

		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson)
		{
			Events.Enqueue((eventType, jobId, runId, payloadJson));
			return true;
		}
	}

	private static readonly string LoggingModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointLogging", "WaypointLogging.psm1");

	private static readonly string CallerStubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointLoggingCallerStub", "WaypointLoggingCallerStub.psm1");

	private readonly RecordingLogBuffer _buffer = new();
	private WaypointRunspacePool _pool = null!;

	public WaypointLoggingAdapterTests()
	{
		Assert.True(File.Exists(Path.GetFullPath(LoggingModulePath)), $"expected the adapter at '{Path.GetFullPath(LoggingModulePath)}'");
		Assert.True(File.Exists(CallerStubModulePath), $"expected the caller stub at '{CallerStubModulePath}'");
	}

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new() { MaxRunspaces = 1, DefaultInvocationTimeout = TimeSpan.FromSeconds(30) };
		options.ModulePreloadPaths.Add(Path.GetFullPath(LoggingModulePath));
		options.ModulePreloadPaths.Add(CallerStubModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, _buffer, wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	public void Dispose()
	{
		_pool?.Dispose();
	}

	private static string Line(JsonDocument document) => document.RootElement.GetProperty("line").GetString()!;

	private static string Severity(JsonDocument document) => document.RootElement.GetProperty("severity").GetString()!;

	[Theory]
	[InlineData("debug line", "debug")]
	[InlineData("verbose line", "verbose")]
	[InlineData("info line", "information")]
	[InlineData("success line", "information")]
	[InlineData("warning line", "warning")]
	[InlineData("error line", "error")]
	[InlineData("critical line", "error")]
	public async Task EverySeverity_LandsInJobLog_OnTheExpectedNativeStream(string expectedMessageFragment, string expectedNativeSeverity)
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-LoggingCallerStubAllSeverities"), CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		(string, Guid?, Guid?, string) match = Assert.Single(
			_buffer.Events, entry => entry.Payload.Contains(expectedMessageFragment, StringComparison.Ordinal));
		using JsonDocument document = JsonDocument.Parse(match.Item4);
		Assert.Equal(expectedNativeSeverity, Severity(document));
		Assert.Contains($"[LoggingCallerStub] {expectedMessageFragment}", Line(document), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Source_IsOmitted_WhenGetLogSplatReceivesNoSource()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-LoggingCallerStubWithoutSource"), CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		(string, Guid?, Guid?, string) match = Assert.Single(
			_buffer.Events, entry => entry.Payload.Contains("sourceless line", StringComparison.Ordinal));
		using JsonDocument document = JsonDocument.Parse(match.Item4);
		Assert.Equal("sourceless line", Line(document));
	}

	/// <summary>
	/// AC: "logging Error must not silently redefine success semantics." An
	/// Error-severity Write-Log call must not throw and must not stop the pipeline;
	/// only the caller's own subsequent `throw` may fail the job -- pinned here by
	/// asserting the job DOES fail (from the throw), while the Error-severity line
	/// still shows up in job.log as a log entry rather than as the failure reason.
	/// </summary>
	[Fact]
	public async Task ErrorSeverityLogLine_DoesNotThrow_OnlyTheCallersOwnThrowFailsTheJob()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-LoggingCallerStubErrorThenTerminatingFailure"), CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Contains("the real terminating failure", result.FailureReason, StringComparison.Ordinal);
		Assert.Contains(
			_buffer.Events,
			entry => entry.Payload.Contains("an error-severity log line, not a job failure", StringComparison.Ordinal));
	}

	/// <summary>
	/// The adapter has no file writer, console writer, or queue of its own -- every
	/// call lands on a native PowerShell stream PowerShellExecutor already captures
	/// as a job.log event. This proves the wiring point exists for whatever text a
	/// caller passes (the real redacting IJobLogBuffer implementation is exercised
	/// by its own tests; this recorder proves nothing bypasses that capture path).
	/// </summary>
	[Fact]
	public async Task MessageText_ReachesJobLog_ThroughTheCapturedStreamOnly()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-LoggingCallerStubWithSecret",
				Parameters: new Dictionary<string, object?> { ["Secret"] = "quote\"and\\slash-token" }),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		(string, Guid?, Guid?, string) match = Assert.Single(
			_buffer.Events, entry => entry.Payload.Contains("connecting with token", StringComparison.Ordinal));
		using JsonDocument document = JsonDocument.Parse(match.Item4);
		Assert.Equal("[LoggingCallerStub] connecting with token quote\"and\\slash-token", Line(document));
	}
}
