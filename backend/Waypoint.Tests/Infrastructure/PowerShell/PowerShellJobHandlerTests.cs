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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// The handler's mapping contract against the real in-process engine and stub module
/// (no Postgres): payload -&gt; typed invocation, result -&gt; outcome kind, auth-marker
/// classification, and note redaction (jobs.note is a sink too).
/// </summary>
public sealed class PowerShellJobHandlerTests : IDisposable
{
	private sealed class NullLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointStubModule", "WaypointStubModule.psm1");

	private readonly InPlaySecretRedactor _redactor = new();
	private WaypointRunspacePool _pool = null!;

	private PowerShellJobHandler CreateHandler()
	{
		PowerShellOptions options = new() { MaxRunspaces = 1, StopGracePeriod = TimeSpan.FromMilliseconds(500) };
		options.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, new NullLogBuffer(), wrapped, NullLogger<PowerShellExecutor>.Instance);
		return new PowerShellJobHandler("discover", executor, _redactor, wrapped);
	}

	public void Dispose() => _pool?.Dispose();

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "discover", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	[Fact]
	public async Task AValidPayload_ExecutesAndSucceeds()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Get-StubEcho","parameters":{"Value":"hello"}}"""), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Null(outcome.Note);
	}

	[Fact]
	public async Task NonTerminatingErrors_SucceedWithANoteToTheLog()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Write-StubStreams"}"""), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Contains("non-terminating", outcome.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ATerminatingFailure_MapsToFailed_WithTheReason()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Invoke-StubFailure","parameters":{"Message":"invented ordinary breakage"}}"""),
			CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("invented ordinary breakage", outcome.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnAuthMarkerInTheFailure_MapsToAuthFailed()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Invoke-StubFailure","parameters":{"Message":"401 Unauthorized (invented depot reply)"}}"""),
			CancellationToken.None);
		Assert.Equal(JobOutcomeKind.AuthFailed, outcome.Kind);
	}

	/// <summary>#162 true positives: a bare digit-run marker ("401"/"403") must still
	/// trip auth-failed when it appears as a standalone token, including HTTP-status
	/// shapes that aren't the exact "401 Unauthorized" wording already covered above.</summary>
	[Theory]
	[InlineData("HTTP 401 returned by invented depot endpoint")]
	[InlineData("request failed (401)")]
	[InlineData("status=401 from invented gateway")]
	[InlineData("depot responded 403: Forbidden")]
	public async Task AStandaloneDigitMarker_StillMapsToAuthFailed(string message)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor($$$"""{"command":"Invoke-StubFailure","parameters":{"Message":"{{{message}}}"}}"""),
			CancellationToken.None);
		Assert.Equal(JobOutcomeKind.AuthFailed, outcome.Kind);
	}

	/// <summary>#162: bare digit-run markers ("401"/"403") must NOT fire when those three
	/// digits merely appear inside an unrelated identifier -- a GUID fragment, a byte
	/// count, a port, or a padded id. Before the fix, plain substring matching classified
	/// these ordinary, deterministic failures as auth-failed on every attempt, and three
	/// consecutive misclassifications durably three-strike a healthy credential.</summary>
	[Theory]
	[InlineData("downloaded 40123 bytes before invented connection reset")]
	[InlineData("object 4a401b99-0000-4000-8000-000000000000 not found (invented guid)")]
	[InlineData("connect to invented-host:14013 refused")]
	[InlineData("artifact size 4013 mismatch (invented manifest)")]
	[InlineData("record id-403321 already exists (invented conflict)")]
	[InlineData("checksum value40199 did not match (invented digest)")]
	[InlineData("timestamp 1754016401 outside invented retention window")]
	public async Task ADigitRunInsideAnUnrelatedIdentifier_DoesNotMapToAuthFailed(string message)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor($$$"""{"command":"Invoke-StubFailure","parameters":{"Message":"{{{message}}}"}}"""),
			CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	/// <summary>jobs.note is a sink (security.md control 1): a failure message that
	/// embeds an in-play secret reaches the outcome redacted.</summary>
	[Fact]
	public async Task ASecretInTheFailureMessage_IsRedactedFromTheNote()
	{
		PowerShellJobHandler handler = CreateHandler();
		const string canary = "invented-note-canary-4242";
		using IDisposable tracked = _redactor.Track(canary);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($$$"""{"command":"Invoke-StubFailure","parameters":{"Message":"rejected token {{{canary}}}"}}"""),
			CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.DoesNotContain(canary, outcome.Note, StringComparison.Ordinal);
		Assert.Contains("[REDACTED]", outcome.Note, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("not json at all")]
	[InlineData("""{"kind":"command"}""")]
	public async Task AnInvalidPayload_FailsWithAClearNote(string payload)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(ContextFor(payload), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("not a valid PowerShell invocation", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>#163: a present-but-malformed 'timeoutSeconds' must fail fast rather
	/// than silently falling back to the 30-minute default -- a payload author who
	/// mistypes the field deserves a diagnosable error, not a job with the wrong bound.</summary>
	[Theory]
	[InlineData("""{"command":"Get-StubEcho","timeoutSeconds":"30"}""")]
	[InlineData("""{"command":"Get-StubEcho","timeoutSeconds":0}""")]
	[InlineData("""{"command":"Get-StubEcho","timeoutSeconds":-5}""")]
	[InlineData("""{"command":"Get-StubEcho","timeoutSeconds":1.5}""")]
	[InlineData("""{"command":"Get-StubEcho","timeoutSeconds":99999999999}""")]
	public async Task AMalformedTimeoutSeconds_FailsWithAClearNoteNamingTheField(string payload)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(ContextFor(payload), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("not a valid PowerShell invocation", outcome.Note, StringComparison.Ordinal);
		Assert.Contains("timeoutSeconds", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>#163: an absent 'timeoutSeconds' is legal (the executor's default
	/// applies) -- only a present-but-invalid value should fail. This pins the happy
	/// path so the strict check above doesn't regress into rejecting omission.</summary>
	[Fact]
	public async Task AnAbsentTimeoutSeconds_StillAppliesTheDefault()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Get-StubEcho","parameters":{"Value":"hello"}}"""), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
	}

	/// <summary>#163: an unrecognized 'kind' (typo or future value) must fail fast
	/// rather than silently falling back to Command and invoking the text as a
	/// command name.</summary>
	[Theory]
	[InlineData("""{"command":"Get-StubEcho","kind":"scirpt"}""")]
	[InlineData("""{"command":"Get-StubEcho","kind":"unknown-future-kind"}""")]
	[InlineData("""{"command":"Get-StubEcho","kind":123}""")]
	public async Task AnUnknownKind_FailsWithAClearNoteNamingTheField(string payload)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(ContextFor(payload), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("not a valid PowerShell invocation", outcome.Note, StringComparison.Ordinal);
		Assert.Contains("kind", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>#163: the legal 'kind' values (both explicit "command" and "script")
	/// plus a valid positive 'timeoutSeconds' must still take the happy path.</summary>
	[Theory]
	[InlineData("""{"command":"Get-StubEcho","kind":"command","parameters":{"Value":"hello"},"timeoutSeconds":30}""")]
	[InlineData("""{"command":"Get-StubEcho -Value 'hello'","kind":"script","timeoutSeconds":30}""")]
	public async Task AValidKindAndTimeout_TakesTheHappyPath(string payload)
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(ContextFor(payload), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
	}

	[Fact]
	public async Task ATimedOutInvocation_MapsToFailed_NotAuthFailed()
	{
		JobExecutionOutcome outcome = await CreateHandler().ExecuteAsync(
			ContextFor("""{"command":"Invoke-StubHang","parameters":{"Seconds":30},"timeoutSeconds":1}"""),
			CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("timeout", outcome.Note, StringComparison.OrdinalIgnoreCase);
	}
}
