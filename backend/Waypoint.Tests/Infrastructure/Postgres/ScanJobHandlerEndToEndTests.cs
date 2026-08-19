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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Scans;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.StigManager;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #274 (second slice of the #23 split) full-loop acceptance, through the REAL
/// loop: a <c>scan</c> job is fanned out, the dispatcher claims it, the real
/// <see cref="ScanJobHandler"/> resolves the target's vCenter credential (stored or
/// ephemeral), decrypts/takes it under job attribution, invokes the stub
/// <c>Invoke-WaypointScan</c> module in-process, persists the HDF report, and reports
/// <see cref="JobOutcomeKind.StageComplete"/> so the job rests at <c>attesting</c> --
/// while the password canary never reaches <c>job_events</c> or <c>jobs.note</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class ScanJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointScanStubModule", "WaypointScanStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-scan-key").FullName;
	private readonly string _artifactDirectory = Directory.CreateTempSubdirectory("wp-scan-artifacts").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private RunSecretStore _runSecrets = null!;
	private ConfigDocRepository _configDocs = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private ScanJobHandler _handler = null!;

	public ScanJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetScanDataAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_runSecrets = new RunSecretStore(_fixture.ConnectionString, cipher, _redactor, Options.Create(new RunSecretOptions()), NullLogger<RunSecretStore>.Instance);
		_configDocs = new ConfigDocRepository(_fixture.ConnectionString);
		_attestationSnapshots = new AttestationSnapshotRepository(_fixture.ConnectionString);

		IOptions<ScanOptions> scanOptions = Options.Create(new ScanOptions
		{
			ArtifactStorePath = _artifactDirectory,
			ProfilePath = "/invented/profile/path",
			TimeoutSeconds = 60,
			AttestationProfile = "invented-vsphere-stig",
			SafTimeoutSeconds = 30,
		});

		// Issue #311: no STIG Manager connection is ever configured in this suite (no
		// row written to stigman_connections), so ScanUploadCoordinator.UploadAsync
		// always resolves to "no connection configured" -- a non-fatal Failed outcome,
		// same as production against an unconfigured appliance. StubStigManagerUploadClient
		// is never actually invoked by these tests as a result, but is still wired for
		// completeness/parity with the real DI graph.
		StigManagerRepository stigman = new(_fixture.ConnectionString);
		ScanUploadCoordinator uploadCoordinator = new(
			stigman, new StubStigManagerUploadClient(), _secretStore, _repository, _redactor);

		_handler = new ScanJobHandler(
			executor, _secretStore, _credentials, _targets, _runSecrets, _repository, _redactor, wrappedPsOptions, scanOptions, _configDocs,
			_attestationSnapshots, uploadCoordinator);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_artifactDirectory, recursive: true);
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>
	/// The full loop with a stored credential: fan out a <c>scan</c> job for a seeded
	/// vsphere target -&gt; dispatcher claims it -&gt; the real handler decrypts the
	/// credential, invokes the stub module -&gt; HDF report lands on disk under the
	/// artifact store, keyed by job id. #275 replaced the attest/convert stub-fail
	/// with real bodies, so the job no longer rests at `attesting` -- it proceeds
	/// through attest/convert to the shape's terminal (`uploaded`) across the same
	/// live dispatcher, which is what <see cref="FullPipeline_WalksQueuedToUploaded_AcrossThreeClaimCycles"/>
	/// covers end to end; this test keeps its original focus (InSpec-stage credential
	/// handling + HDF persistence + canary non-leakage).
	/// </summary>
	[Fact]
	public async Task StoredCredential_ScanSucceeds_PersistsHdf_ReachesUploaded()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		const string canary = "invented-scan-e2e-canary-b7c3";
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync(canary);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, site_id = Guid.NewGuid() });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		Assert.True(File.Exists(hdfPath), $"expected HDF report at '{hdfPath}'.");

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	/// <summary>Predecessor constraint: InSpec exit code 100 is a completed scan, not a tool failure.</summary>
	[Fact]
	public async Task ExitCode100_IsMappedToSuccess_NotFailure()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "exit100");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-exit100-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
	}

	/// <summary>A non-auth InSpec/transport failure maps to `failed` with a log-tail job event, never a thrown exception.</summary>
	[Fact]
	public async Task InspecFailure_MapsToFailed_WithLogTailEvent()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "failure");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-failure-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.JobLog, jobIds[0]));
	}

	/// <summary>An auth-shaped InSpec failure (marker "401") classifies as `auth-failed`, feeding the ADR-0008 consecutive-failure halt.</summary>
	[Fact]
	public async Task AuthShapedFailure_MapsToAuthFailed()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "auth");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-auth-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("auth-failed", await GetJobFieldAsync(jobIds[0], "state"));
	}

	/// <summary>
	/// Issue #308 AC 1: an nsx-api target scans end to end through the same stage
	/// pipeline (InSpec -&gt; attest -&gt; convert) as vsphere -- proving the ScanJobHandler
	/// dispatch added for NSX drives Invoke-WaypointNsxScan, persists the HDF the same
	/// way, and never leaks the resolved NSX credential's password (which the stub's
	/// session-token call binds the same way the real Invoke-WaypointNsxScan does).
	/// </summary>
	[Fact]
	public async Task NsxTarget_ScanSucceeds_PersistsHdf_ReachesUploaded()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		const string canary = "invented-nsx-e2e-canary-f4a2";
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync(canary);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", ScanTargetPriority.Nsx, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		Assert.True(File.Exists(hdfPath), $"expected HDF report at '{hdfPath}'.");

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	/// <summary>
	/// Issue #308 AC 2: an NSX session-token auth failure (the stub's "auth" mode, same
	/// "401" marker the dot-sourced sibling-repository Get-NsxSessionToken's real failure path
	/// would surface) hits the same credential-halt classification as vsphere.
	/// </summary>
	[Fact]
	public async Task NsxTarget_AuthShapedFailure_MapsToAuthFailed()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "auth");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-auth-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", ScanTargetPriority.Nsx, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("auth-failed", await GetJobFieldAsync(jobIds[0], "state"));
	}

	/// <summary>
	/// Issue #308 AC 3: a mixed vsphere+nsx run fans out both target kinds under one run
	/// and the nsx-api job (ScanTargetPriority.Nsx = 1) is claimed for its FIRST (InSpec)
	/// stage before the vsphere job (ScanTargetPriority.VCenter = 3) is claimed at all --
	/// proving the dispatcher's `ORDER BY priority, created_at` claim ordering, not just
	/// that both kinds can run in isolation. Proven via each job's first `job.state`
	/// event's `seq` (job_events' monotonic, gapless assignment order -- the moment
	/// ExecuteAsync's InSpec-stage claim begins) rather than a snapshot poll: with
	/// MaxConcurrency 1 claims are serialized, but this handler's own multi-stage
	/// pipeline (InSpec -&gt; attest -&gt; convert, each its own claim cycle) means a
	/// snapshot taken right as nsx reaches its terminal state can race against the very
	/// next claim tick already picking up vsphere -- the seq comparison is immune to
	/// that race because it only cares about the FIRST claim of each job, which is
	/// fully serialized by the priority-ordered claim query.
	/// </summary>
	[Fact]
	public async Task MixedVsphereAndNsxRun_ClaimsNsxBeforeVsphere_InPriorityOrder()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid vsphereTargetId, Guid vsphereCredentialId) = await SeedVsphereTargetAsync("invented-mixed-vsphere-canary");
		(Guid nsxTargetId, Guid nsxCredentialId) = await SeedNsxTargetAsync("invented-mixed-nsx-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string vspherePayload = JsonSerializer.Serialize(new { target_id = vsphereTargetId });
		string nsxPayload = JsonSerializer.Serialize(new { target_id = nsxTargetId });

		// vsphere is fanned out FIRST (created_at earlier) to prove ordering is driven by
		// priority, not insertion order -- if the dispatcher claimed by created_at alone
		// the vsphere job would go first despite its lower (3 > 1) priority.
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[
				new JobSpec("scan", ScanTargetPriority.VCenter, TargetId: vsphereTargetId, CredentialId: vsphereCredentialId, Payload: vspherePayload),
				new JobSpec("scan", ScanTargetPriority.Nsx, TargetId: nsxTargetId, CredentialId: nsxCredentialId, Payload: nsxPayload),
			],
			"tester",
			CancellationToken.None);
		Guid vsphereJobId = jobIds[0];
		Guid nsxJobId = jobIds[1];

		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 1 };
		JobDispatcherHostedService dispatcher = new(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(nsxJobId);
			await PollUntilTerminalAsync(vsphereJobId);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		long nsxFirstClaimedAtSeq = await GetFirstJobStateEventTimeAsync(nsxJobId);
		long vsphereFirstClaimedAtSeq = await GetFirstJobStateEventTimeAsync(vsphereJobId);
		Assert.True(
			nsxFirstClaimedAtSeq < vsphereFirstClaimedAtSeq,
			$"expected nsx job's first claim (seq {nsxFirstClaimedAtSeq}) before vsphere's (seq {vsphereFirstClaimedAtSeq}).");

		Assert.Equal("uploaded", await GetJobFieldAsync(nsxJobId, "state"));
		Assert.Equal("uploaded", await GetJobFieldAsync(vsphereJobId, "state"));
	}

	/// <summary>
	/// Issue #309 AC 1: an ssh (SRG) target scans to HDF and terminates at `done` -- the
	/// Srg shape's terminal (attest then stop, no convert, no CKL, no STIG Manager
	/// upload). Proves the payload's `target_kind` (RunsController.CreateScanRunAsync)
	/// actually routes this job to JobShape.Srg (JobShapes.ForJob), not JobShape.Standard
	/// -- if routing were wrong the job would rest at `attesting` (StageComplete) or fail
	/// the ADR-0012 stage switch instead of reaching `done`.
	/// </summary>
	[Fact]
	public async Task SrgTarget_ScanSucceeds_PersistsHdf_ReachesDone_NoConvertNoCkl()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		const string canary = "invented-srg-e2e-canary-c8d1";
		(Guid targetId, Guid credentialId) = await SeedSrgTargetAsync(canary);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, target_kind = TargetKinds.Ssh });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", ScanTargetPriority.Srg, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));
		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		Assert.True(File.Exists(hdfPath), $"expected HDF report at '{hdfPath}'.");

		// HDF-only: no CKL is ever produced for an SRG target (the convert stage never runs).
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.False(File.Exists(cklPath), $"expected no CKL for an SRG target at '{cklPath}'.");

		await AssertCanaryNeverLeakedAsync(canary, credentialId);
	}

	/// <summary>
	/// Issue #309 AC 2: sudo_enabled (#249's typed credential field) is read off the
	/// resolved stored credential and passed through to Invoke-WaypointSrgScan -- proven
	/// by asserting the stub's own Sudo/SudoRequiresPassword-echoing Information line
	/// (job.log) shows sudo=True for a credential seeded with sudoEnabled: true.
	/// </summary>
	[Fact]
	public async Task SrgTarget_SudoEnabledCredential_PassesSudoThroughToInvocation()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedSrgTargetAsync("invented-srg-sudo-canary", sudoEnabled: true);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, target_kind = TargetKinds.Ssh });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", ScanTargetPriority.Srg, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));

		// PowerShell Information-stream lines captured by the executor land as job.log
		// events (same mechanism the canary assertions elsewhere in this file rely on).
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%sudo=True%'", connection);
		query.Parameters.AddWithValue(jobIds[0]);
		Assert.True((long)(await query.ExecuteScalarAsync())! >= 1, "expected the stub's sudo=True Information line in job.log.");
	}

	/// <summary>Same auth-shaped-failure classification as vsphere/nsx, exercised through the SRG (ssh) path.</summary>
	[Fact]
	public async Task SrgTarget_AuthShapedFailure_MapsToAuthFailed()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "auth");
		(Guid targetId, Guid credentialId) = await SeedSrgTargetAsync("invented-srg-auth-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, target_kind = TargetKinds.Ssh });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", ScanTargetPriority.Srg, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("auth-failed", await GetJobFieldAsync(jobIds[0], "state"));
	}

	/// <summary>
	/// Issue #309 AC 3: a mixed vsphere+SRG run orders the SRG job LAST -- proven the
	/// same way <see cref="MixedVsphereAndNsxRun_ClaimsNsxBeforeVsphere_InPriorityOrder"/>
	/// proves NSX-first, via each job's first `job.state` event seq (immune to the same
	/// multi-stage-pipeline snapshot race that method's doc comment explains).
	/// ScanTargetPriority.VCenter (3) &lt; ScanTargetPriority.Srg (6, the lowest/last
	/// priority in the six-valued scheme), so vsphere claims first despite being fanned
	/// out second below.
	/// </summary>
	[Fact]
	public async Task MixedVsphereAndSrgRun_ClaimsVsphereBeforeSrg_InPriorityOrder()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid srgTargetId, Guid srgCredentialId) = await SeedSrgTargetAsync("invented-mixed-srg-canary");
		(Guid vsphereTargetId, Guid vsphereCredentialId) = await SeedVsphereTargetAsync("invented-mixed-vsphere2-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string srgPayload = JsonSerializer.Serialize(new { target_id = srgTargetId, target_kind = TargetKinds.Ssh });
		string vspherePayload = JsonSerializer.Serialize(new { target_id = vsphereTargetId });

		// SRG is fanned out FIRST (created_at earlier) to prove ordering is driven by
		// priority, not insertion order.
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[
				new JobSpec("scan", ScanTargetPriority.Srg, TargetId: srgTargetId, CredentialId: srgCredentialId, Payload: srgPayload),
				new JobSpec("scan", ScanTargetPriority.VCenter, TargetId: vsphereTargetId, CredentialId: vsphereCredentialId, Payload: vspherePayload),
			],
			"tester",
			CancellationToken.None);
		Guid srgJobId = jobIds[0];
		Guid vsphereJobId = jobIds[1];

		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 1 };
		JobDispatcherHostedService dispatcher = new(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);

		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(vsphereJobId);
			await PollUntilTerminalAsync(srgJobId);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		long vsphereFirstClaimedAtSeq = await GetFirstJobStateEventTimeAsync(vsphereJobId);
		long srgFirstClaimedAtSeq = await GetFirstJobStateEventTimeAsync(srgJobId);
		Assert.True(
			vsphereFirstClaimedAtSeq < srgFirstClaimedAtSeq,
			$"expected vsphere job's first claim (seq {vsphereFirstClaimedAtSeq}) before SRG's (seq {srgFirstClaimedAtSeq}).");

		Assert.Equal("uploaded", await GetJobFieldAsync(vsphereJobId, "state"));
		Assert.Equal("done", await GetJobFieldAsync(srgJobId, "state"));
	}

	/// <summary>
	/// ADR-0011/#276/#434: a NULL credential_id job takes its secret from the run's
	/// encrypted run_secrets row, never falling back to the target's stored credential
	/// -- proven here by seeding a target WITH a stored credential but fanning the job
	/// out with HasRunSecret, then asserting the stub saw the ad hoc username, not the
	/// stored one.
	/// </summary>
	[Fact]
	public async Task RunSecret_UsedWhenJobCredentialIdIsNull_NeverFallsBackToStoredCredential()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-unused-stored-secret", username: "stored-user@example.internal");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		const string runSecretValue = "invented-runsecret-canary-d9e1"; // gitleaks:allow — invented test canary, asserted never to reach the stub or any persistence surface
		await _runSecrets.StoreAsync(runId, new RunSecretCredential("adhoc-user@example.internal", runSecretValue), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, Payload: payload, HasRunSecret: true)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		// #275: attest/convert now run for real (no longer a stub dead-end), so the
		// pipeline reaches the shape's terminal -- this test's own focus is the
		// ad hoc run secret consumption during the InSpec stage, which already
		// happened by the time the job reaches its terminal state.
		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));

		// Unlike the predecessor single-shot in-memory cache, a run secret is NOT
		// consumed on read -- it remains decryptable across retries/lease-recovery
		// while the run is non-terminal (issue #434 AC). This run's only job just
		// reached a terminal state, which completes the run too (JobQueueRepository.TryCompleteRunAsync)
		// -- and terminal run completion is exactly what deletes the row (issue #434 AC
		// "terminal completion deletes the secret"), so it is gone now, not "still there".
		using DecryptedRunSecret? afterTerminal = await _runSecrets.DecryptAsync(runId, jobIds[0], "test-verify", CancellationToken.None);
		Assert.Null(afterTerminal);

		await AssertCanaryNeverLeakedAsync(runSecretValue, credentialId: null);
	}

	/// <summary>No credential_id AND no run_secrets row (never registered) fails auth-style, never a stored-credential fallback.</summary>
	[Fact]
	public async Task NoCredentialIdAndNoRunSecret_FailsCleanly()
	{
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-unreachable-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, Payload: payload, HasRunSecret: true)], "tester", CancellationToken.None);

		// Deliberately never call _runSecrets.StoreAsync -- simulates an expiry sweep
		// or a run whose secret was already deleted (e.g. a prior terminal completion
		// that this job's retry reopened).
		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("no run secret is available", note, StringComparison.Ordinal);
	}

	/// <summary>Issue #262: a stored vCenter credential with no username fails cleanly, never falling back to the display name.</summary>
	[Fact]
	public async Task StoredCredentialMissingUsername_FailsCleanly()
	{
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-no-username-canary", username: null);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("no username set", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// #275/#298: a fanned-out scan job walks its full multi-stage pipeline -- InSpec
	/// scan -&gt; attest -&gt; convert, each its own dispatcher claim cycle -- from `queued`
	/// all the way to the shape's terminal `uploaded` state, persisting both the HDF and
	/// CKL artifacts along the way; issue #311's non-fatal upload-failure contract
	/// (no STIG Manager connection configured in this suite) is asserted too.
	/// </summary>
	[Fact]
	public async Task FullPipeline_WalksQueuedToUploaded_AcrossThreeClaimCycles()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-full-pipeline-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		// Standard shape's terminal (ADR-0012/#298): the dispatcher forces Succeeded to
		// `uploaded` here ("artifacts ready" -- the actual HTTP upload attempt happens
		// inside the convert stage itself, issue #311, and is asserted below).
		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));

		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(hdfPath), $"expected HDF at '{hdfPath}'.");
		Assert.True(File.Exists(cklPath), $"expected CKL at '{cklPath}'.");

		// Issue #311 AC ("upload failure must NEVER fail the scan run"): this suite
		// never configures a STIG Manager connection (no stigman_connections row, no
		// site override), so ScanUploadCoordinator.UploadAsync degrades to a
		// non-fatal "failed" upload_status with an explanatory detail -- proving the
		// job still reached its terminal `uploaded` state (asserted above) despite the
		// upload itself never succeeding.
		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "upload_status"));
		Assert.Contains("No STIG Manager connection", await GetJobFieldAsync(jobIds[0], "upload_detail"));
	}

	/// <summary>
	/// #275 AC: an expired attestation is never applied (control stays Open), is
	/// logged as a WARN job.log event, and is recorded in the results output (here:
	/// jobs.note at the attest->converting requeue) so #27's sidebar can show
	/// expired-skips.
	/// </summary>
	[Fact]
	public async Task ExpiredAttestation_IsNotApplied_LogsWarn_AndRecordsInNote()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-expired-attest-canary");

		string profile = "invented-vsphere-stig";
		Guid docId = Guid.NewGuid();
		await _configDocs.SaveAsync(
			docId, ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, targetId, "tester",
			"status: Not_A_Finding\njustification: lapsed waiver\nexpires: 2020-01-01\n", CancellationToken.None);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand warnQuery = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%Warning%' AND payload::text LIKE '%expired%'",
			connection);
		warnQuery.Parameters.AddWithValue(jobIds[0]);
		Assert.True((long)(await warnQuery.ExecuteScalarAsync())! >= 1);

		// jobs.note is overwritten by each subsequent AdvanceAsync/RequeueAtStageAsync
		// call -- the earliest place the expired-skip fact is still legible after the
		// convert stage ran is the job.log WARN asserted above; also confirm the
		// attest->converting requeue note (captured mid-pipeline via job_events'
		// job.state payload, which carries the note at the time of that transition)
		// recorded the expired-skip, not just the WARN line.
		await using NpgsqlCommand stateEventQuery = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.state' AND payload::text LIKE '%expired-skipped%'", connection);
		stateEventQuery.Parameters.AddWithValue(jobIds[0]);
		Assert.True((long)(await stateEventQuery.ExecuteScalarAsync())! >= 1);

		// Issue #306: the same expiry the WARN/job.state events above describe must also
		// land in the persisted at-scan-time ledger -- applied=false, expired=true. No
		// less-specific layer has a doc either, so ConfigDocResolver.Resolve falls
		// through to "no doc at all" (DocId null is its documented contract for that
		// case -- ConfigDocResolution's doc comment) even though a doc DID exist at the
		// target layer; it just lapsed.
		AttestationSnapshot snapshot = Assert.Single(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));
		Assert.Equal(jobIds[0], snapshot.JobId);
		Assert.Equal(targetId, snapshot.TargetId);
		Assert.Null(snapshot.DocId);
		Assert.False(snapshot.Applied);
		Assert.True(snapshot.Expired);
		_ = docId;
	}

	/// <summary>
	/// Issue #306's write-path AC: the attest stage persists a real at-scan-time
	/// snapshot for an APPLIED (non-expired) waiver -- doc id/version/author/timestamp
	/// all recorded, <c>applied_at</c> a genuine scan-time stamp (asserted as "close to
	/// now" rather than any fixed value, since the real dispatcher clock produced it).
	/// </summary>
	[Fact]
	public async Task AppliedAttestation_PersistsAtScanTimeSnapshot()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-applied-attest-canary");

		string profile = "invented-vsphere-stig";
		Guid docId = Guid.NewGuid();
		(ConfigDocSaveOutcome outcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			docId, ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, targetId, "tester",
			"status: Not_A_Finding\njustification: invented waiver, still valid\nexpires: 2099-01-01\n", CancellationToken.None);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		DateTimeOffset beforeAttest = DateTimeOffset.UtcNow;
		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));

		AttestationSnapshot snapshot = Assert.Single(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));
		Assert.Equal(runId, snapshot.RunId);
		Assert.Equal(jobIds[0], snapshot.JobId);
		Assert.Equal(targetId, snapshot.TargetId);
		Assert.Equal(profile, snapshot.Profile);
		Assert.Equal($"target:{targetId}", snapshot.Scope);
		Assert.Equal(doc!.Id, snapshot.DocId);
		Assert.Equal(version!.Version, snapshot.DocVersion);
		Assert.Equal("tester", snapshot.DocAuthor);
		Assert.True(snapshot.Applied);
		Assert.False(snapshot.Expired);
		Assert.InRange(snapshot.AppliedAt, beforeAttest.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
	}

	/// <summary>
	/// The integrity property issue #306 exists to guarantee: reading the run's ledger
	/// back through <c>GET /runs/{id}/attestations-applied</c>-equivalent storage access
	/// after the config-doc has been edited must still show the ORIGINAL at-scan-time
	/// facts, not the edited ones. <see cref="RunArtifactsApiTests"/> proves this at the
	/// HTTP layer; this proves the same property one layer down, against the real
	/// dispatcher-driven write this whole suite exercises.
	/// </summary>
	[Fact]
	public async Task RecordedSnapshot_SurvivesConfigDocEditedAfterTheRun()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-post-edit-canary");

		string profile = "invented-vsphere-stig";
		Guid docId = Guid.NewGuid();
		await _configDocs.SaveAsync(
			docId, ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, targetId, "tester",
			"status: Not_A_Finding\njustification: original waiver\nexpires: 2099-01-01\n", CancellationToken.None);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		AttestationSnapshot before = Assert.Single(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));

		// Edit the same slot AFTER the run: a new version, lapsed expiry, different text.
		await _configDocs.SaveAsync(
			docId, ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, targetId, "tester",
			"status: Not_A_Finding\njustification: EDITED AFTER THE RUN\nexpires: 2020-01-01\n", CancellationToken.None);

		AttestationSnapshot after = Assert.Single(await _attestationSnapshots.ListForRunAsync(runId, CancellationToken.None));
		Assert.Equal(before.DocVersion, after.DocVersion);
		Assert.Equal(before.DocVersionCreatedAt, after.DocVersionCreatedAt);
		Assert.Equal(before.Applied, after.Applied);
		Assert.Equal(before.Expired, after.Expired);
		Assert.Equal(before.AppliedAt, after.AppliedAt);
	}

	/// <summary>
	/// Issue #304: the attest stage's resolved-attestation temp file
	/// (<c>waypoint-attest-{jobid}.yml</c>, <c>ScanJobHandler.ExecuteAttestStageAsync</c>)
	/// must be owner-only (0600) on disk for its whole on-disk window, not just
	/// eventually -- the shared system temp dir's umask default is typically 0644
	/// (world-readable). The stub module's <c>Invoke-WaypointAttest</c> stats the file
	/// the instant it receives <c>AttestTemplatePath</c> (mirroring where the real
	/// module's <c>saf attest apply</c> would read it) and reports the Unix mode via
	/// <c>$env:WAYPOINT_ATTEST_TEMPFILE_MODE_PATH</c>, read here BEFORE the handler's
	/// `finally` block deletes the temp file.
	/// </summary>
	[Fact]
	public async Task AttestTempFile_IsCreatedOwnerOnly_0600()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // Unix file modes are only meaningful on Linux/macOS; CI runs Linux.
		}

		string modeReportPath = Path.Combine(_artifactDirectory, "attest-tempfile-mode.txt");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_TEMPFILE_MODE_PATH", modeReportPath);
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		try
		{
			(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-attest-tempfile-mode-canary");

			string profile = "invented-vsphere-stig";
			await _configDocs.SaveAsync(
				Guid.NewGuid(), ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, targetId, "tester",
				"status: Not_A_Finding\njustification: invented non-expired waiver\nexpires: 2099-01-01\n", CancellationToken.None);

			Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
			string payload = JsonSerializer.Serialize(new { target_id = targetId });
			IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
				runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

			JobDispatcherHostedService dispatcher = CreateDispatcher();
			await dispatcher.StartAsync(CancellationToken.None);
			try
			{
				await PollUntilTerminalAsync(jobIds[0]);
			}
			finally
			{
				await dispatcher.StopAsync(CancellationToken.None);
			}

			Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
			Assert.True(File.Exists(modeReportPath), $"stub did not observe an AttestTemplatePath -- expected '{modeReportPath}' to be written.");

			string reportedMode = await File.ReadAllTextAsync(modeReportPath);
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Enum.Parse<UnixFileMode>(reportedMode));
		}
		finally
		{
			Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_TEMPFILE_MODE_PATH", null);
		}
	}

	/// <summary>A non-auth SAF attest failure maps to `failed` with a log-tail job event, never a thrown exception.</summary>
	[Fact]
	public async Task AttestFailure_MapsToFailed_WithLogTailEvent()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "failure");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-attest-failure-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.Equal("attesting", await GetJobFieldAsync(jobIds[0], "stage"));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.JobLog, jobIds[0]));
	}

	/// <summary>
	/// #275 AC / the #296 counters pattern: a job that fails at `converting` keeps
	/// `stage = 'converting'` on its `failed` row (ADR-0012 point 5) -- resuming it
	/// (seeded here the same way #298's precedent seeds a mid-pipeline marker) re-runs
	/// only the convert stage, never re-running InSpec/attest.
	/// </summary>
	[Fact]
	public async Task ResumeFromConvertingAfterFailure_ReRunsOnlyConvert()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "failure");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-resume-convert-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService firstAttempt = CreateDispatcher();
		await firstAttempt.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await firstAttempt.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.Equal("converting", await GetJobFieldAsync(jobIds[0], "stage"));
		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		string attestedPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.attested.json");
		DateTime hdfWrittenAt = File.GetLastWriteTimeUtc(File.Exists(attestedPath) ? attestedPath : hdfPath);

		// Operator-facing retry (ADR-0012 point 5: "no such endpoint exists yet") --
		// simulate it directly the same way #298's precedent seeded a mid-pipeline
		// marker: move the row back to `queued`, keeping `stage = 'converting'` intact.
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand requeue = new(
				"UPDATE jobs SET state = 'queued', claimed_by = NULL, lease_expires_at = NULL, heartbeat_at = NULL WHERE id = $1", connection);
			requeue.Parameters.AddWithValue(jobIds[0]);
			await requeue.ExecuteNonQueryAsync();
		}

		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		JobDispatcherHostedService secondAttempt = CreateDispatcher();
		await secondAttempt.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await secondAttempt.StopAsync(CancellationToken.None);
		}

		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath));

		// The HDF (or attested report) from the first attempt was never rewritten --
		// proof the resumed execution re-ran only convert, not InSpec/attest.
		Assert.Equal(hdfWrittenAt, File.GetLastWriteTimeUtc(File.Exists(attestedPath) ? attestedPath : hdfPath));
	}

	private async Task<(Guid TargetId, Guid CredentialId)> SeedVsphereTargetAsync(string secretValue, string? username = "administrator@example.internal")
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-scan-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, username))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (targetId!.Value, credentialId);
	}

	private async Task<(Guid TargetId, Guid CredentialId)> SeedNsxTargetAsync(
		string secretValue, Guid? siteId = null, string? username = "admin@example.internal")
	{
		Guid resolvedSiteId = siteId ?? (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-nsx-{Guid.NewGuid():N}@example.internal", CredentialTypes.Nsx, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, username))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "nsxmgr-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			resolvedSiteId, TargetKinds.NsxApi, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (targetId!.Value, credentialId);
	}

	private async Task<(Guid TargetId, Guid CredentialId)> SeedSrgTargetAsync(
		string secretValue, Guid? siteId = null, bool sudoEnabled = false, string? username = "svc-srg@example.internal")
	{
		Guid resolvedSiteId = siteId ?? (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-srg-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared, sudoEnabled, CancellationToken.None, username))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = "srg-photon-01.example.internal" });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			resolvedSiteId, TargetKinds.Ssh, $"target-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		return (targetId!.Value, credentialId);
	}

	/// <summary>
	/// security.md control 1/4 + the epic #6/#8 canary machinery, proven through THIS
	/// handler: the credential value never reaches job_events payloads, jobs.note, or
	/// the HDF report on disk. When <paramref name="credentialId"/> is non-null, also
	/// asserts the #8 decrypt-audit row carries this run's attribution.
	/// </summary>
	private async Task AssertCanaryNeverLeakedAsync(string canary, Guid? credentialId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand leaked = new(
			"SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand notes = new(
			"SELECT count(*) FROM jobs WHERE note LIKE '%' || $1 || '%'", connection))
		{
			notes.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await notes.ExecuteScalarAsync())!);
		}

		foreach (string file in Directory.EnumerateFiles(_artifactDirectory))
		{
			Assert.DoesNotContain(canary, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
		}

		if (credentialId is { } id)
		{
			await using NpgsqlCommand audited = new(
				"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id IS NOT NULL", connection);
			audited.Parameters.AddWithValue(id);
			Assert.True((long)(await audited.ExecuteScalarAsync())! >= 1);
		}
	}

	/// <summary>
	/// The <c>seq</c> (monotonic, gapless assignment order -- see migration 0001) of the
	/// EARLIEST <c>job.state</c> event for a job -- the moment its first claim began.
	/// <c>seq</c>, not <c>created_at</c>, is the ordering key: two events committed
	/// within the same wall-clock tick are still strictly seq-ordered, so this is
	/// race-free even under a fast poll interval.
	/// </summary>
	private async Task<long> GetFirstJobStateEventTimeAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT seq FROM job_events WHERE event_type = 'job.state' AND job_id = $1 ORDER BY seq ASC LIMIT 1", connection);
		query.Parameters.AddWithValue(jobId);
		object? result = await query.ExecuteScalarAsync();
		Assert.NotNull(result);
		return (long)result!;
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE event_type = $1 AND job_id = $2", connection);
		query.Parameters.AddWithValue(eventType);
		query.Parameters.AddWithValue(jobId);
		return (long)(await query.ExecuteScalarAsync())! > 0;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT COALESCE({field}::text, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT COALESCE(note, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled" or "uploaded")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}

	private async Task ResetScanDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE config_versions, config_docs, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Never-called stub (no stigman_connections row exists in this suite, so
	/// <see cref="Waypoint.Infrastructure.StigManager.StigManagerRepository.ResolveForSiteAsync"/>
	/// always returns null and <see cref="ScanUploadCoordinator"/> never reaches the
	/// network boundary) -- present only so <see cref="ScanJobHandler"/> can be
	/// constructed with the same DI shape production uses.
	/// </summary>
	private sealed class StubStigManagerUploadClient : IStigManagerUploadClient
	{
		public Task<StigManagerUploadResult> UploadCklAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");
		}

		public Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");
		}
	}
}
