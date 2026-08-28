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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Xunit;
using RealPowerShellExecutor = Waypoint.Infrastructure.PowerShell.PowerShellExecutor;
using RealWaypointRunspacePool = Waypoint.Infrastructure.PowerShell.WaypointRunspacePool;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1016 (epic #726), owner decision 2026-08-28: unit coverage of
/// <see cref="ContentCheckJobHandler.ExecuteAsync"/> -- the fanned-out chunk job that now
/// runs the bounded per-leaf `inspec check` pass (<c>Get-WaypointComplianceContentEntries</c>,
/// unchanged command/parameters/timeout math from issue #993/#989) that used to live
/// directly inside <c>ContentPullJobHandler</c>'s phase-2 loop. This handler durably
/// records each parsed profile's content + check outcome and replaces that profile's
/// controls; it never promotes anything itself (<c>ContentPullReconcileServiceTests</c>
/// covers that). <see cref="Execute_RealExecutor_FixtureContentTree_RecordsCheckedProfile"/>
/// is the real-executor equivalence proof (issue #972/#984 lineage): a real content tree
/// + a real bounded `inspec check` (invented stub binary) flows through the REAL
/// <see cref="RealPowerShellExecutor"/> end to end and yields exactly the same
/// <c>RawYaml</c>/check-outcome evidence the pre-#1016 single-pass handler's own
/// equivalent test proved.
///
/// <see cref="InspecCheckPathMutationCollection"/> serializes the real-executor test
/// against <c>InspecCheckRealExecutorTests</c> -- both mutate the process-wide <c>PATH</c>
/// environment variable to resolve <c>Get-Command inspec</c> onto an invented stub, which
/// is unsafe under xUnit's default class-level parallelism without a shared collection.
/// </summary>
[Collection("InspecCheckPathMutation")]
public sealed class ContentCheckJobHandlerTests
{
	private const string ValidVCenterManifest = """
		name: vsphere-8-vcenter-stig-baseline
		title: vCenter STIG
		version: 2.3.0
		inputs:
		  - name: vcenter_host
		    type: string
		    required: true
		""";

	// --- fakes -------------------------------------------------------------------

	private sealed class FakePowerShellExecutor : IPowerShellExecutor
	{
		private readonly PowerShellExecutionResult _result;

		public FakePowerShellExecutor(PowerShellExecutionResult result) => _result = result;

		public List<PowerShellRequest> Requests { get; } = [];

		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(_result);
		}
	}

	private sealed class FakeCheckFanOutRepository : IContentPullCheckFanOutRepository
	{
		private readonly Dictionary<Guid, ContentPullCheckFanOut> _byCheckJobId = new();

		public List<(Guid CheckJobId, ContentCheckResultRecord Result)> RecordedResults { get; } = [];

		public void AddFanOut(Guid checkJobId, string sourceCommit, IReadOnlyList<ContentCheckProfileDirectory> chunk) =>
			_byCheckJobId[checkJobId] = new ContentPullCheckFanOut(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), checkJobId, sourceCommit, chunk, "pending");

		public Task RecordFanOutAsync(Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit, IReadOnlyList<ContentCheckProfileDirectory> profileDirectories, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task RecordEmptyFanOutAsync(Guid runId, Guid contentPullJobId, string sourceCommit, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ContentPullCheckFanOut?> GetFanOutForCheckJobAsync(Guid checkJobId, CancellationToken cancellationToken) =>
			Task.FromResult(_byCheckJobId.TryGetValue(checkJobId, out ContentPullCheckFanOut? fanOut) ? fanOut : null);

		public Task RecordCheckResultAsync(Guid checkJobId, ContentCheckResultRecord result, CancellationToken cancellationToken)
		{
			RecordedResults.Add((checkJobId, result));
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<Guid>> ListPendingReconcileContentPullJobIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<ContentPullCheckFanOut>> ListFanOutsForContentPullJobAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<ContentPullCheckReconcileReadiness> GetReconcileReadinessAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<ContentCheckResultRecord>> ListCheckResultsAsync(IReadOnlyList<Guid> checkJobIds, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task MarkReconciledAsync(Guid contentPullJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class FakeProfileRepository : IProfileRepository
	{
		private readonly List<Profile> _profiles;

		public FakeProfileRepository(IEnumerable<(string ProfileKey, Guid Id)> profiles) =>
			_profiles = [.. profiles.Select(p => new Profile(p.Id, p.ProfileKey, p.ProfileKey, null, "c1", ProfileStates.Current, DateTimeOffset.UtcNow))];

		public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Profile>>(_profiles);

		public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class FakeProfileControlRepository : IProfileControlRepository
	{
		public Dictionary<Guid, IReadOnlyList<ProfileControlUpsert>> ReplacedByProfileId { get; } = [];

		public Task<IReadOnlyList<ProfileControl>> ListByProfileAsync(Guid profileId, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task ReplaceForProfileAsync(Guid profileId, IReadOnlyList<ProfileControlUpsert> controls, CancellationToken cancellationToken)
		{
			ReplacedByProfileId[profileId] = controls;
			return Task.CompletedTask;
		}
	}

	// --- helpers -------------------------------------------------------------------

	private static PSObject ContentEntryObject(
		string profileKey, string? rawYaml, bool hasControlsDirectory, bool inspecCheckRan, bool inspecCheckPassed, PSObject[]? controls = null, params string[] controlFileNames)
	{
		PSObject entry = new();
		entry.Properties.Add(new PSNoteProperty("ProfileKey", profileKey));
		entry.Properties.Add(new PSNoteProperty("RawYaml", rawYaml));
		entry.Properties.Add(new PSNoteProperty("HasControlsDirectory", hasControlsDirectory));
		entry.Properties.Add(new PSNoteProperty("HasFilesDirectory", false));
		entry.Properties.Add(new PSNoteProperty("ControlFileNames", controlFileNames));
		entry.Properties.Add(new PSNoteProperty("Controls", controls ?? []));
		entry.Properties.Add(new PSNoteProperty("InspecCheckRan", inspecCheckRan));
		entry.Properties.Add(new PSNoteProperty("InspecCheckPassed", inspecCheckPassed));
		entry.Properties.Add(new PSNoteProperty("InspecCheckDetail", inspecCheckRan && !inspecCheckPassed ? "inspec check exited non-zero (invented fixture detail)" : null));
		return entry;
	}

	private static PSObject ControlObject(string? controlId, string? title, string? severity)
	{
		PSObject control = new();
		control.Properties.Add(new PSNoteProperty("ControlId", controlId));
		control.Properties.Add(new PSNoteProperty("Title", title));
		control.Properties.Add(new PSNoteProperty("Severity", severity));
		return control;
	}

	private static PowerShellExecutionResult Ok(params object?[] output) =>
		new(Succeeded: true, output, HadErrors: false, TimedOut: false, FailureReason: null, NativeExitCode: null);

	private static PowerShellExecutionResult Fail(string? reason) =>
		new(Succeeded: false, [], HadErrors: true, TimedOut: false, FailureReason: reason, NativeExitCode: 1);

	// --- tests -----------------------------------------------------------------

	[Fact]
	public async Task Execute_NoFanOutRowRecorded_FailsHonestly()
	{
		FakePowerShellExecutor executor = new(Ok());
		FakeCheckFanOutRepository checkFanOut = new();
		FakeProfileRepository profiles = new([]);
		FakeProfileControlRepository profileControls = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions());
		ContentCheckJobHandler handler = new(executor, checkFanOut, profiles, profileControls, options);

		ClaimedJob job = new(Guid.NewGuid(), Guid.NewGuid(), "content-check", null, null, null, 1, "{}", 0, 1);
		JobExecutionContext context = new(job, "worker-1", new NoOpEventPublisher(), new NoOpJobRunnerRepository(), JobShape.Simple);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("No content_pull_checks row", outcome.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_EntriesInvocationFails_ReturnsFailed_RecordsNothing()
	{
		FakePowerShellExecutor executor = new(Fail("inspec check timed out (invented fixture reason)"));
		FakeCheckFanOutRepository checkFanOut = new();
		Guid jobId = Guid.NewGuid();
		checkFanOut.AddFanOut(jobId, "commitX", [new ContentCheckProfileDirectory("p0", "/invented/p0")]);
		FakeProfileRepository profiles = new([]);
		FakeProfileControlRepository profileControls = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions());
		ContentCheckJobHandler handler = new(executor, checkFanOut, profiles, profileControls, options);

		ClaimedJob job = new(jobId, Guid.NewGuid(), "content-check", null, null, null, 1, "{}", 0, 1);
		JobExecutionContext context = new(job, "worker-1", new NoOpEventPublisher(), new NoOpJobRunnerRepository(), JobShape.Simple);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("invented fixture reason", outcome.Note, StringComparison.Ordinal);
		Assert.Empty(checkFanOut.RecordedResults);
	}

	[Fact]
	public async Task Execute_Success_RecordsCheckResultAndReplacesControls_PerProfile()
	{
		FakePowerShellExecutor executor = new(Ok(
			ContentEntryObject("profile-a", ValidVCenterManifest, hasControlsDirectory: true, inspecCheckRan: true, inspecCheckPassed: true,
				controls: [ControlObject("V-1001", "First control", "medium")], "control_1001.rb"),
			ContentEntryObject("profile-b", null, hasControlsDirectory: true, inspecCheckRan: false, inspecCheckPassed: false,
				controls: [ControlObject("V-2001", "Other profile's control", "low")], "control_2001.rb")));

		Guid jobId = Guid.NewGuid();
		FakeCheckFanOutRepository checkFanOut = new();
		checkFanOut.AddFanOut(jobId, "commitC", [new ContentCheckProfileDirectory("profile-a", "/invented/profile-a"), new ContentCheckProfileDirectory("profile-b", "/invented/profile-b")]);

		Guid profileAId = Guid.NewGuid();
		Guid profileBId = Guid.NewGuid();
		FakeProfileRepository profiles = new([("profile-a", profileAId), ("profile-b", profileBId)]);
		FakeProfileControlRepository profileControls = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions());
		ContentCheckJobHandler handler = new(executor, checkFanOut, profiles, profileControls, options);

		ClaimedJob job = new(jobId, Guid.NewGuid(), "content-check", null, null, null, 1, "{}", 0, 1);
		JobExecutionContext context = new(job, "worker-1", new NoOpEventPublisher(), new NoOpJobRunnerRepository(), JobShape.Simple);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(2, checkFanOut.RecordedResults.Count);

		(Guid CheckJobId, ContentCheckResultRecord Result) recordedA = Assert.Single(checkFanOut.RecordedResults, r => r.Result.ProfileKey == "profile-a");
		Assert.Equal(jobId, recordedA.CheckJobId);
		Assert.Equal(ValidVCenterManifest, recordedA.Result.RawYaml);
		Assert.True(recordedA.Result.InspecCheckRan);
		Assert.True(recordedA.Result.InspecCheckPassed);

		Assert.Equal(2, profileControls.ReplacedByProfileId.Count);
		ProfileControlUpsert controlA = Assert.Single(profileControls.ReplacedByProfileId[profileAId]);
		Assert.Equal("V-1001", controlA.ControlId);
	}

	[Fact]
	public async Task Execute_ProfileNotYetInInventory_SkipsControlReplace_StillRecordsResult()
	{
		// A profile the fan-out chunk names but that has not yet appeared in the
		// inventory (a defensive edge case -- ContentPullJobHandler always replaces
		// profiles before fanning out, so this should not happen in practice) must not
		// throw; the check result is still recorded honestly.
		FakePowerShellExecutor executor = new(Ok(
			ContentEntryObject("unknown-profile", ValidVCenterManifest, hasControlsDirectory: true, inspecCheckRan: true, inspecCheckPassed: true)));

		Guid jobId = Guid.NewGuid();
		FakeCheckFanOutRepository checkFanOut = new();
		checkFanOut.AddFanOut(jobId, "commitD", [new ContentCheckProfileDirectory("unknown-profile", "/invented/unknown-profile")]);
		FakeProfileRepository profiles = new([]);
		FakeProfileControlRepository profileControls = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions());
		ContentCheckJobHandler handler = new(executor, checkFanOut, profiles, profileControls, options);

		ClaimedJob job = new(jobId, Guid.NewGuid(), "content-check", null, null, null, 1, "{}", 0, 1);
		JobExecutionContext context = new(job, "worker-1", new NoOpEventPublisher(), new NoOpJobRunnerRepository(), JobShape.Simple);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Single(checkFanOut.RecordedResults);
		Assert.Empty(profileControls.ReplacedByProfileId);
	}

	// --- real-executor equivalence proof (issue #972/#984 lineage) -----------------

	/// <summary>
	/// Issue #1016 equivalence proof: the same invented fixture content tree the
	/// pre-#1016 <c>ContentPullJobHandler</c> real-executor test used flows through the
	/// REAL <see cref="RealPowerShellExecutor"/> and a real bounded `inspec check`
	/// (invented stub binary) via <see cref="ContentCheckJobHandler"/> directly (no sync
	/// stub needed -- this handler never calls the sync command) and yields the same
	/// non-empty <c>RawYaml</c>/passing-check evidence the old single-pass handler
	/// proved end to end.
	/// </summary>
	[Fact]
	public async Task Execute_RealExecutor_FixtureContentTree_RecordsCheckedProfile()
	{
		string fixtureRoot = Directory.CreateTempSubdirectory("wp-1016-content-tree").FullName;
		string stubInspecDir = Directory.CreateTempSubdirectory("wp-1016-inspec-stub").FullName;
		string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		try
		{
			string profileDir = Path.Combine(fixtureRoot, "vsphere", "8.0.3", "v2r3-stig", "inspec", "baseline", "vcenter");
			Directory.CreateDirectory(profileDir);
			Directory.CreateDirectory(Path.Combine(profileDir, "controls"));
			await File.WriteAllTextAsync(Path.Combine(profileDir, "inspec.yml"), ValidVCenterManifest);
			await File.WriteAllTextAsync(
				Path.Combine(profileDir, "controls", "vcenter_control.rb"),
				"control 'V-000001' do\n  title 'Invented fixture control'\n  impact 0.7\nend\n");

			WriteStubInspec(stubInspecDir, exitCode: 0);
			Environment.SetEnvironmentVariable("PATH", stubInspecDir + Path.PathSeparator + originalPath);

			string realModulePath = Path.Combine(
				AppContext.BaseDirectory, "PowerShell", "Modules", "WaypointComplianceContent", "WaypointComplianceContent.psm1");
			Assert.True(File.Exists(realModulePath), $"real module not found at '{realModulePath}' -- Waypoint.Infrastructure.Execution's PowerShell\\Modules content did not copy into the test output.");

			PowerShellOptions psOptions = new()
			{
				MaxRunspaces = 1,
				DefaultInvocationTimeout = TimeSpan.FromMinutes(1),
				StopGracePeriod = TimeSpan.FromSeconds(2),
			};
			psOptions.ModulePreloadPaths.Add(realModulePath);
			IOptions<PowerShellOptions> wrappedOptions = Options.Create(psOptions);

			using RealWaypointRunspacePool pool = new(wrappedOptions, NullLogger<RealWaypointRunspacePool>.Instance);
			RecordingJobLogBuffer logBuffer = new();
			RealPowerShellExecutor realExecutor = new(pool, logBuffer, wrappedOptions, NullLogger<RealPowerShellExecutor>.Instance);

			Guid jobId = Guid.NewGuid();
			const string profileKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter";
			FakeCheckFanOutRepository checkFanOut = new();
			checkFanOut.AddFanOut(jobId, "invented0000000000000000000000000000abcd", [new ContentCheckProfileDirectory(profileKey, profileDir)]);

			Guid profileId = Guid.NewGuid();
			FakeProfileRepository profiles = new([(profileKey, profileId)]);
			FakeProfileControlRepository profileControls = new();
			IOptions<ComplianceContentOptions> contentOptions = Options.Create(new ComplianceContentOptions { ContentPath = fixtureRoot });

			ContentCheckJobHandler handler = new(realExecutor, checkFanOut, profiles, profileControls, contentOptions);

			ClaimedJob job = new(jobId, Guid.NewGuid(), "content-check", null, null, null, 1, "{}", 0, 1);
			JobExecutionContext context = new(job, "worker-1", new NoOpEventPublisher(), new NoOpJobRunnerRepository(), JobShape.Simple);

			JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

			Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
			(Guid CheckJobId, ContentCheckResultRecord Result) recorded = Assert.Single(checkFanOut.RecordedResults);
			Assert.Equal(profileKey, recorded.Result.ProfileKey);
			Assert.False(string.IsNullOrWhiteSpace(recorded.Result.RawYaml), "RawYaml did not survive the real executor boundary.");
			Assert.True(recorded.Result.InspecCheckRan, "inspec check did not run through the real executor -- stub binary likely not resolved on PATH.");
			Assert.True(recorded.Result.InspecCheckPassed);

			Assert.Single(profileControls.ReplacedByProfileId);
			ProfileControlUpsert control = Assert.Single(profileControls.ReplacedByProfileId[profileId]);
			Assert.Equal("V-000001", control.ControlId);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
			Directory.Delete(fixtureRoot, recursive: true);
			Directory.Delete(stubInspecDir, recursive: true);
		}
	}

	/// <summary>Invented stub "inspec" executable -- see <c>ContentPullJobHandlerTests</c>'s sibling for the full rationale; identical shape here.</summary>
	private static void WriteStubInspec(string directory, int exitCode)
	{
		string stubPath = Path.Combine(directory, "inspec");
		string script = "#!/bin/sh\n"
			+ "# Invented stub for issue #1016's real-executor equivalence test.\n"
			+ "echo '{\"controls\": []}'\n"
			+ $"exit {exitCode}\n";
		File.WriteAllText(stubPath, script.ReplaceLineEndings("\n"));

#pragma warning disable CA1416 // Linux-only test asset; this repo's CI and runners are both Linux.
		File.SetUnixFileMode(
			stubPath,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
	}

	/// <summary>Records job.log events without a real IJobLogBuffer backend.</summary>
	private sealed class RecordingJobLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private sealed class NoOpEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class NoOpJobRunnerRepository : IJobRunnerRepository
	{
		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunQueueState?>(null);
		public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => throw new NotSupportedException();
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
}
