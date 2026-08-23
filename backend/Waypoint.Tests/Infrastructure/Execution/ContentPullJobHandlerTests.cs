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

using System.Management.Automation;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #40: unit coverage of <see cref="ContentPullJobHandler.ExecuteAsync"/> in
/// isolation, driving the handler with an in-memory fake <see cref="IPowerShellExecutor"/>
/// (the precedent <c>DownloadJobHandlerEndToEndTests</c> exercises the real executor with
/// a stub module through the whole job engine; this class instead pins the handler's own
/// branch logic without Postgres). Proves the AC's central promise -- a failed pull STILL
/// lands in pull history -- across the config-missing, invocation-failure, no-commit,
/// success, and tag-vs-branch state paths, plus the <c>ParseOutput</c>/<c>TryParseProfile</c>
/// parsing (including malformed rows that must not fail the whole pull).
/// </summary>
public sealed class ContentPullJobHandlerTests
{
	private const string RepositoryUrl = "https://git.example.internal/dod/compliance-content.git";
	private const string ContentPath = "/var/lib/waypoint/compliance-content";

	// --- fakes -----------------------------------------------------------------

	private sealed class FakePowerShellExecutor : IPowerShellExecutor
	{
		private readonly PowerShellExecutionResult _result;

		public FakePowerShellExecutor(PowerShellExecutionResult result) => _result = result;

		public PowerShellRequest? LastRequest { get; private set; }

		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(_result);
		}
	}

	private sealed record RecordedPull(
		Guid? JobId, string RefType, string RefValue, string? Commit, string Status, string? Note, string? InitiatedBy);

	private sealed class FakeContentRepository : IComplianceContentRepository
	{
		private readonly ComplianceContentConfig? _config;

		public FakeContentRepository(ComplianceContentConfig? config) => _config = config;

		public List<RecordedPull> Pulls { get; } = [];

		public Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_config);

		public Task<ComplianceContentConfig> PutConfigAsync(string repositoryUrl, string refType, string refValue, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task RecordPullAsync(
			Guid? jobId, string refType, string refValue, string? commit, string status, string? note, string? initiatedBy, CancellationToken cancellationToken)
		{
			Pulls.Add(new RecordedPull(jobId, refType, refValue, commit, status, note, initiatedBy));
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<ComplianceContentPull>> ListPullsAsync(int limit, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class FakeProfileRepository : IProfileRepository
	{
		public IReadOnlyList<ProfileUpsert>? Replaced { get; private set; }

		public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken)
		{
			Replaced = profiles;
			return Task.CompletedTask;
		}
	}

	/// <summary>Only <see cref="GetRunQueueStateAsync"/> is exercised by the handler (via ResolveActorAsync).</summary>
	private sealed class FakeJobRunnerRepository : IJobRunnerRepository
	{
		private readonly string? _initiatedBy;

		public FakeJobRunnerRepository(string? initiatedBy) => _initiatedBy = initiatedBy;

		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) =>
			Task.FromResult<RunQueueState?>(new RunQueueState("running", Paused: false, Blocked: false, BlockedReason: null, InitiatedBy: _initiatedBy));

		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class RecordingEventPublisher : IJobEventPublisher
	{
		public List<(string EventType, Guid? JobId, Guid? RunId, string Payload)> Events { get; } = [];

		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{
			Events.Add((eventType, jobId, runId, payloadJson));
			return Task.CompletedTask;
		}
	}

	// --- helpers ---------------------------------------------------------------

	private static ComplianceContentConfig Config(string refType, string refValue) =>
		new(RepositoryUrl, refType, refValue, PulledCommit: null, PulledBy: null, PulledAt: null,
			CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

	private static (ContentPullJobHandler Handler, JobExecutionContext Context, RecordingEventPublisher Events,
		FakeContentRepository Content, FakeProfileRepository Profiles) Build(
			PowerShellExecutionResult psResult,
			ComplianceContentConfig? config,
			string? initiatedBy = "admin@example.internal")
	{
		FakePowerShellExecutor executor = new(psResult);
		FakeContentRepository content = new(config);
		FakeProfileRepository profiles = new();
		FakeJobRunnerRepository jobs = new(initiatedBy);
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });

		ContentPullJobHandler handler = new(executor, content, profiles, jobs, options);

		Guid jobId = Guid.NewGuid();
		Guid runId = Guid.NewGuid();
		ClaimedJob job = new(jobId, runId, "content-pull", TargetId: null, TargetName: null, CredentialId: null,
			Priority: 5, Payload: "{}", AttemptCount: 0, MaxAttempts: 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		return (handler, context, events, content, profiles);
	}

	private static PSObject Success(string commit, params PSObject[] profiles)
	{
		PSObject root = new();
		root.Properties.Add(new PSNoteProperty("Commit", commit));
		root.Properties.Add(new PSNoteProperty("Profiles", profiles));
		return root;
	}

	private static PSObject ProfileObject(string? profileKey, string? name, string? version)
	{
		PSObject profile = new();
		profile.Properties.Add(new PSNoteProperty("ProfileKey", profileKey));
		profile.Properties.Add(new PSNoteProperty("Name", name));
		profile.Properties.Add(new PSNoteProperty("Version", version));
		return profile;
	}

	private static PowerShellExecutionResult Ok(params object?[] output) =>
		new(Succeeded: true, output, HadErrors: false, TimedOut: false, FailureReason: null, NativeExitCode: null);

	private static PowerShellExecutionResult Fail(string? reason) =>
		new(Succeeded: false, [], HadErrors: true, TimedOut: false, FailureReason: reason, NativeExitCode: 1);

	// --- tests -----------------------------------------------------------------

	[Fact]
	public async Task Execute_Success_ReplacesProfiles_RecordsSucceededPull_EmitsProgress()
	{
		PSObject output = Success(
			"deadbeefcafe",
			ProfileObject("dod-vsphere-8-esxi-stig", "vSphere 8 ESXi STIG", "1.2"),
			ProfileObject("dod-vsphere-8-vcsa-stig", "vSphere 8 vCSA STIG", "1.0"));
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		Assert.NotNull(profiles.Replaced);
		Assert.Equal(2, profiles.Replaced!.Count);
		Assert.Collection(profiles.Replaced,
			p => Assert.Equal("dod-vsphere-8-esxi-stig", p.ProfileKey),
			p => Assert.Equal("dod-vsphere-8-vcsa-stig", p.ProfileKey));
		Assert.All(profiles.Replaced, p => Assert.Equal("deadbeefcafe", p.Commit));

		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, pull.Status);
		Assert.Equal("deadbeefcafe", pull.Commit);
		Assert.Null(pull.Note);
		Assert.Equal("admin@example.internal", pull.InitiatedBy);

		(string EventType, Guid? JobId, Guid? RunId, string Payload) progress =
			Assert.Single(events.Events, e => e.EventType == JobEventTypes.RunProgress);
		Assert.Contains("deadbeefcafe", progress.Payload, StringComparison.Ordinal);
		Assert.Contains("\"profile_count\":2", progress.Payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_BranchConfig_LabelsProfilesCurrent()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles) =
			Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Branch, "main"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Current, Assert.Single(profiles.Replaced!).State);
	}

	[Fact]
	public async Task Execute_TagConfig_LabelsProfilesPinned()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles) =
			Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Tag, "v1.2.3"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Pinned, Assert.Single(profiles.Replaced!).State);
	}

	[Fact]
	public async Task Execute_MissingConfig_FailsWithoutInvokingExecutor_AndRecordsNoPull()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Ok(), config: null);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("No compliance-content repository is configured", outcome.Note, StringComparison.Ordinal);
		// No config -> no pull history row and no profile mutation (nothing was even attempted).
		Assert.Empty(content.Pulls);
		Assert.Null(profiles.Replaced);
		Assert.Empty(events.Events);
	}

	[Fact]
	public async Task Execute_ExecutorFailure_StillRecordsFailedPull_WithReason()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Fail("git checkout failed: ref not found"), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		// The AC's central promise: a failed pull STILL lands in history.
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Null(pull.Commit);
		Assert.Equal("git checkout failed: ref not found", pull.Note);
		Assert.Null(profiles.Replaced);
		Assert.DoesNotContain(events.Events, e => e.EventType == JobEventTypes.RunProgress);
	}

	[Fact]
	public async Task Execute_ExecutorFailure_WithNoReason_RecordsFallbackNote()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _) =
			Build(Fail(reason: null), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Contains("no failure reason", pull.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_NoCommitInOutput_RecordsFailedPull_AndSkipsProfileReplace()
	{
		// Executor "succeeded" but produced no PSObject carrying a Commit -> unchanged/no-commit path.
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Ok("just a string, not a PSObject"), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Contains("no commit", pull.Note, StringComparison.Ordinal);
		Assert.Null(profiles.Replaced);
		Assert.DoesNotContain(events.Events, e => e.EventType == JobEventTypes.RunProgress);
	}

	[Fact]
	public async Task Execute_BlankCommit_TreatedAsNoCommit()
	{
		PSObject output = new();
		output.Properties.Add(new PSNoteProperty("Commit", "   "));
		output.Properties.Add(new PSNoteProperty("Profiles", Array.Empty<PSObject>()));

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal(ComplianceContentPullStatuses.Failed, Assert.Single(content.Pulls).Status);
		Assert.Null(profiles.Replaced);
	}

	[Fact]
	public async Task Execute_MalformedProfileRows_AreSkipped_WithoutFailingThePull()
	{
		// A missing-key profile and a non-PSObject row must both be dropped; the valid one survives.
		PSObject output = Success(
			"commit9",
			ProfileObject(profileKey: null, "no key", "1.0"), // dropped: blank ProfileKey
			ProfileObject("good-profile", name: null, version: null)); // name defaults to key

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, FakeProfileRepository profiles) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		ProfileUpsert survivor = Assert.Single(profiles.Replaced!);
		Assert.Equal("good-profile", survivor.ProfileKey);
		Assert.Equal("good-profile", survivor.Name); // blank Name falls back to the key
		Assert.Null(survivor.Version);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, Assert.Single(content.Pulls).Status);
	}

	[Fact]
	public async Task Execute_MalformedProfileInEnumerable_NonPsObjectRowIsSkipped()
	{
		PSObject root = new();
		root.Properties.Add(new PSNoteProperty("Commit", "commitX"));
		root.Properties.Add(new PSNoteProperty("Profiles", new object?[] { "not-a-psobject", ProfileObject("kept", "Kept", "9") }));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles) =
			Build(Ok(root), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal("kept", Assert.Single(profiles.Replaced!).ProfileKey);
	}

	[Fact]
	public async Task Execute_ForwardsConfiguredParametersToExecutor()
	{
		FakePowerShellExecutor executor = new(Ok(Success("c1")));
		FakeContentRepository content = new(Config(ComplianceContentRefTypes.Tag, "v2"));
		FakeProfileRepository profiles = new();
		FakeJobRunnerRepository jobs = new("admin@example.internal");
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });
		ContentPullJobHandler handler = new(executor, content, profiles, jobs, options);

		ClaimedJob job = new(Guid.NewGuid(), Guid.NewGuid(), "content-pull", null, null, null, 5, "{}", 0, 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.NotNull(executor.LastRequest);
		Assert.Equal("Invoke-WaypointComplianceContentPull", executor.LastRequest!.Command);
		Assert.Equal(PowerShellRequestKind.Command, executor.LastRequest.Kind);
		Assert.Equal(RepositoryUrl, executor.LastRequest.Parameters!["RepositoryUrl"]);
		Assert.Equal(ComplianceContentRefTypes.Tag, executor.LastRequest.Parameters!["RefType"]);
		Assert.Equal("v2", executor.LastRequest.Parameters!["RefValue"]);
		Assert.Equal(ContentPath, executor.LastRequest.Parameters!["ContentPath"]);
	}

	[Fact]
	public async Task Execute_NullInitiatedBy_ResolvesActorToSystem()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _) =
			Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Branch, "main"), initiatedBy: null);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal("system", Assert.Single(content.Pulls).InitiatedBy);
	}

	[Fact]
	public async Task Execute_NullRunId_ResolvesActorToSystem_WithoutQueryingRunState()
	{
		FakePowerShellExecutor executor = new(Ok(Success("c1", ProfileObject("p1", "P1", null))));
		FakeContentRepository content = new(Config(ComplianceContentRefTypes.Branch, "main"));
		FakeProfileRepository profiles = new();
		FakeJobRunnerRepository jobs = new("admin@example.internal");
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });
		ContentPullJobHandler handler = new(executor, content, profiles, jobs, options);

		// A job with no run id: ResolveActorAsync short-circuits to "system" without a run-state read.
		ClaimedJob job = new(Guid.NewGuid(), RunId: null, "content-pull", null, null, null, 5, "{}", 0, 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal("system", Assert.Single(content.Pulls).InitiatedBy);
	}
}
