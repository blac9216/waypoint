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
/// Issue #1016 (epic #726), owner decision 2026-08-28: unit coverage of
/// <see cref="ContentPullJobHandler.ExecuteAsync"/>'s NARROWED contract -- phase 1 sync
/// (unchanged from issue #993) plus fan-out of one <c>content-check</c> job per chunk
/// (new). The semantic-import/promotion/staging pipeline this handler used to run
/// inline moved to <see cref="Waypoint.Infrastructure.Execution.ComplianceContent.ContentPullReconcileService"/>
/// (see <c>ContentPullReconcileServiceTests</c>) and the chunked `inspec check` pass
/// moved to <c>ContentCheckJobHandler</c> (see <c>ContentCheckJobHandlerTests</c>,
/// including the real-executor fixture equivalence proof). This class proves: config/
/// sync failure paths still record honest pull-history failures exactly as before,
/// profile-inventory replace still happens at sync time, and a successful sync fans out
/// the expected number of <c>content-check</c> jobs (respecting <c>ContentPullChunkSize</c>)
/// with the exact profile-directory chunks recorded for each -- WITHOUT recording a
/// pull-history success itself (that is reconcile's job now).
/// </summary>
public sealed class ContentPullJobHandlerTests
{
	private const string RepositoryUrl = "https://git.example.internal/dod/compliance-content.git";
	private const string ContentPath = "/var/lib/waypoint/compliance-content";

	// --- fakes -----------------------------------------------------------------

	private sealed class FakePowerShellExecutor : IPowerShellExecutor
	{
		private readonly PowerShellExecutionResult _syncResult;

		public FakePowerShellExecutor(PowerShellExecutionResult syncResult) => _syncResult = syncResult;

		public List<PowerShellRequest> Requests { get; } = [];

		public PowerShellRequest? LastSyncRequest => Requests.LastOrDefault(r => r.Command == "Sync-WaypointComplianceContentTree");

		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(_syncResult);
		}
	}

	private sealed record RecordedPull(
		Guid? JobId, string RefType, string RefValue, string? Commit, string Status, string? Note, string? InitiatedBy);

	private sealed class FakeContentRepository : IComplianceContentRepository
	{
		private readonly ComplianceContentConfig? _config;

		public FakeContentRepository(ComplianceContentConfig? config) => _config = config;

		public List<RecordedPull> Pulls { get; } = [];

		public Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken) => Task.FromResult(_config);

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
			Task.FromResult<IReadOnlyList<Profile>>([]);

		public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken)
		{
			Replaced = profiles;
			return Task.CompletedTask;
		}
	}

	/// <summary>Only <see cref="GetRunQueueStateAsync"/> and <see cref="FanOutAdditionalJobsAsync"/> are exercised by this handler.</summary>
	private sealed class FakeJobRunnerRepository : IJobRunnerRepository
	{
		private readonly string? _initiatedBy;
		private int _nextJobIdSeed;

		public FakeJobRunnerRepository(string? initiatedBy) => _initiatedBy = initiatedBy;

		public List<(Guid RunId, IReadOnlyList<JobSpec> Specs, string? CreatedBy)> FanOutCalls { get; } = [];

		/// <summary>Set to force <see cref="FanOutAdditionalJobsAsync"/> to throw, simulating a run that stopped being 'running' mid-fan-out.</summary>
		public bool ThrowOnFanOut { get; set; }

		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) =>
			Task.FromResult<RunQueueState?>(new RunQueueState("running", Paused: false, Blocked: false, BlockedReason: null, InitiatedBy: _initiatedBy));

		public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
		{
			if (ThrowOnFanOut)
			{
				throw new InvalidOperationException("run is not running (invented test fixture failure).");
			}

			FanOutCalls.Add((runId, specs, createdBy));
			List<Guid> ids = [];
			foreach (JobSpec _ in specs)
			{
				ids.Add(new Guid($"00000000-0000-0000-0000-{(++_nextJobIdSeed):D12}"));
			}

			return Task.FromResult<IReadOnlyList<Guid>>(ids);
		}

		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
	}

	private sealed class FakeCheckFanOutRepository : IContentPullCheckFanOutRepository
	{
		public List<(Guid RunId, Guid ContentPullJobId, Guid CheckJobId, string SourceCommit, IReadOnlyList<ContentCheckProfileDirectory> ProfileDirectories)> RecordedFanOuts { get; } = [];

		public Task RecordFanOutAsync(
			Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit,
			IReadOnlyList<ContentCheckProfileDirectory> profileDirectories, CancellationToken cancellationToken)
		{
			RecordedFanOuts.Add((runId, contentPullJobId, checkJobId, sourceCommit, profileDirectories));
			return Task.CompletedTask;
		}

		public Task<ContentPullCheckFanOut?> GetFanOutForCheckJobAsync(Guid checkJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task RecordCheckResultAsync(Guid checkJobId, ContentCheckResultRecord result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Guid>> ListPendingReconcileContentPullJobIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<ContentPullCheckFanOut>> ListFanOutsForContentPullJobAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<ContentPullCheckReconcileReadiness> GetReconcileReadinessAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<ContentCheckResultRecord>> ListCheckResultsAsync(IReadOnlyList<Guid> checkJobIds, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task MarkReconciledAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
		FakeContentRepository Content, FakeProfileRepository Profiles, FakeJobRunnerRepository Jobs, FakeCheckFanOutRepository CheckFanOut, Guid RunId) Build(
			PowerShellExecutionResult syncResult,
			ComplianceContentConfig? config,
			string? initiatedBy = "admin@example.internal",
			int chunkSize = 25,
			Guid? runIdOverride = null)
	{
		FakePowerShellExecutor executor = new(syncResult);
		FakeContentRepository content = new(config);
		FakeProfileRepository profiles = new();
		FakeJobRunnerRepository jobs = new(initiatedBy);
		FakeCheckFanOutRepository checkFanOut = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions
		{
			ContentPath = ContentPath,
			ContentPullChunkSize = chunkSize,
		});

		ContentPullJobHandler handler = new(executor, content, profiles, jobs, options, checkFanOut);

		Guid jobId = Guid.NewGuid();
		Guid runId = runIdOverride ?? Guid.NewGuid();
		ClaimedJob job = new(jobId, runId, "content-pull", TargetId: null, TargetName: null, CredentialId: null,
			Priority: 5, Payload: "{}", AttemptCount: 0, MaxAttempts: 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		return (handler, context, events, content, profiles, jobs, checkFanOut, runId);
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
		profile.Properties.Add(new PSNoteProperty("_ProfileDirectory", profileKey is null ? null : $"/invented/{profileKey}"));
		return profile;
	}

	private static PowerShellExecutionResult Ok(params object?[] output) =>
		new(Succeeded: true, output, HadErrors: false, TimedOut: false, FailureReason: null, NativeExitCode: null);

	private static PowerShellExecutionResult Fail(string? reason) =>
		new(Succeeded: false, [], HadErrors: true, TimedOut: false, FailureReason: reason, NativeExitCode: 1);

	// --- tests -----------------------------------------------------------------

	[Fact]
	public async Task Execute_MissingConfig_FailsWithoutInvokingExecutor_AndRecordsNoPull()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, _, _, _) =
			Build(Ok(), config: null);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(content.Pulls);
	}

	[Fact]
	public async Task Execute_ExecutorFailure_RecordsFailedPull_WithReason()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, _, _, _) =
			Build(Fail("git clone failed (invented fixture reason)"), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Contains("invented fixture reason", pull.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_NoCommitInOutput_RecordsFailedPull_AndSkipsProfileReplace()
	{
		PSObject output = new();
		output.Properties.Add(new PSNoteProperty("Commit", null));
		output.Properties.Add(new PSNoteProperty("Profiles", Array.Empty<PSObject>()));

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, FakeProfileRepository profiles, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Null(profiles.Replaced);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
	}

	[Fact]
	public async Task Execute_Success_ReplacesProfileInventory_AtSyncTime()
	{
		PSObject output = Success(
			"deadbeefcafe",
			ProfileObject("dod-vsphere-8-esxi-stig", "vSphere 8 ESXi STIG", "1.2"),
			ProfileObject("dod-vsphere-8-vcsa-stig", "vSphere 8 vCSA STIG", "1.0"));
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.NotNull(profiles.Replaced);
		Assert.Equal(2, profiles.Replaced!.Count);
	}

	[Fact]
	public async Task Execute_Success_DoesNotRecordPullHistory_ReconcileOwnsThatNow()
	{
		PSObject output = Success("deadbeefcafe", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Empty(content.Pulls);
	}

	[Fact]
	public async Task Execute_Success_FansOutOneCheckJobPerChunk()
	{
		PSObject output = Success(
			"commitFanOut",
			ProfileObject("p0", "P0", null),
			ProfileObject("p1", "P1", null),
			ProfileObject("p2", "P2", null),
			ProfileObject("p3", "P3", null),
			ProfileObject("p4", "P4", null));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, FakeJobRunnerRepository jobs, FakeCheckFanOutRepository checkFanOut, Guid runId) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"), chunkSize: 2);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		// 5 profiles / chunk size 2 -> ceil(5/2) = 3 fanned-out content-check jobs.
		Assert.Equal(3, jobs.FanOutCalls.Count);
		Assert.All(jobs.FanOutCalls, call => Assert.Equal(runId, call.RunId));
		Assert.All(jobs.FanOutCalls, call => Assert.Equal("content-check", Assert.Single(call.Specs).JobType));

		Assert.Equal(3, checkFanOut.RecordedFanOuts.Count);
		Assert.Equal(2, checkFanOut.RecordedFanOuts[0].ProfileDirectories.Count);
		Assert.Equal(2, checkFanOut.RecordedFanOuts[1].ProfileDirectories.Count);
		Assert.Single(checkFanOut.RecordedFanOuts[2].ProfileDirectories);
		Assert.All(checkFanOut.RecordedFanOuts, f => Assert.Equal("commitFanOut", f.SourceCommit));

		// Every profile is covered by exactly one chunk (no gaps, no overlaps).
		HashSet<string> coveredKeys = [.. checkFanOut.RecordedFanOuts.SelectMany(f => f.ProfileDirectories).Select(p => p.ProfileKey)];
		Assert.Equal(["p0", "p1", "p2", "p3", "p4"], coveredKeys.Order(StringComparer.Ordinal));

		Assert.Contains("3 check job(s) fanned out", outcome.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_NoProfilesDiscovered_FansOutZeroCheckJobs_StillSucceeds()
	{
		PSObject output = Success("commitEmpty");
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, FakeJobRunnerRepository jobs, FakeCheckFanOutRepository checkFanOut, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Empty(jobs.FanOutCalls);
		Assert.Empty(checkFanOut.RecordedFanOuts);
		Assert.Contains("0 check job(s) fanned out", outcome.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_EmitsFanOutProgressEvent_WithCommitAndCheckJobCount()
	{
		PSObject output = Success("commitProgress", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events, _, _, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		var progress = Assert.Single(events.Events, e => e.EventType == JobEventTypes.RunProgress);
		Assert.Contains("commitProgress", progress.Payload, StringComparison.Ordinal);
		Assert.Contains("check_job_count", progress.Payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_BranchConfig_LabelsProfilesCurrent()
	{
		PSObject output = Success("c1", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Current, profiles.Replaced![0].State);
	}

	[Fact]
	public async Task Execute_TagConfig_LabelsProfilesPinned()
	{
		PSObject output = Success("c1", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Tag, "v1.0"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Pinned, profiles.Replaced![0].State);
	}

	[Fact]
	public async Task Execute_MalformedProfileRows_AreSkipped_WithoutFailingThePull()
	{
		PSObject malformed = new();
		malformed.Properties.Add(new PSNoteProperty("ProfileKey", null));
		PSObject output = Success("c1", malformed, ProfileObject("p-good", "Good", null));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Single(profiles.Replaced!);
		Assert.Equal("p-good", profiles.Replaced![0].ProfileKey);
	}

	[Fact]
	public async Task Execute_ForwardsConfiguredParametersToSyncExecutor()
	{
		PSObject output = Success("c1");
		FakePowerShellExecutor executor = new(Ok(output));
		FakeContentRepository content = new(Config(ComplianceContentRefTypes.Branch, "release/2026"));
		FakeProfileRepository profiles = new();
		FakeJobRunnerRepository jobs = new("admin@example.internal");
		FakeCheckFanOutRepository checkFanOut = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });
		ContentPullJobHandler handler = new(executor, content, profiles, jobs, options, checkFanOut);

		ClaimedJob job = new(Guid.NewGuid(), Guid.NewGuid(), "content-pull", null, null, null, 5, "{}", 0, 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		await handler.ExecuteAsync(context, CancellationToken.None);

		PowerShellRequest? syncRequest = executor.LastSyncRequest;
		Assert.NotNull(syncRequest);
		Assert.Equal(RepositoryUrl, syncRequest!.Parameters!["RepositoryUrl"]);
		Assert.Equal(ComplianceContentRefTypes.Branch, syncRequest.Parameters["RefType"]);
		Assert.Equal("release/2026", syncRequest.Parameters["RefValue"]);
		Assert.Equal(ContentPath, syncRequest.Parameters["ContentPath"]);
	}

	[Fact]
	public async Task Execute_NullInitiatedBy_ResolvesActorToSystem()
	{
		PSObject output = Success("c1", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, FakeJobRunnerRepository jobs, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"), initiatedBy: null);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.All(jobs.FanOutCalls, call => Assert.Equal("system", call.CreatedBy));
	}

	[Fact]
	public async Task Execute_FanOutThrows_RecordsFailedPull_DoesNotPropagateUnhandled()
	{
		PSObject output = Success("c1", ProfileObject("p0", "P0", null));
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, FakeJobRunnerRepository jobs, _, _) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));
		jobs.ThrowOnFanOut = true;

		await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(context, CancellationToken.None));

		// The dispatcher (not this handler) turns an unhandled exception into a Failed
		// outcome + job.log entry (JobDispatcherHostedService's catch-all) -- this
		// handler itself does not swallow a fan-out failure into a fabricated success,
		// so pull history stays untouched at this layer (same "nothing partially
		// recorded" discipline the pre-#1016 handler had for its own atomic steps).
		Assert.Empty(content.Pulls);
	}
}
