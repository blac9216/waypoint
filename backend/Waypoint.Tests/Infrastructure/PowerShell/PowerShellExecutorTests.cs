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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// The #149 acceptance criteria against a real in-process SMA engine and the
/// invented stub module (no vendor code, no Postgres -- stream capture lands in an
/// in-memory <see cref="IJobLogBuffer"/> recorder): object marshaling, all five
/// streams with severities, parameter binding that cannot interpolate, terminating
/// errors, and hang containment with pool recovery.
/// </summary>
public sealed class PowerShellExecutorTests : IDisposable
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

	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointStubModule", "WaypointStubModule.psm1");

	private readonly RecordingLogBuffer _buffer = new();
	private WaypointRunspacePool _pool = null!;

	private PowerShellExecutor CreateExecutor(int maxRunspaces = 2, TimeSpan? stopGrace = null)
	{
		PowerShellOptions options = new()
		{
			MaxRunspaces = maxRunspaces,
			DefaultInvocationTimeout = TimeSpan.FromMinutes(2),
			StopGracePeriod = stopGrace ?? TimeSpan.FromSeconds(2)
		};
		options.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, _buffer, wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	public void Dispose()
	{
		_pool?.Dispose();
	}

	[Fact]
	public async Task APreloadedModuleFunction_ReturnsMarshaledObjects()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubInventory", Parameters: new Dictionary<string, object?> { ["Count"] = 3 }),
			CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal(3, result.Output.Count);

		// BaseObject unwrapping: a [pscustomobject] comes back as PSCustomObject; its
		// note properties must be readable from C# (via the PSObject property bag --
		// PSCustomObject exposes no CLR properties for the dynamic binder).
		System.Management.Automation.PSObject first = System.Management.Automation.PSObject.AsPSObject(result.Output[0]!);
		Assert.Equal("esxi-01.example.internal", first.Properties["Name"].Value?.ToString());
		Assert.Equal(1, System.Management.Automation.LanguagePrimitives.ConvertTo<int>(first.Properties["Index"].Value));
	}

	[Fact]
	public async Task AllFiveStreams_LandInJobLog_WithTheirSeverities()
	{
		PowerShellExecutor executor = CreateExecutor();
		Guid jobId = Guid.NewGuid();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Write-StubStreams", JobId: jobId), CancellationToken.None);

		Assert.True(result.Succeeded); // non-terminating error only
		Assert.True(result.HadErrors);

		var events = _buffer.Events.ToArray();
		Assert.All(events, entry => Assert.Equal(JobEventTypes.JobLog, entry.EventType));
		Assert.All(events, entry => Assert.Equal(jobId, entry.JobId));
		foreach (string severity in new[] { "information", "warning", "error", "verbose", "debug" })
		{
			Assert.Contains(events, entry =>
				entry.Payload.Contains($"\"severity\":\"{severity}\"", StringComparison.Ordinal) &&
				entry.Payload.Contains($"stub {severity}", StringComparison.Ordinal) ==
					(severity is "information" or "warning" or "verbose" or "debug") ||
				(severity == "error" && entry.Payload.Contains("stub non-terminating error line", StringComparison.Ordinal)));
		}
	}

	/// <summary>
	/// Issue #629/#618: entries in <see cref="PowerShellOptions.ModulePreloadNames"/> are
	/// genuinely imported into every pooled runspace's initial session state, through a
	/// code path separate from <see cref="PowerShellOptions.ModulePreloadPaths"/>. The
	/// live fix puts <c>VMware.PowerCLI</c> there so the full meta-module hydrates
	/// discovery objects correctly; that module cannot be relied on off-lab, so this pins
	/// the wiring with the invented stub module (given via the NAMES list) and asserts one
	/// of its exported functions is callable in a runspace whose ModulePreloadPaths was
	/// deliberately left empty -- proving the import came from ModulePreloadNames, not the
	/// paths list.
	/// </summary>
	[Fact]
	public async Task ModulePreloadNames_AreImportedIntoEveryRunspace()
	{
		PowerShellOptions options = new()
		{
			MaxRunspaces = 1,
			DefaultInvocationTimeout = TimeSpan.FromMinutes(2),
			StopGracePeriod = TimeSpan.FromSeconds(2),
		};
		// Intentionally NOT adding StubModulePath to ModulePreloadPaths: the only way
		// Get-StubEcho can resolve below is via the ModulePreloadNames import.
		options.ModulePreloadNames.Add(StubModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _buffer, wrapped, NullLogger<PowerShellExecutor>.Instance);

		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = "from-names-list" }),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal("from-names-list", Assert.Single(result.Output)?.ToString());
	}

	/// <summary>The injection-unrepresentability claim, tested from the outside: a
	/// parameter stuffed with PowerShell metacharacters round-trips byte-identical.
	/// If any code path interpolated it into script text, the subexpression would
	/// have evaluated and the echo would differ.</summary>
	[Fact]
	public async Task AParameterFullOfMetacharacters_RoundTripsUnevaluated()
	{
		PowerShellExecutor executor = CreateExecutor();
		const string hostile = "'; $(Write-Error pwned); \"`$(Get-Date)\" #";
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = hostile }),
			CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal(hostile, Assert.Single(result.Output));
		Assert.DoesNotContain(_buffer.Events, entry => entry.Payload.Contains("pwned", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ATerminatingError_FailsTheResult_WithTheMessage()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest("Invoke-StubFailure", Parameters: new Dictionary<string, object?> { ["Message"] = "invented boom" }),
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.TimedOut);
		Assert.Contains("invented boom", result.FailureReason, StringComparison.Ordinal);
	}

	/// <summary>#149's containment criterion: a pipeline that ignores Stop() is
	/// poisoned and replaced, the invocation reports TimedOut, and the pool keeps
	/// serving -- the next invocation on the same executor succeeds.</summary>
	[Fact]
	public async Task AHungPipeline_TimesOut_PoisonsItsRunspace_AndThePoolRecovers()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 1, stopGrace: TimeSpan.FromMilliseconds(500));
		PowerShellExecutionResult hung = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-StubHang",
				Parameters: new Dictionary<string, object?> { ["Seconds"] = 30 },
				Timeout: TimeSpan.FromSeconds(1)),
			CancellationToken.None);

		Assert.False(hung.Succeeded);
		Assert.True(hung.TimedOut);
		Assert.Equal(1, _pool.Health.PoisonedTotal);

		PowerShellExecutionResult next = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = "alive" }),
			CancellationToken.None);
		Assert.True(next.Succeeded);
		Assert.Equal("alive", Assert.Single(next.Output));
		Assert.True(_pool.Health.CreatedTotal >= 2);
	}

	/// <summary>Round-1 finding 1: poisoning must free the CALLER, not just the pool
	/// slot -- the hung stub sleeps 30s, so returning in a few seconds proves the
	/// wedged PowerShell was abandoned to background disposal, not awaited.</summary>
	[Fact]
	public async Task AHungPipeline_ReturnsWithinTheGraceBudget_NotTheHangDuration()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 1, stopGrace: TimeSpan.FromMilliseconds(500));
		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		PowerShellExecutionResult hung = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-StubHang",
				Parameters: new Dictionary<string, object?> { ["Seconds"] = 30 },
				Timeout: TimeSpan.FromSeconds(1)),
			CancellationToken.None);
		stopwatch.Stop();

		Assert.True(hung.TimedOut);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Timed-out invocation took {stopwatch.Elapsed.TotalSeconds:F1}s to return -- the caller waited on the hung pipeline's disposal.");
	}

	/// <summary>Round-1 finding 2: cancellation gets the same containment as a timeout --
	/// stop, grace, poison-and-abandon -- and then propagates; the caller is never
	/// parked on a Stop()-resistant pipeline, and the pool recovers.</summary>
	[Fact]
	public async Task CancellationOfAHungPipeline_PropagatesQuickly_AndPoisons()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 1, stopGrace: TimeSpan.FromMilliseconds(500));
		using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
			new PowerShellRequest("Invoke-StubHang", Parameters: new Dictionary<string, object?> { ["Seconds"] = 30 }),
			cts.Token));
		stopwatch.Stop();

		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Cancelled invocation took {stopwatch.Elapsed.TotalSeconds:F1}s to propagate.");
		Assert.Equal(1, _pool.Health.PoisonedTotal);

		PowerShellExecutionResult next = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = "alive" }),
			CancellationToken.None);
		Assert.True(next.Succeeded);
	}

	/// <summary>Round-1 finding 3: $LASTEXITCODE persists on a reused runspace -- a clean
	/// module-function call after a failed native command must not inherit its code.</summary>
	[Fact]
	public async Task AStaleNativeExitCode_DoesNotFailTheNextInvocation()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 1);
		PowerShellExecutionResult native = await executor.ExecuteAsync(
			new PowerShellRequest("& sh -c 'exit 3'", PowerShellRequestKind.Script), CancellationToken.None);
		Assert.False(native.Succeeded);
		Assert.Equal(3, native.NativeExitCode);

		PowerShellExecutionResult clean = await executor.ExecuteAsync(
			new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = "fresh" }),
			CancellationToken.None);
		Assert.True(clean.Succeeded);
		Assert.Null(clean.NativeExitCode);
		Assert.Null(clean.FailureReason);
	}

	/// <summary>#160: an abandoned pipeline's stream handlers are detached -- a record
	/// it emits after the job already reported TimedOut must not land in job.log.</summary>
	[Fact]
	public async Task AnAbandonedPipeline_CannotEmitLateEventsAfterTheResult()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 1, stopGrace: TimeSpan.FromMilliseconds(200));
		PowerShellExecutionResult hung = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-StubHangThenWrite",
				Parameters: new Dictionary<string, object?> { ["Milliseconds"] = 2500 },
				Timeout: TimeSpan.FromMilliseconds(500)),
			CancellationToken.None);
		Assert.True(hung.TimedOut);

		int eventsAtResult = _buffer.Events.Count;

		// Outlive the hang: the stub wakes at ~2.5s and writes its warning; with the
		// handlers detached, nothing new may arrive.
		await Task.Delay(TimeSpan.FromSeconds(4));
		Assert.Equal(eventsAtResult, _buffer.Events.Count);
		Assert.DoesNotContain(_buffer.Events, entry => entry.Payload.Contains("late line", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConcurrentInvocationsBeyondThePoolSize_AllComplete()
	{
		PowerShellExecutor executor = CreateExecutor(maxRunspaces: 2);
		PowerShellExecutionResult[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
			executor.ExecuteAsync(
				new PowerShellRequest("Get-StubEcho", Parameters: new Dictionary<string, object?> { ["Value"] = $"run-{index}" }),
				CancellationToken.None)));

		Assert.All(results, result => Assert.True(result.Succeeded));
		Assert.Equal(
			Enumerable.Range(0, 6).Select(index => $"run-{index}").Order(),
			results.Select(result => (string)result.Output.Single()!).Order());
		Assert.True(_pool.Health.CreatedTotal <= 2);
	}

	/// <summary>A stream message flows through the buffer exactly as the redaction seam
	/// expects: this recorder sees the serialized payload the real buffer would redact --
	/// the slice-1 tests own redaction itself; this proves the wiring point exists.</summary>
	[Fact]
	public async Task StreamMessages_ArriveAsSerializedJson_NotInterpolatedText()
	{
		PowerShellExecutor executor = CreateExecutor();
		await executor.ExecuteAsync(
			new PowerShellRequest("Write-StubSecretLeak", Parameters: new Dictionary<string, object?> { ["Secret"] = "quote\"and\\slash" }),
			CancellationToken.None);

		(string, Guid?, Guid?, string) infoEvent = Assert.Single(
			_buffer.Events, entry => entry.Payload.Contains("info leaks", StringComparison.Ordinal));
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(infoEvent.Item4);
		Assert.Equal("information", document.RootElement.GetProperty("severity").GetString());
		Assert.Equal("info leaks quote\"and\\slash", document.RootElement.GetProperty("line").GetString());
	}
}
