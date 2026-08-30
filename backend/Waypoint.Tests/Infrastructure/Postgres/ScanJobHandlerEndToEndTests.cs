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
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
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
using Waypoint.Infrastructure.Runs;
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
	/// <summary>Issue #639: stands in for ComplianceContentOptions.ContentPath -- the content-pull working tree ScanJobHandler now resolves a selected profile's directory under.</summary>
	private readonly string _contentDirectory = Directory.CreateTempSubdirectory("wp-scan-content").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	/// <summary>
	/// Issue #1020's <c>PoolThrowsDuringRent_...</c> test disposes <see cref="_pool"/>
	/// itself mid-test (to force RentAsync to throw). WaypointRunspacePool.Dispose is
	/// not idempotent -- a second call faults on its already-disposed
	/// CancellationTokenSource -- so this guard is test-harness bookkeeping only,
	/// not a statement about production disposal semantics (nothing in production
	/// disposes the pool twice; DI owns it as a singleton disposed exactly once at
	/// host shutdown).
	/// </summary>
	private bool _poolDisposedByTest;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private RunSecretStore _runSecrets = null!;
	private ConfigDocRepository _configDocs = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private ScanJobHandler _handler = null!;
	private Waypoint.Infrastructure.ComplianceContent.CatalogRepository _catalog = null!;
	private Waypoint.Infrastructure.ComplianceContent.BaselineRepository _baselines = null!;
	private Waypoint.Infrastructure.ComplianceContent.BenchmarkRepository _benchmarks = null!;

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

		ScanOptions scanOptionsValue = new()
		{
			ArtifactStorePath = _artifactDirectory,
			ProfilePath = "/invented/profile/path",
			TimeoutSeconds = 60,
			AttestationProfile = "invented-vsphere-stig",
			SafTimeoutSeconds = 30,
		};
		// Issue #741 CKL benchmark identity: the legacy static target-kind-keyed stamp
		// for a vsphere-kind target -- deliberately distinct from any frozen benchmark
		// revision an e2e test seeds, so the fallback-vs-frozen precedence is provable.
		scanOptionsValue.BenchmarkMetadata["vsphere"] = new ScanBenchmarkMetadata
		{
			BenchmarkId = "invented_static_vsphere_benchmark",
			Title = "Invented Static vSphere STIG",
			ReleaseInfo = "Release: 1 Benchmark Date: 01 Jan 2026",
			Version = "1",
		};
		// Issue #918: the NSX-transport analogue of the vsphere-kind static stamp above --
		// a DISTINCT identity so NsxComponentJob_FrozenBenchmarkRevision_...'s
		// precedence-proving assertions have a real (invented) fallback value to prove
		// were NOT used, mirroring #915's vsphere-kind design rather than relying on a
		// null/absent fallback that a broken precedence could not be told apart from.
		scanOptionsValue.BenchmarkMetadata["nsx-api"] = new ScanBenchmarkMetadata
		{
			BenchmarkId = "invented_static_nsx_benchmark",
			Title = "Invented Static NSX STIG",
			ReleaseInfo = "Release: 1 Benchmark Date: 01 Jan 2026",
			Version = "1",
		};
		IOptions<ScanOptions> scanOptions = Options.Create(scanOptionsValue);
		IOptions<Waypoint.Core.ComplianceContent.ComplianceContentOptions> complianceContentOptions =
			Options.Create(new Waypoint.Core.ComplianceContent.ComplianceContentOptions { ContentPath = _contentDirectory });

		// Issue #311: no STIG Manager connection is ever configured in this suite (no
		// row written to stigman_connections), so ScanUploadCoordinator.UploadAsync
		// always resolves to "no connection configured" -- a non-fatal Failed outcome,
		// same as production against an unconfigured appliance. StubStigManagerUploadClient
		// is never actually invoked by these tests as a result, but is still wired for
		// completeness/parity with the real DI graph.
		StigManagerRepository stigman = new(_fixture.ConnectionString);
		ScanUploadCoordinator uploadCoordinator = new(
			stigman, new StubStigManagerUploadClient(), _secretStore, _repository, _redactor);

		// Issue #738: ComponentProfileRevisionResolver's dependencies, also kept as fields
		// so the vcenter-component tests below can seed real catalog/baseline rows.
		_catalog = new Waypoint.Infrastructure.ComplianceContent.CatalogRepository(_fixture.ConnectionString);
		_baselines = new Waypoint.Infrastructure.ComplianceContent.BaselineRepository(_fixture.ConnectionString);
		ComponentProfileRevisionResolver vCenterProfileRevisions = new(_baselines, _catalog, complianceContentOptions);
		_benchmarks = new Waypoint.Infrastructure.ComplianceContent.BenchmarkRepository(_fixture.ConnectionString);

		// Issue #745: component-result recording, additive alongside the pre-existing
		// pipeline this suite already exercises end to end.
		ComponentResultRecordingService resultRecording = new(
			new ComponentResultRepository(_fixture.ConnectionString), NullLogger<ComponentResultRecordingService>.Instance);

		_handler = new ScanJobHandler(
			executor, _secretStore, _credentials, _targets, _runSecrets, _repository, _redactor, wrappedPsOptions, scanOptions,
			complianceContentOptions, _configDocs, _attestationSnapshots, uploadCoordinator, vCenterProfileRevisions, _benchmarks, resultRecording);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		if (!_poolDisposedByTest)
		{
			_pool.Dispose();
		}
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_artifactDirectory, recursive: true);
		Directory.Delete(_contentDirectory, recursive: true);
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

	/// <summary>
	/// Issue #585 (epic #582, migration 0044): a job carrying a per-purpose credential
	/// snapshot (<c>job_credential_bindings</c>) makes the handler decrypt the
	/// SNAPSHOT's execution-purpose credential, not the legacy <c>jobs.credential_id</c>
	/// column -- proven by decrypt-audit attribution: only the snapshot credential gets
	/// a <c>secret.decrypted</c> row. The sibling tests in this class fan out specs
	/// with NO CredentialBindings, which is exactly the pre-0044 legacy job shape --
	/// their continued passing is the legacy-fallback proof.
	/// </summary>
	[Fact]
	public async Task SnapshotBinding_PreferredOverLegacyJobColumn_ForExecutionPurpose()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid legacyColumnCredentialId) = await SeedVsphereTargetAsync("invented-legacy-column-secret"); // gitleaks:allow -- invented test canary
		Guid snapshotCredentialId = (await _credentials.CreateAsync(
			$"svc-snapshot-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "snapshot-admin@example.internal"))!.Value;
		await _secretStore.StoreAsync(snapshotCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-snapshot-secret" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, site_id = Guid.NewGuid() });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 3, TargetId: targetId, CredentialId: legacyColumnCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec("vsphere-api", snapshotCredentialId)])],
			"tester", CancellationToken.None);

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
		await using (NpgsqlCommand snapshotDecrypts = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id = $2", connection))
		{
			snapshotDecrypts.Parameters.AddWithValue(snapshotCredentialId);
			snapshotDecrypts.Parameters.AddWithValue(jobIds[0]);
			Assert.Equal(1L, (long)(await snapshotDecrypts.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand legacyDecrypts = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id = $2", connection))
		{
			legacyDecrypts.Parameters.AddWithValue(legacyColumnCredentialId);
			legacyDecrypts.Parameters.AddWithValue(jobIds[0]);
			Assert.Equal(0L, (long)(await legacyDecrypts.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Issue #639's core fix: a job payload carrying <c>profile_key</c> (set by
	/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService.CreateScanRunAsync"/>
	/// after validating the profile is installed) makes the handler resolve
	/// <c>ComplianceContentOptions.ContentPath/{profile_key}</c> -- the SAME working
	/// tree <c>content-pull</c> populates -- as the InSpec profile directory, NOT the
	/// legacy fixed <see cref="ScanOptions.ProfilePath"/>. Proven the same way
	/// <see cref="SrgTarget_SudoEnabledCredential_PassesSudoThroughToInvocation"/>
	/// proves sudo passthrough: the stub module echoes its resolved <c>ProfilePath</c>
	/// parameter onto the Information stream, captured as a job.log event.
	/// </summary>
	[Fact]
	public async Task ScanPayload_WithProfileKey_ResolvesContentStorePath_NotLegacyFixedPath()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-profile-key-canary");

		const string profileKey = "vmware/vsphere/vsphere8-vcenter-stig-baseline";
		string expectedProfilePath = Path.Combine(_contentDirectory, profileKey);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId, profile_key = profileKey });
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
		await using NpgsqlCommand resolvedQuery = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%' || $2 || '%'", connection);
		resolvedQuery.Parameters.AddWithValue(jobIds[0]);
		resolvedQuery.Parameters.AddWithValue(expectedProfilePath);
		Assert.True(
			(long)(await resolvedQuery.ExecuteScalarAsync())! >= 1,
			$"expected the stub's Information line to echo the resolved content-store path '{expectedProfilePath}'.");

		await using NpgsqlCommand legacyQuery = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%/invented/profile/path%'", connection);
		legacyQuery.Parameters.AddWithValue(jobIds[0]);
		Assert.Equal(0L, (long)(await legacyQuery.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// The transitional fallback half of issue #639: a job payload with NO
	/// <c>profile_key</c> (a row fanned out before this change, or any future caller
	/// that legitimately has none yet) still resolves to the legacy fixed
	/// <see cref="ScanOptions.ProfilePath"/> rather than failing outright -- see
	/// <c>ScanJobHandler.ResolveProfilePath</c>'s doc comment.
	/// </summary>
	[Fact]
	public async Task ScanPayload_WithoutProfileKey_FallsBackToLegacyFixedPath()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-no-profile-key-canary");

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
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%/invented/profile/path%'", connection);
		query.Parameters.AddWithValue(jobIds[0]);
		Assert.True((long)(await query.ExecuteScalarAsync())! >= 1, "expected the stub's Information line to echo the legacy fixed ProfilePath.");
	}

	/// <summary>
	/// Issue #737 item-4 (round-2, the round-1 review's blocker), extended by #739/#740
	/// to carry the frozen profile/baseline ids every real esxi-selector plan item now
	/// freezes: a NARROWABLE component job (transport <c>vmware</c>, selector
	/// <c>esxi</c>) executes an InSpec invocation SCOPED TO THAT ESXi host -- not the
	/// whole vCenter. Proven the same way the profile-key test proves path resolution:
	/// the stub echoes the selector the handler passed onto the Information stream,
	/// captured as a <c>job.log</c> event. The narrowed job's line reads
	/// <c>selector=esxi/&lt;host&gt;</c>; it must NEVER read <c>&lt;whole-target&gt;</c>.
	/// This is the "assert what an EXECUTED component job scans, not just how many jobs
	/// exist" observation finding 1 required.
	/// </summary>
	[Fact]
	public async Task NarrowableEsxiComponentJob_ScopesInvocationToThatHost_NotWholeVCenter()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-narrow-esxi-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedVSphereCatalogAndBaselineAsync("narrow-esxi", CatalogSelectorKinds.Esxi, materializeOnDisk: true);

		const string esxiHost = "esxi-narrow-07.example.internal";
		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "esxi",
			selector_name = esxiHost,
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 4, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

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
		Assert.True(await ScanLogContainsAsync(jobIds[0], $"selector=esxi/{esxiHost}"),
			"expected the executed scan to be narrowed to this ESXi host.");
		Assert.False(await ScanLogContainsAsync(jobIds[0], "selector=<whole-target>"),
			"a narrowed esxi component job must NOT run a whole-target scan.");
	}

	/// <summary>
	/// Issue #737 item-4 sibling isolation, extended by #739/#740's frozen profile/
	/// baseline ids: two ESXi component jobs on the SAME target each scan their OWN
	/// host, with DISTINCT selectors -- neither re-scans the whole vCenter, and neither
	/// carries the other's selector. This is the direct counter-proof to the round-1
	/// blocker ("N sibling jobs each run the identical whole-target scan").
	/// </summary>
	[Fact]
	public async Task TwoNarrowableSiblingJobs_EachScopeToTheirOwnHost_NeverWholeTarget()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-narrow-siblings-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedVSphereCatalogAndBaselineAsync("narrow-siblings", CatalogSelectorKinds.Esxi, materializeOnDisk: true);

		const string hostA = "esxi-sib-a.example.internal";
		const string hostB = "esxi-sib-b.example.internal";
		string payloadA = JsonSerializer.Serialize(new
		{
			target_id = targetId, transport = "vmware", selector_kind = "esxi", selector_name = hostA,
			catalog_execution_profile_id = executionProfileId, baseline_id = baselineId,
		});
		string payloadB = JsonSerializer.Serialize(new
		{
			target_id = targetId, transport = "vmware", selector_kind = "esxi", selector_name = hostB,
			catalog_execution_profile_id = executionProfileId, baseline_id = baselineId,
		});

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[
				new JobSpec("scan", 4, TargetId: targetId, CredentialId: credentialId, Payload: payloadA),
				new JobSpec("scan", 4, TargetId: targetId, CredentialId: credentialId, Payload: payloadB),
			],
			"tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
			await PollUntilTerminalAsync(jobIds[1]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		// Each job scanned exactly its own host, never the other's, never whole-target.
		Assert.True(await ScanLogContainsAsync(jobIds[0], $"selector=esxi/{hostA}"));
		Assert.False(await ScanLogContainsAsync(jobIds[0], $"selector=esxi/{hostB}"));
		Assert.True(await ScanLogContainsAsync(jobIds[1], $"selector=esxi/{hostB}"));
		Assert.False(await ScanLogContainsAsync(jobIds[1], $"selector=esxi/{hostA}"));
		Assert.False(await ScanLogContainsAsync(jobIds[0], "selector=<whole-target>"));
		Assert.False(await ScanLogContainsAsync(jobIds[1], "selector=<whole-target>"));
	}

	/// <summary>
	/// Issue #737 item-4 fan-out gate: the ONE collapsed whole-target remainder job
	/// (payload <c>unnarrowed = true</c>) executes a WHOLE-TARGET scan -- exactly the
	/// pre-#737 invocation, with NO selector -- so an un-narrowable component set never
	/// forces a narrowed invocation the runner cannot honor. Together with the fan-out
	/// tests (which prove there is exactly ONE such job per target), this is the
	/// "no configuration produces duplicate whole-target executions" invariant.
	/// </summary>
	[Fact]
	public async Task UnnarrowedCollapsedJob_RunsWholeTargetScan_NoSelector()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-unnarrowed-canary");

		// The collapsed remainder job carries a component_id + transport for provenance,
		// but unnarrowed = true and NO selector_kind -- the handler must ignore any
		// narrowing and run the whole-target invocation.
		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			component_id = Guid.NewGuid(),
			unnarrowed = true,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.True(await ScanLogContainsAsync(jobIds[0], "selector=<whole-target>"),
			"an unnarrowed collapsed job must run a whole-target scan.");
		Assert.False(await ScanLogContainsAsync(jobIds[0], "selector=esxi/"),
			"an unnarrowed collapsed job must NOT carry any object selector.");
	}

	/// <summary>
	/// Issue #741/#743: a VCSA service item (ssh/service) executes over ssh, authenticated
	/// with the ITEM's own vcsa-ssh purpose -- never the vsphere-api credential the
	/// OWNING TARGET's kind would otherwise default to. Proven the stub-echo way every
	/// other selector test in this class proves invocation shape: the stub's
	/// Information line names the ssh host/username it was actually called with.
	/// </summary>
	[Fact]
	public async Task VcsaServiceComponentJob_AuthenticatesWithVcsaSshPurpose_NotVsphereApi()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-envoy", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "envoy", materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "envoy",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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

		// This is HDF+CKL output (STIG) -- the job must complete the FULL Standard
		// pipeline (uploaded), not terminate early at 'done' the way an SRG job would.
		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.True(await ScanLogContainsAsync(jobIds[0], "Scanning stub SRG host"),
			"a VCSA service item must invoke the SRG/ssh path, never the vmware:// path.");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand vcsaDecrypts = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id = $2", connection))
		{
			vcsaDecrypts.Parameters.AddWithValue(vcsaSshCredentialId);
			vcsaDecrypts.Parameters.AddWithValue(jobIds[0]);
			Assert.Equal(1L, (long)(await vcsaDecrypts.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand vsphereDecrypts = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id = $2", connection))
		{
			vsphereDecrypts.Parameters.AddWithValue(vsphereApiCredentialId);
			vsphereDecrypts.Parameters.AddWithValue(jobIds[0]);
			Assert.Equal(0L, (long)(await vsphereDecrypts.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Issue #741 CKL benchmark identity (the review's finding-1 non-null branch): a STIG
	/// component job whose plan item FREEZES a real <c>benchmark_revision_id</c> stamps the
	/// produced CKL with THAT revision's own benchmark identity
	/// (<see cref="BenchmarkRevision.BenchmarkKey"/>/<c>Title</c>/<c>Release</c>/<c>Version</c>),
	/// NOT the legacy static target-kind-keyed <see cref="ScanBenchmarkMetadata"/> stamp --
	/// proving the convert stage prefers the frozen revision (ADR-0022 exact-version
	/// behavior). The static vsphere stamp is deliberately configured to a DISTINCT value
	/// (see the constructor's <see cref="ScanOptions.BenchmarkMetadata"/> seed), so a wrong
	/// precedence would surface the static id in the CKL and fail this test. The stub
	/// convert echoes every stamped field into the CKL body for exactly this assertion.
	/// </summary>
	[Fact]
	public async Task StigComponentJob_FrozenBenchmarkRevision_StampsCklFromFrozenRevision_NotStaticFallback()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-bench"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-bench" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-sts-bench", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "sts", materializeOnDisk: true);

		// A real (invented) frozen benchmark revision -- migration 0052's benchmark_revisions
		// row -- with an identity deliberately unlike the static vsphere fallback.
		BenchmarkRevision revision = await _benchmarks.ImportRevisionAsync(
			new BenchmarkImportCandidate(
				"xccdf_invented.vmware_vcsa-sts_STIG",
				"Invented VCSA STS Service STIG",
				"2",
				"Release: 4 Benchmark Date: 15 Feb 2026",
				$"digest-bench-{Guid.NewGuid():N}",
				[new XccdfRule("SV-100001r1_rule", "V-100001", BenchmarkRuleSeverities.High, "invented rule")]),
			BenchmarkSources.ManualUpload, CancellationToken.None);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
			benchmark_revision_id = revision.Id,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath), $"expected a CKL at '{cklPath}'.");
		string ckl = await File.ReadAllTextAsync(cklPath);

		// Every field comes from the FROZEN revision, not the static vsphere fallback.
		Assert.Contains($"benchmark={revision.BenchmarkKey}", ckl, StringComparison.Ordinal);
		Assert.Contains($"title={revision.Title}", ckl, StringComparison.Ordinal);
		Assert.Contains($"release={revision.Release}", ckl, StringComparison.Ordinal);
		Assert.Contains($"version={revision.Version}", ckl, StringComparison.Ordinal);
		Assert.DoesNotContain("invented_static_vsphere_benchmark", ckl, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #741 CKL benchmark identity (finding-1 fallback branch): a STIG component job
	/// with NO frozen <c>benchmark_revision_id</c> on its payload falls back to the legacy
	/// static target-kind-keyed <see cref="ScanBenchmarkMetadata"/> stamp -- byte-identical
	/// to pre-#741 behavior for any job that never froze a benchmark identity. Pins that
	/// the new non-null branch is the ONLY thing that changes the stamp; its absence leaves
	/// the static path intact.
	/// </summary>
	[Fact]
	public async Task StigComponentJob_NoFrozenBenchmarkRevision_StampsCklFromStaticFallback()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-static"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-static" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-sts-static", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "sts", materializeOnDisk: true);

		// No benchmark_revision_id key at all -- the legacy/unnarrowed shape.
		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath), $"expected a CKL at '{cklPath}'.");
		string ckl = await File.ReadAllTextAsync(cklPath);

		// The static vsphere-kind stamp, exactly as configured in the constructor.
		Assert.Contains("benchmark=invented_static_vsphere_benchmark", ckl, StringComparison.Ordinal);
		Assert.Contains("title=Invented Static vSphere STIG", ckl, StringComparison.Ordinal);
		Assert.Contains("version=1", ckl, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1068 / PR #1224 review round 1 finding 3: drives one full scan job to
	/// completion for a target whose name and <c>connection.host</c> the caller chose,
	/// and returns the stub CKL's body. The stub <c>Invoke-WaypointConvert</c> echoes
	/// <c>hostname=/fqdn=/ip=/mac=</c> exactly so the C# half -- which is what derives
	/// per-target asset identity -- can be asserted end to end rather than only at the
	/// PowerShell argument-string layer. Issue #1285: also returns the job id so a
	/// caller can additionally inspect that job's <c>job.log</c> events.
	/// </summary>
	private async Task<(string Ckl, Guid JobId)> RunScanAndReadStubCklAsync(string caseTag, string targetName, string connectionHost)
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetWithAssetFactsAsync(
			$"invented-vsphere-api-secret-{caseTag}" /* gitleaks:allow -- invented test canary */, targetName, connectionHost);
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(
			vcsaSshCredentialId,
			System.Text.Encoding.UTF8.GetBytes($"invented-vcsa-ssh-secret-{caseTag}" /* gitleaks:allow -- invented test canary */),
			"test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) = await SeedSshCatalogAndBaselineAsync(
			$"vcsa-sts-{caseTag}", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl,
			selectorName: "sts", materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath), $"expected a CKL at '{cklPath}'.");
		return (await File.ReadAllTextAsync(cklPath), jobIds[0]);
	}

	/// <summary>
	/// Issue #1068 AC1/AC3, the FQDN arm of the convert stage's
	/// <c>IPAddress.TryParse</c> split: a <c>connection.host</c> that is NOT an IP
	/// literal is stamped as the CKL's <c>--fqdn</c>, never its <c>--ip</c>, and the
	/// target's own operator-assigned name is stamped as <c>--hostname</c>. Waypoint
	/// holds no MAC fact (issue #1227), so <c>--mac</c> stays empty -- a missing fact,
	/// never invented. Swapping the two branches, or dropping the Hostname line, fails
	/// here; before this test the whole C# half was uncovered.
	/// </summary>
	[Fact]
	public async Task ScanJob_FqdnConnectionHost_StampsCklHostnameAndFqdn_NeverIpOrMac()
	{
		const string targetName = "invented-vcsa-fqdn-target";
		const string connectionHost = "vcsa-fqdn-01.example.internal";

		(string ckl, _) = await RunScanAndReadStubCklAsync("assetfqdn", targetName, connectionHost);

		Assert.Contains($"hostname={targetName}", ckl, StringComparison.Ordinal);
		Assert.Contains($"fqdn={connectionHost}", ckl, StringComparison.Ordinal);
		Assert.Contains("ip= ", ckl, StringComparison.Ordinal);
		Assert.Contains("mac= ", ckl, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1068 AC1/AC3, the IP-literal arm: an RFC 5737 documentation-range
	/// <c>connection.host</c> is stamped as the CKL's <c>--ip</c> and never its
	/// <c>--fqdn</c>. Paired with the FQDN test above, the two targets are
	/// distinguishable by asset identity even though they share a profile -- the whole
	/// point of restoring these flags.
	/// </summary>
	[Fact]
	public async Task ScanJob_IpLiteralConnectionHost_StampsCklHostnameAndIp_NeverFqdnOrMac()
	{
		const string targetName = "invented-vcsa-ip-target";
		const string connectionHost = "198.51.100.10";

		(string ckl, _) = await RunScanAndReadStubCklAsync("assetip", targetName, connectionHost);

		Assert.Contains($"hostname={targetName}", ckl, StringComparison.Ordinal);
		Assert.Contains($"ip={connectionHost}", ckl, StringComparison.Ordinal);
		Assert.Contains("fqdn= ", ckl, StringComparison.Ordinal);
		Assert.Contains("mac= ", ckl, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1285, the third <c>AddCklAssetIdentityAsync</c> branch (PR #1224's WARN-
	/// and-omit): a target name outside <see cref="CklAssetIdentity"/>'s allow-list --
	/// here one carrying a literal double quote, the exact round-1 injection
	/// character -- is never added to the convert stage's <c>parameters</c>, so the
	/// stub's echoed CKL shows an empty <c>hostname=</c> rather than the rejected text,
	/// and a <c>job.log</c> WARN names the field ("Hostname") without ever repeating the
	/// value. <c>connectionHost</c> stays an ordinary FQDN throughout as a passthrough
	/// control: only the field CklAssetIdentity actually rejects is omitted, proving the
	/// drop is value-specific rather than the whole convert-stage identity going dark.
	/// <para>
	/// PR #1320 round-1 finding 1: the leak guard searches for <c>LeakToken</c>, the
	/// distinctive prefix of the rejected name, NOT the whole name. <c>job_events.payload</c>
	/// is <c>JSONB</c>, so <c>payload::text</c> renders a literal <c>"</c> escaped as
	/// <c>\"</c> and a LIKE pattern ending in the bare quote could never match even when
	/// the value IS present -- an inert assertion. The token is letters/digits/<c>-</c>
	/// only, so it survives JSON escaping byte-for-byte in both the rendered text and any
	/// decoded value, and both sweeps below bite. Proven by mutation: appending the value
	/// to <c>AddCklAssetIdentityAsync</c>'s WARN line turns both assertions red.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ScanJob_TargetNameFailsCklAssetIdentity_OmitsHostnameAndWarnsFieldOnly_NeverTheValue()
	{
		// Escape-proof: no JSON-escapable character, so it renders identically inside a
		// JSONB payload's text and inside any decoded string value.
		const string leakToken = "invented-leak-token-7f3a";
		const string targetName = leakToken + "\""; // rejected: literal '"' is round-1's injection character.
		const string connectionHost = "vcsa-fqdn-warn-01.example.internal"; // passthrough control: still stamped.

		(string ckl, Guid jobId) = await RunScanAndReadStubCklAsync("assetwarn", targetName, connectionHost);

		Assert.Contains("hostname= ", ckl, StringComparison.Ordinal);
		Assert.DoesNotContain(leakToken, ckl, StringComparison.Ordinal);
		Assert.Contains($"fqdn={connectionHost}", ckl, StringComparison.Ordinal);
		Assert.Contains("ip= ", ckl, StringComparison.Ordinal);
		Assert.Contains("mac= ", ckl, StringComparison.Ordinal);

		bool sawFieldWarn = await JobLogContainsSeverityAndFragmentAsync(jobId, "Warning", "Hostname");
		Assert.True(sawFieldWarn, "expected a job.log WARN naming the rejected field 'Hostname'.");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Sweep 1 -- the whole rendered payload of EVERY row for this job.
		await using NpgsqlCommand rendered = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND payload::text LIKE '%' || $2 || '%'", connection);
		rendered.Parameters.AddWithValue(jobId);
		rendered.Parameters.AddWithValue(leakToken);
		Assert.Equal(0L, (long)(await rendered.ExecuteScalarAsync())!);

		// Sweep 2 -- the DECODED value of every top-level key of every row, so the guard
		// does not depend on JSONB's text rendering at all.
		await using NpgsqlCommand decoded = new(
			"""
			SELECT count(*) FROM job_events e
			WHERE e.job_id = $1
			  AND jsonb_typeof(e.payload) = 'object'
			  AND EXISTS (SELECT 1 FROM jsonb_each_text(e.payload) kv WHERE kv.value LIKE '%' || $2 || '%')
			""", connection);
		decoded.Parameters.AddWithValue(jobId);
		decoded.Parameters.AddWithValue(leakToken);
		Assert.Equal(0L, (long)(await decoded.ExecuteScalarAsync())!);

		// Guard the guard: the token must be findable when it IS present, so a green
		// result above means "absent", never "the pattern cannot match".
		await using NpgsqlCommand selfCheck = new(
			"SELECT (jsonb_build_object('line', 'omitted: ' || $1::text)::text LIKE '%' || $2 || '%')", connection);
		selfCheck.Parameters.AddWithValue(targetName);
		selfCheck.Parameters.AddWithValue(leakToken);
		Assert.True((bool)(await selfCheck.ExecuteScalarAsync())!, "the leak pattern must match a payload that really carries the value.");
	}

	/// <summary>
	/// Issue #744 (epic #726 Wave 4 first slice) rule-correction matrix, matched case:
	/// a frozen benchmark revision whose one rule_id is exactly the CKL stub's
	/// fixed existing rule id ('SV-100001r1_rule', see
	/// <c>WaypointScanStubModule.Invoke-WaypointConvert</c>'s invented simulation)
	/// reaches ScanJobHandler's convert stage as a fully-matched correction: the
	/// emitted job.log coverage event reports Matched=1, zero unmatched, and Info
	/// severity (full coverage never warns).
	/// </summary>
	[Fact]
	public async Task StigComponentJob_RuleCorrection_AllRulesMatched_ReportsFullCoverage()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-rulematch"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-rulematch" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-sts-rulematch", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "sts", materializeOnDisk: true);

		// Matches the stub's fixed existing rule id exactly -- a fully-covered revision.
		BenchmarkRevision revision = await _benchmarks.ImportRevisionAsync(
			new BenchmarkImportCandidate(
				"xccdf_invented.vmware_vcsa-sts_STIG",
				"Invented VCSA STS Service STIG",
				"2",
				"Release: 4 Benchmark Date: 15 Feb 2026",
				$"digest-rulematch-{Guid.NewGuid():N}",
				[new XccdfRule("SV-100001r1_rule", "V-100001", BenchmarkRuleSeverities.High, "invented rule")]),
			BenchmarkSources.ManualUpload, CancellationToken.None);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
			benchmark_revision_id = revision.Id,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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

		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' "
			+ "AND payload::text LIKE '%\"matched\": 1%' AND payload::text LIKE '%\"unmatched_count\": 0%'", connection);
		query.Parameters.AddWithValue(jobIds[0]);
		Assert.True(
			(long)(await query.ExecuteScalarAsync())! >= 1,
			"expected a job.log coverage event reporting matched=1, unmatched_count=0.");
	}

	/// <summary>
	/// Issue #744 rule-correction matrix, unmatched case: a frozen benchmark revision
	/// whose ONE rule_id is deliberately NOT the CKL stub's fixed existing rule id
	/// reaches ScanJobHandler's convert stage as a fully-unmatched correction -- the
	/// AC "unmatched rules are visible and cannot masquerade as complete" -- proving
	/// the coverage event surfaces the exact unmatched rule id rather than silently
	/// reporting success, and the job still completes (unmatched rules degrade
	/// honestly, they never fail the scan run).
	/// </summary>
	[Fact]
	public async Task StigComponentJob_RuleCorrection_UnmatchedRule_ReportsCoverageGapWithoutFailingJob()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-ruleunmatched"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-ruleunmatched" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-sts-ruleunmatched", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "sts", materializeOnDisk: true);

		// Deliberately a DIFFERENT rule_id than the stub's fixed existing identity --
		// simulates a mapped revision whose rules do not cover this CKL's controls.
		BenchmarkRevision revision = await _benchmarks.ImportRevisionAsync(
			new BenchmarkImportCandidate(
				"xccdf_invented.vmware_vcsa-sts_STIG",
				"Invented VCSA STS Service STIG",
				"2",
				"Release: 4 Benchmark Date: 15 Feb 2026",
				$"digest-ruleunmatched-{Guid.NewGuid():N}",
				[new XccdfRule("SV-999999r1_rule", "V-999999", BenchmarkRuleSeverities.High, "unrelated invented rule")]),
			BenchmarkSources.ManualUpload, CancellationToken.None);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
			benchmark_revision_id = revision.Id,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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

		// Unmatched rules never fail the scan run -- the job still reaches its terminal.
		Assert.Equal("uploaded", await GetJobFieldAsync(jobIds[0], "state"));
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath), "unmatched rule correction must never destroy the CKL artifact.");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' "
			+ "AND payload::text LIKE '%\"matched\": 0%' AND payload::text LIKE '%SV-100001r1_rule%' AND payload::text LIKE '%Warning%'", connection);
		query.Parameters.AddWithValue(jobIds[0]);
		Assert.True(
			(long)(await query.ExecuteScalarAsync())! >= 1,
			"expected a Warning job.log coverage event naming the exact unmatched rule id 'SV-100001r1_rule', never silently dropped.");
	}

	/// <summary>
	/// Issue #918: the NSX-transport analogue of
	/// <see cref="StigComponentJob_FrozenBenchmarkRevision_StampsCklFromFrozenRevision_NotStaticFallback"/>.
	/// PR #916 (issue #742) claimed the frozen-revision CKL stamp is transport-agnostic
	/// because it rides the generic <c>benchmark_revision_id</c> payload field through
	/// <c>BuildPlanItemJobSpec</c>, unchanged from #915 -- but no NSX-transport fixture
	/// exercised it, so an nsx-api-specific regression (e.g. a branch that never threads
	/// <c>benchmark_revision_id</c> into the stamp for this transport) would not have been
	/// caught by the ssh/vmware tests alone (docs/testing.md's fixture-monoculture
	/// guidance). Seeds a real (invented) migration-0052 <c>benchmark_revisions</c> row via
	/// <see cref="_benchmarks"/> with an identity distinct from the static NSX-kind
	/// fallback stamp, freezes it onto a narrowed nsx-api/service (NSX 4.x STIG) component
	/// job payload, and asserts every stamped CKL field (benchmark/title/release/version)
	/// comes from that frozen revision, never the static fallback.
	/// </summary>
	[Fact]
	public async Task NsxComponentJob_FrozenBenchmarkRevision_StampsCklFromFrozenRevision_NotStaticFallback()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-frozen-revision-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedNsxCatalogAndBaselineAsync("manager-frozen-revision", CatalogOutputKinds.HdfAndCkl, "manager", materializeOnDisk: true);

		// A real (invented) frozen benchmark revision -- migration 0052's benchmark_revisions
		// row -- with an identity deliberately unlike the static "nsx-api"-kind fallback
		// stamp configured in the constructor's ScanOptions.BenchmarkMetadata seed.
		BenchmarkRevision revision = await _benchmarks.ImportRevisionAsync(
			new BenchmarkImportCandidate(
				"xccdf_invented.vmware_nsx-manager_STIG",
				"Invented NSX Manager STIG",
				"2",
				"Release: 3 Benchmark Date: 20 Feb 2026",
				$"digest-nsx-bench-{Guid.NewGuid():N}",
				[new XccdfRule("SV-200001r1_rule", "V-200001", BenchmarkRuleSeverities.High, "invented rule")]),
			BenchmarkSources.ManualUpload, CancellationToken.None);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = CatalogTransports.NsxApi,
			selector_kind = CatalogSelectorKinds.Service,
			selector_name = "manager",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = CatalogOutputKinds.HdfAndCkl,
			benchmark_revision_id = revision.Id,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.True(File.Exists(cklPath), $"expected a CKL at '{cklPath}'.");
		string ckl = await File.ReadAllTextAsync(cklPath);

		// Every field comes from the FROZEN revision, not the static NSX-kind fallback.
		Assert.Contains($"benchmark={revision.BenchmarkKey}", ckl, StringComparison.Ordinal);
		Assert.Contains($"title={revision.Title}", ckl, StringComparison.Ordinal);
		Assert.Contains($"release={revision.Release}", ckl, StringComparison.Ordinal);
		Assert.Contains($"version={revision.Version}", ckl, StringComparison.Ordinal);
		Assert.DoesNotContain("invented_static_nsx_benchmark", ckl, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #741/#743 output routing: SRG output (<c>output_kind: "hdf"</c>) terminates
	/// at <c>done</c> after the attest stage -- NEVER reaching convert/CKL/upload --
	/// determined purely by the item's frozen CATALOG kind, proven here on a VCSA
	/// SERVICE item whose OWNING TARGET is vsphere-kind (the case #743's AC explicitly
	/// calls out: target kind must never decide this).
	/// </summary>
	[Fact]
	public async Task VcsaServiceComponentJob_SrgOutputKind_TerminatesAtDone_NeverReachesConvert()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-2"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-2" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-vami-srg", CatalogSelectorKinds.Service, CatalogOutputKinds.Hdf, selectorName: "vami", materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "vami",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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

		// 'done', never 'uploaded' -- the convert/CKL/upload stage must be unreachable.
		Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.False(File.Exists(Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl")),
			"an SRG-output (hdf-only) job must never produce a CKL file.");
	}

	/// <summary>
	/// Issue #743: a whole-appliance SSH product item (ssh/target -- Photon/Aria/vIDM)
	/// on an <c>ssh</c>-kind target executes over ssh with its OWN frozen profile/
	/// baseline, exactly like the VCSA service path, proving the SAME narrowing/
	/// execution machinery serves both #741 and #743 families.
	/// </summary>
	[Fact]
	public async Task SshTargetProductComponentJob_ResolvesActivatedRevisionProfilePath()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedSrgTargetAsync("invented-photon-secret"); // gitleaks:allow -- invented test canary
		(Guid executionProfileId, Guid baselineId, string profileKey) =
			await SeedSshCatalogAndBaselineAsync("photon-os", CatalogSelectorKinds.Target, CatalogOutputKinds.Hdf, selectorName: null, materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "target",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 6, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

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
		string expectedProfilePath = Path.Combine(_contentDirectory, "revisions/digest-photon-os", profileKey);
		Assert.True(await ScanLogContainsAsync(jobIds[0], expectedProfilePath),
			"expected the ssh/target item to resolve and execute the ACTIVATED content-revision profile directory.");
	}

	/// <summary>
	/// Issue #911's reserved-key guard reused for the ssh family (#741/#743): an operator
	/// config-doc Input body for a narrowed ssh-transport item is still passed through
	/// <see cref="Waypoint.Core.Scans.ScanScopingInputFilter"/> even though the ssh
	/// family introduces no platform-computed scoping key of its own today -- this test
	/// pins that the filter runs unconditionally (defensive: a future reserved ssh
	/// scoping key would then already be protected with no code change here) and that an
	/// ordinary (non-reserved-key) resolved input still reaches the invocation.
	/// </summary>
	[Fact]
	public async Task VcsaServiceComponentJob_ResolvedInputsReachInvocation_ViaGeneratedInputFile()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid vsphereApiCredentialId) = await SeedVsphereTargetAsync("invented-vsphere-api-secret-3"); // gitleaks:allow -- invented test canary
		Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
			$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
		await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes("invented-vcsa-ssh-secret-3" /* gitleaks:allow -- invented test canary */), "test", CancellationToken.None);

		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedSshCatalogAndBaselineAsync("vcsa-sts", CatalogSelectorKinds.Service, CatalogOutputKinds.HdfAndCkl, selectorName: "sts", materializeOnDisk: true);

		(Guid docId, int docVersion) = await CreateResolvedInputDocAsync(executionProfileId, "invented_value: 'not-a-reserved-key'\n");

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "service",
			selector_name = "sts",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf_ckl",
			input_resolutions = new[]
			{
				new { InputName = "postgresqlPort", State = "resolved", DocId = docId, DocVersion = docVersion },
			},
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 2, TargetId: targetId, CredentialId: vsphereApiCredentialId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VcsaSsh, vcsaSshCredentialId)])],
			"tester", CancellationToken.None);

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
		Assert.True(await ScanLogContainsAsync(jobIds[0], "invented_value"),
			"expected the resolved Input config-doc body to reach the ssh invocation via the generated --input-file.");
	}

	/// <summary>Seeds a Global-layer Input config doc keyed to <paramref name="executionProfileId"/> (the same key <c>PlanConfigResolutionService</c> resolves against) and returns its (DocId, Version) for a payload's <c>input_resolutions</c> entry.</summary>
	private async Task<(Guid DocId, int Version)> CreateResolvedInputDocAsync(Guid executionProfileId, string bodyYaml)
	{
		(ConfigDocSaveOutcome outcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, $"profile-{executionProfileId:N}", ConfigDocLayers.Global, layerRef: null,
			"tester", bodyYaml, CancellationToken.None, catalogExecutionProfileId: executionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome);
		return (doc!.Id, version!.Version);
	}

	/// <summary>
	/// Issue #741/#743: the ssh-family generalization of
	/// <see cref="SeedVSphereCatalogAndBaselineAsync"/> -- seeds a real catalog execution
	/// profile on the <c>ssh</c> transport (either the <c>service</c> or <c>target</c>
	/// selector), an ACTIVATED baseline bound to a content revision, an accepted
	/// <c>catalog_import_report_entries</c> row, and -- when
	/// <paramref name="materializeOnDisk"/> is true -- the real on-disk revision/profile
	/// directory so <see cref="ComponentProfileRevisionResolver"/> resolution succeeds
	/// end to end. Returns the ids a narrowed ssh-transport job payload needs.
	/// </summary>
	private async Task<(Guid CatalogExecutionProfileId, Guid BaselineId, string ProfileKey)> SeedSshCatalogAndBaselineAsync(
		string suffix, string selectorKind, string outputKind, string? selectorName, bool materializeOnDisk)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", $"ssh-{suffix}-{Guid.NewGuid():N}", "Invented SSH Product", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "1.0.0", "1.0.0", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"{selectorKind}-{suffix}", selectorKind, CatalogTransports.Ssh, selectorKind, selectorName, null),
			CancellationToken.None);
		string contentKind = string.Equals(outputKind, CatalogOutputKinds.HdfAndCkl, StringComparison.Ordinal) ? CatalogKinds.Stig : CatalogKinds.Srg;
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, contentKind, $"release-{suffix}-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}-{Guid.NewGuid():N}", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", outputKind, CancellationToken.None);

		string profileKey = $"ssh/invented/{selectorKind}-{suffix}-profile";
		CatalogImportReport report = await _catalog.RecordImportReportAsync($"commit-{suffix}", $"digest-{suffix}-{Guid.NewGuid():N}", 1, 0, 0, CancellationToken.None);
		await _catalog.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Accepted, profileKey, null, executionProfile.Id, CancellationToken.None);

		string contentDigest = $"digest-{suffix}";
		string stagedRelativePath = $"revisions/{contentDigest}";
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", contentDigest, stagedRelativePath, CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		if (materializeOnDisk)
		{
			string profileDirectory = Path.Combine(_contentDirectory, stagedRelativePath, profileKey);
			Directory.CreateDirectory(profileDirectory);
			await File.WriteAllTextAsync(Path.Combine(profileDirectory, "inspec.yml"), $"name: invented-{selectorKind}-{suffix}-profile\n");
		}

		return (executionProfile.Id, staged.Id, profileKey);
	}

	/// <summary>
	/// Issue #742 (NSX, epic #726 Wave 3's final transport): the nsx-api/service
	/// analog of <see cref="SeedSshCatalogAndBaselineAsync"/> -- seeds a real catalog
	/// execution profile for one named NSX functional component (manager/dfw/tier0-fw/
	/// ...), an ACTIVATED baseline bound to a content revision, an accepted
	/// <c>catalog_import_report_entries</c> row, and -- when
	/// <paramref name="materializeOnDisk"/> is true -- the real on-disk revision/profile
	/// directory so <see cref="ComponentProfileRevisionResolver"/> resolution succeeds
	/// end to end. Returns the ids a narrowed nsx-api component job payload needs.
	/// </summary>
	private async Task<(Guid CatalogExecutionProfileId, Guid BaselineId, string ProfileKey)> SeedNsxCatalogAndBaselineAsync(
		string suffix, string outputKind, string selectorName, bool materializeOnDisk)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "nsx", $"nsx-{suffix}-{Guid.NewGuid():N}", "Invented NSX Product", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "4.x", "4.x", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"service-{suffix}", CatalogSelectorKinds.Service, CatalogTransports.NsxApi, CatalogSelectorKinds.Service, selectorName, null),
			CancellationToken.None);
		string contentKind = string.Equals(outputKind, CatalogOutputKinds.HdfAndCkl, StringComparison.Ordinal) ? CatalogKinds.Stig : CatalogKinds.Srg;
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, contentKind, $"release-{suffix}-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", outputKind, CancellationToken.None);

		string profileKey = $"nsx-api/invented/{selectorName}-{suffix}-profile";
		CatalogImportReport report = await _catalog.RecordImportReportAsync($"commit-{suffix}", $"digest-{suffix}-{Guid.NewGuid():N}", 1, 0, 0, CancellationToken.None);
		await _catalog.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Accepted, profileKey, null, executionProfile.Id, CancellationToken.None);

		string contentDigest = $"digest-{suffix}-{Guid.NewGuid():N}";
		string stagedRelativePath = $"revisions/{contentDigest}";
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", contentDigest, stagedRelativePath, CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		if (materializeOnDisk)
		{
			string profileDirectory = Path.Combine(_contentDirectory, stagedRelativePath, profileKey);
			Directory.CreateDirectory(profileDirectory);
			await File.WriteAllTextAsync(Path.Combine(profileDirectory, "inspec.yml"), $"name: invented-nsx-{selectorName}-{suffix}-profile\n");
		}

		return (executionProfile.Id, staged.Id, profileKey);
	}

	/// <summary>
	/// Seeds a real catalog execution profile (vcenter selector), an ACTIVATED baseline
	/// bound to a content revision, an accepted <c>catalog_import_report_entries</c> row
	/// (so <see cref="ComponentProfileRevisionResolver"/> can resolve the profile-key
	/// provenance), and -- when <paramref name="materializeOnDisk"/> is true -- the real
	/// on-disk revision/profile directory under <see cref="_contentDirectory"/> so
	/// resolution succeeds end to end. Returns the ids a vcenter-selector job payload
	/// needs.
	/// </summary>
	private Task<(Guid CatalogExecutionProfileId, Guid BaselineId, string ProfileKey)> SeedVCenterCatalogAndBaselineAsync(
		string suffix, bool materializeOnDisk) =>
		SeedVSphereCatalogAndBaselineAsync(suffix, CatalogSelectorKinds.VCenter, materializeOnDisk);

	/// <summary>
	/// Issue #739/#740: the selector-parameterized generalization of
	/// <see cref="SeedVCenterCatalogAndBaselineAsync"/> -- the SAME seeding shape (one
	/// catalog execution profile, one activated baseline bound to a staged content
	/// revision, one accepted import-provenance row) for whichever narrowable
	/// vSphere-family selector (<paramref name="selectorKind"/>: vcenter/esxi/vm) a test
	/// needs, since <see cref="ComponentProfileRevisionResolver"/> resolves the same
	/// baseline -&gt; revision -&gt; profile_key chain regardless of selector kind.
	/// </summary>
	private async Task<(Guid CatalogExecutionProfileId, Guid BaselineId, string ProfileKey)> SeedVSphereCatalogAndBaselineAsync(
		string suffix, string selectorKind, bool materializeOnDisk)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", $"vsphere-{suffix}-{Guid.NewGuid():N}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"{selectorKind}-{suffix}", selectorKind, CatalogTransports.VMware, selectorKind, null, null),
			CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{suffix}-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}-{Guid.NewGuid():N}", "Test Group", 3, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.Hdf, CancellationToken.None);

		string profileKey = $"vmware/vsphere/{selectorKind}-{suffix}-stig-baseline";
		CatalogImportReport report = await _catalog.RecordImportReportAsync($"commit-{suffix}", $"digest-{suffix}-{Guid.NewGuid():N}", 1, 0, 0, CancellationToken.None);
		await _catalog.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Accepted, profileKey, null, executionProfile.Id, CancellationToken.None);

		string contentDigest = $"digest-{suffix}-{Guid.NewGuid():N}";
		string stagedRelativePath = $"revisions/{contentDigest}";
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", contentDigest, stagedRelativePath, CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		if (materializeOnDisk)
		{
			string profileDirectory = Path.Combine(_contentDirectory, stagedRelativePath, profileKey);
			Directory.CreateDirectory(profileDirectory);
			await File.WriteAllTextAsync(Path.Combine(profileDirectory, "inspec.yml"), $"name: invented-{selectorKind}-stig-profile\n");
		}

		return (executionProfile.Id, staged.Id, profileKey);
	}

	/// <summary>
	/// Issue #738 AC 1 ("the exact planned vCenter endpoint and profile/input revisions
	/// are executed"): a job payload naming a vcenter-selector item's
	/// <c>catalog_execution_profile_id</c>/<c>baseline_id</c> resolves and executes
	/// against the ACTIVATED content-revision directory
	/// (<see cref="ComponentProfileRevisionResolver"/>), never the run-level
	/// <c>profile_key</c>/legacy fixed path -- proven the same way the #639 profile-key
	/// test proves resolution: the stub echoes its resolved <c>ProfilePath</c> onto the
	/// Information stream.
	/// </summary>
	[Fact]
	public async Task VCenterComponentJob_ResolvesActivatedRevisionProfilePath_NotLegacyOrRunLevelPath()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-vcenter-component-canary");
		(Guid executionProfileId, Guid baselineId, string profileKey) =
			await SeedVCenterCatalogAndBaselineAsync("resolve-success", materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "vcenter",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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

		// The resolved path is {ContentPath}/revisions/{digest}/{profileKey} -- assert
		// on the profileKey suffix (the digest is randomized per test run) plus that the
		// legacy fixed ProfilePath never appears.
		Assert.True(await ScanLogContainsAsync(jobIds[0], profileKey),
			"expected the stub's Information line to echo the resolved activated-revision profile path.");
		Assert.False(await ScanLogContainsAsync(jobIds[0], "/invented/profile/path"),
			"a vcenter component job must never fall back to the legacy fixed ProfilePath.");
	}

	/// <summary>
	/// Issue #738 AC 1's fail-closed half: a vcenter-selector job whose baseline's
	/// content revision was never materialized on disk (a real, common failure --
	/// e.g. this runner's compliance-content volume does not yet have the revision the
	/// plan item was frozen against) fails ONLY this job with an actionable diagnostic,
	/// never silently falling back to a wrong/fixed profile.
	/// </summary>
	[Fact]
	public async Task VCenterComponentJob_RevisionNotMaterializedOnDisk_FailsClosed_WithActionableDiagnostic()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-vcenter-missing-revision-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedVCenterCatalogAndBaselineAsync("missing-revision", materializeOnDisk: false);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "vcenter",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.Contains("not materialized", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #738 AC 1's defensive compatibility gate: a vcenter-selector payload with
	/// NO <c>catalog_execution_profile_id</c>/<c>baseline_id</c> (a malformed/legacy
	/// payload the planner should never produce for this selector) fails closed with an
	/// actionable diagnostic rather than silently falling back to an unscoped profile.
	/// </summary>
	[Fact]
	public async Task VCenterComponentJob_MissingFrozenProfileIds_FailsClosed_NeverFallsBackToUnscopedProfile()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-vcenter-no-ids-canary");

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "vcenter",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.Contains("carries no catalog_execution_profile_id", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #738/#879: a vCenter component job's frozen, RESOLVED Input config-doc
	/// (state = resolved, naming a real config_docs/config_versions row) is materialized
	/// into an actual InSpec inputs file passed to the invocation -- not merely recorded
	/// as plan-time provenance. Proven via the same stub-echo idiom: the stub's
	/// Information line includes the generated inputs file's raw content.
	/// </summary>
	[Fact]
	public async Task VCenterComponentJob_ResolvedInputConfigDoc_IsMaterializedAsInspecInputsFile()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-vcenter-inputs-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedVCenterCatalogAndBaselineAsync("inputs-materialize", materializeOnDisk: true);

		const string inputsBody = "invented_target_ip: '198.51.100.42'\n";
		(ConfigDocSaveOutcome saveOutcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, $"invented-vcenter-inputs-profile-{Guid.NewGuid():N}", ConfigDocLayers.Global,
			null, "test-fixture", inputsBody, CancellationToken.None, executionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, saveOutcome);
		Assert.NotNull(doc);
		Assert.NotNull(version);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "vcenter",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			input_resolutions = new[]
			{
				new { InputName = "invented_target_ip", State = "resolved", DocId = doc!.Id, DocVersion = version!.Version },
			},
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.True(await ScanLogContainsAsync(jobIds[0], "invented_target_ip"),
			"expected the resolved Input config doc's body to be materialized into the InSpec inputs file the stub echoed.");
	}

	/// <summary>
	/// Issue #739/#740 AC "the exact planned ESXi/VM endpoint and profile/input
	/// revisions are executed": an esxi-selector item resolves its activated content
	/// revision through the SAME <see cref="ComponentProfileRevisionResolver"/> chain
	/// PR #907 (#738) proved for vcenter, AND still carries its narrowed
	/// SelectorName through to the invocation -- proving profile resolution and object
	/// narrowing compose rather than one replacing the other.
	/// </summary>
	[Theory]
	[InlineData("esxi")]
	[InlineData("vm")]
	public async Task NarrowedVSphereComponentJob_ResolvesActivatedRevisionProfilePath_AndKeepsSelectorNarrowing(string selectorKind)
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync($"invented-{selectorKind}-component-canary");
		(Guid executionProfileId, Guid baselineId, string profileKey) =
			await SeedVSphereCatalogAndBaselineAsync($"{selectorKind}-resolve-success", selectorKind, materializeOnDisk: true);

		const string objectName = "invented-object-01.example.internal";
		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = selectorKind,
			selector_name = objectName,
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.True(await ScanLogContainsAsync(jobIds[0], profileKey),
			"expected the stub's Information line to echo the resolved activated-revision profile path.");
		Assert.False(await ScanLogContainsAsync(jobIds[0], "/invented/profile/path"),
			"a narrowed component job must never fall back to the legacy fixed ProfilePath.");
		Assert.True(await ScanLogContainsAsync(jobIds[0], $"selector={selectorKind}/{objectName}"),
			"resolving the activated profile must not drop the item's own object narrowing.");
	}

	/// <summary>
	/// Issue #911 (closed by this slice): an operator Input config-doc body naming a
	/// reserved platform selector-scoping key (<c>vmhostName</c>) must NEVER widen a
	/// narrowed esxi scan -- this is the exact repro from #911's Summary, now reachable
	/// because #739/#740 wires the config-doc inputs file for esxi/vm. Proven two ways:
	/// (1) the stub's echoed inputs-file content shows the PLATFORM's own vmhostName
	/// (the narrowed host), never the operator-supplied one, and (2) a job.log WARN
	/// names the dropped key -- the operator's colliding value is not silently ignored,
	/// it is diagnosably rejected.
	/// </summary>
	[Fact]
	public async Task EsxiComponentJob_ConfigDocInputsNamingVmhostName_NeverOverridesNarrowedSelector()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-911-repro-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedVSphereCatalogAndBaselineAsync("911-repro", CatalogSelectorKinds.Esxi, materializeOnDisk: true);

		const string narrowedHost = "esxi-narrowed-real.example.internal";
		const string attackerHost = "esxi-attacker-widened.example.internal";

		// The operator's Input config doc names the RESERVED scoping key `vmhostName` --
		// #911's Summary's exact hazard: this must never reach InSpec and override the
		// platform-computed narrowed selector.
		string inputsBody = $"vmhostName: '{attackerHost}'\ninvented_unrelated_input: 'kept'\n";
		(ConfigDocSaveOutcome saveOutcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, $"invented-911-inputs-profile-{Guid.NewGuid():N}", ConfigDocLayers.Global,
			null, "test-fixture", inputsBody, CancellationToken.None, executionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, saveOutcome);
		Assert.NotNull(doc);
		Assert.NotNull(version);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "vmware",
			selector_kind = "esxi",
			selector_name = narrowedHost,
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			input_resolutions = new[]
			{
				new { InputName = "vmhostName", State = "resolved", DocId = doc!.Id, DocVersion = version!.Version },
			},
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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

		// The narrowed selector file (appended LAST, issue #911's flag-order flip) always
		// carries the platform's own vmhostName -- assert the ACTUAL scan scope, not just
		// job success.
		Assert.True(await ScanLogContainsAsync(jobIds[0], $"selector=esxi/{narrowedHost}"),
			"the executed scan must stay narrowed to the platform-computed host.");

		// The generated operator inputs file (echoed by the stub) must never contain the
		// attacker-supplied vmhostName value -- the reserved key was dropped before the
		// file was even written, not merely out-voted by ordering.
		Assert.False(await ScanLogContainsAsync(jobIds[0], attackerHost),
			"the operator config-doc's vmhostName value must never reach the invocation at all.");

		// The unrelated key from the SAME config doc survives the filter -- proving this
		// is a targeted key drop, not a reject of the whole document.
		Assert.True(await ScanLogContainsAsync(jobIds[0], "invented_unrelated_input"),
			"a non-reserved key in the same config doc must still be materialized.");

		// A WARN names the dropped key -- diagnosable, not silent.
		bool sawWarn = await JobLogContainsSeverityAndFragmentAsync(jobIds[0], "Warning", "vmhostName");
		Assert.True(sawWarn, "expected a job.log WARN naming the dropped reserved scoping key.");
	}

	/// <summary>True when any of the job's <c>job.log</c> events at <paramref name="severity"/> contains <paramref name="fragment"/> (same simple substring idiom as the pre-existing attestation-WARN test).</summary>
	private async Task<bool> JobLogContainsSeverityAndFragmentAsync(Guid jobId, string severity, string fragment)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' "
				+ "AND payload::text LIKE '%' || $2 || '%' AND payload::text LIKE '%' || $3 || '%'", connection);
		query.Parameters.AddWithValue(jobId);
		query.Parameters.AddWithValue(severity);
		query.Parameters.AddWithValue(fragment);
		return (long)(await query.ExecuteScalarAsync())! >= 1;
	}

	/// <summary>True when any of the job's <c>job.log</c> events contains <paramref name="fragment"/> (the scan stub echoes its resolved selector there).</summary>
	/// <summary>
	/// Issue #743: job.log events read STRUCTURALLY rather than by raw-payload LIKE --
	/// true only when some job.log event carries severity <c>Warning</c> AND a line
	/// containing <paramref name="fragment"/>, so the assertion proves the severity as
	/// well as the text and is immune to JSON escaping of the line's own punctuation.
	/// </summary>
	private async Task<bool> WarnLogContainsAsync(Guid jobId, string fragment)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"""
			SELECT count(*) FROM job_events
			WHERE job_id = $1 AND event_type = 'job.log'
				AND payload::jsonb ->> 'severity' = 'Warning'
				AND payload::jsonb ->> 'line' LIKE '%' || $2 || '%'
			""", connection);
		query.Parameters.AddWithValue(jobId);
		query.Parameters.AddWithValue(fragment);
		return (long)(await query.ExecuteScalarAsync())! >= 1;
	}

	private async Task<bool> ScanLogContainsAsync(Guid jobId, string fragment)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%' || $2 || '%'", connection);
		query.Parameters.AddWithValue(jobId);
		query.Parameters.AddWithValue(fragment);
		return (long)(await query.ExecuteScalarAsync())! >= 1;
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

	/// <summary>
	/// Issue #1020 retry-honesty backstop: an exception thrown from BELOW
	/// <see cref="ScanJobHandler"/> -- here, disposing the runspace pool out from
	/// under it, so <see cref="PowerShellExecutor.ExecuteAsync"/>'s
	/// <c>_pool.RentAsync</c> throws <see cref="ObjectDisposedException"/> instead of
	/// returning a result -- must still classify through the SAME path as an ordinary
	/// InSpec/transport failure (<c>failed</c>, log-tail event, no thrown exception
	/// out of the handler) rather than reaching the job dispatcher's generic
	/// handler-threw catch as a raw "Unhandled exception" note. This is the shape
	/// round-9's crashed jobs actually hit (a throw from runspace/module init, before
	/// any InSpec invocation ran) -- ObjectDisposedException stands in for "the pool
	/// threw during RentAsync" generically; the fix (module-import serialization in
	/// <see cref="WaypointRunspacePool"/>) targets the AnalysisCache race specifically,
	/// but this backstop must catch ANY such throw, not just that one exception type.
	/// </summary>
	[Fact]
	public async Task PoolThrowsDuringRent_ClassifiesAsFailed_NotAnUnhandledDispatcherCrash()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-pool-throw-canary");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 3, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

		// Disposed BEFORE the dispatcher ever claims the job, so the very first
		// RentAsync call the handler's InSpec-stage invocation makes throws
		// ObjectDisposedException -- deterministic, no timing window needed.
		_pool.Dispose();
		_poolDisposedByTest = true;

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

		// The honest outcome: failed (retryable per ADR-0012), classified through
		// FailScanAsync exactly like any other transport-level failure -- never left
		// at `running` and never surfaced only as a dispatcher-level "Unhandled
		// exception" note with no job.log tail.
		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.True(await EventTypeExistsAsync(JobEventTypes.JobLog, jobIds[0]));
		string note = await GetJobFieldAsync(jobIds[0], "note");
		Assert.DoesNotContain("Unhandled exception", note, StringComparison.Ordinal);
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

	/// <summary>
	/// Issue #921 (Wave 3 live validation): an unreachable-endpoint transport failure
	/// must name the underlying connection error in the job's failure note, not the
	/// downstream "report file not found" symptom the stub module reports (mirroring
	/// the real Invoke-WaypointScan's own Test-Path-driven message when InSpec exits
	/// nonzero with no report on disk). The stub writes the transport diagnostic to
	/// the error stream first, exactly as the real module's underlying
	/// Invoke-ExternalCommand -SurfaceOutputOnFailure does.
	/// </summary>
	[Fact]
	public async Task UnreachableEndpointFailure_NotesTheTransportError_NotTheMissingReportConsequence()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "unreachable");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-unreachable-canary");

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
		string note = await GetJobFieldAsync(jobIds[0], "note");
		Assert.Contains("Unable to connect to VIServer", note, StringComparison.Ordinal);
		Assert.DoesNotContain("report file not found", note, StringComparison.Ordinal);
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
	/// Issue #742 (NSX, epic #726 Wave 3's final transport) AC "each component executes
	/// the correct leaf profile and benchmark mapping": a narrowed nsx-api/service item
	/// (e.g. the NSX Manager function) resolves its OWN activated content-revision
	/// profile through the SAME <see cref="ComponentProfileRevisionResolver"/> chain
	/// #738/#739/#740/#741/#743 proved for the vmware/ssh families, and its
	/// SelectorName rides the invocation for attribution -- never falling back to the
	/// run-level profile_key/legacy NsxProfilePath.
	/// </summary>
	[Fact]
	public async Task NsxComponentJob_ResolvesActivatedRevisionProfilePath_AndCarriesSelectorName()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-component-canary");
		(Guid executionProfileId, Guid baselineId, string profileKey) =
			await SeedNsxCatalogAndBaselineAsync("manager-resolve-success", CatalogOutputKinds.HdfAndCkl, "manager", materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = CatalogTransports.NsxApi,
			selector_kind = CatalogSelectorKinds.Service,
			selector_name = "manager",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = CatalogOutputKinds.HdfAndCkl,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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
		Assert.True(await ScanLogContainsAsync(jobIds[0], profileKey),
			"expected the stub's Information line to echo the resolved activated-revision profile path.");
		Assert.False(await ScanLogContainsAsync(jobIds[0], "/invented/nsx/profile/path"),
			"an nsx-api component job must never fall back to the legacy fixed NsxProfilePath.");
		Assert.True(await ScanLogContainsAsync(jobIds[0], "selector=manager"),
			"expected the stub to echo the component's own SelectorName (manager).");
	}

	/// <summary>
	/// Issue #742's fail-closed half, same shape as
	/// <see cref="VCenterComponentJob_RevisionNotMaterializedOnDisk_FailsClosed_WithActionableDiagnostic"/>:
	/// an nsx-api component job whose baseline's content revision was never
	/// materialized on disk fails ONLY this job with an actionable diagnostic, never
	/// silently falling back to a wrong/fixed profile.
	/// </summary>
	[Fact]
	public async Task NsxComponentJob_RevisionNotMaterializedOnDisk_FailsClosed_WithActionableDiagnostic()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-missing-revision-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedNsxCatalogAndBaselineAsync("dfw-missing-revision", CatalogOutputKinds.HdfAndCkl, "dfw", materializeOnDisk: false);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = CatalogTransports.NsxApi,
			selector_kind = CatalogSelectorKinds.Service,
			selector_name = "dfw",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = CatalogOutputKinds.HdfAndCkl,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("not materialized", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #742's defensive compatibility gate, same shape as
	/// <see cref="VCenterComponentJob_MissingFrozenProfileIds_FailsClosed_NeverFallsBackToUnscopedProfile"/>:
	/// an nsx-api/service payload with NO catalog_execution_profile_id/baseline_id (a
	/// malformed/legacy payload the planner should never produce for this selector)
	/// fails closed with an actionable diagnostic rather than silently falling back to
	/// an unscoped profile.
	/// </summary>
	[Fact]
	public async Task NsxComponentJob_MissingFrozenProfileIds_FailsClosed_NeverFallsBackToUnscopedProfile()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-no-ids-canary");

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = CatalogTransports.NsxApi,
			selector_kind = CatalogSelectorKinds.Service,
			selector_name = "manager",
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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

		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		string note = await GetJobNoteAsync(jobIds[0]);
		Assert.Contains("carries no catalog_execution_profile_id", note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #742's security-critical AC: a narrowed nsx-api component's resolved Input
	/// config-doc body IS materialized into the InSpec inputs file (same as the
	/// vmware/ssh families), but an operator-supplied auth-input key
	/// (<c>sessionToken</c>) is dropped by <see cref="ScanScopingInputFilter"/> rather
	/// than ever reaching the InSpec invocation -- proven both negatively (the
	/// attacker-supplied token value never appears anywhere in job.log) and positively
	/// (an unrelated key from the same doc survives, and a job.log WARN names the
	/// dropped key).
	/// </summary>
	[Fact]
	public async Task NsxComponentJob_ConfigDocNamingSessionToken_NeverReachesInvocation_DropsAndWarns()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, Guid credentialId) = await SeedNsxTargetAsync("invented-nsx-auth-input-canary");
		(Guid executionProfileId, Guid baselineId, string _) =
			await SeedNsxCatalogAndBaselineAsync("manager-auth-key-guard", CatalogOutputKinds.HdfAndCkl, "manager", materializeOnDisk: true);

		const string attackerToken = "attacker-supplied-session-token-9f3c"; // gitleaks:allow -- invented test canary, not a real token
		const string inputsBody = "sessionToken: 'attacker-supplied-session-token-9f3c'\ninvented_unrelated_input: 'kept-value'\n"; // gitleaks:allow -- invented test canary, not a real token
		(ConfigDocSaveOutcome saveOutcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, $"invented-nsx-inputs-profile-{Guid.NewGuid():N}", ConfigDocLayers.Global,
			null, "test-fixture", inputsBody, CancellationToken.None, executionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, saveOutcome);
		Assert.NotNull(doc);
		Assert.NotNull(version);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = CatalogTransports.NsxApi,
			selector_kind = CatalogSelectorKinds.Service,
			selector_name = "manager",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = CatalogOutputKinds.HdfAndCkl,
			input_resolutions = new[]
			{
				new { InputName = "sessionToken", State = "resolved", DocId = doc!.Id, DocVersion = version!.Version },
			},
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
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

		// Negative proof: the attacker-supplied token value never reaches job.log at all
		// (it was dropped before the inputs file was ever written, so the stub's echo of
		// the materialized file's content never contains it).
		Assert.False(await ScanLogContainsAsync(jobIds[0], attackerToken),
			"a reserved NSX auth-input key's operator-supplied value must never reach the InSpec invocation.");

		// Positive proof: the unrelated sibling key from the SAME doc survived filtering.
		Assert.True(await ScanLogContainsAsync(jobIds[0], "invented_unrelated_input"),
			"expected the unrelated key from the same config doc to survive filtering.");

		// The drop itself is attributed via a job.log WARN naming the reserved key.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND event_type = 'job.log' AND payload::text LIKE '%sessionToken%' AND payload::text LIKE '%Warning%'", connection);
		query.Parameters.AddWithValue(jobIds[0]);
		Assert.True((long)(await query.ExecuteScalarAsync())! >= 1, "expected a job.log WARN naming the dropped reserved key 'sessionToken'.");

		// Also assert the real credential's password never leaked (same canary
		// discipline as every other e2e test in this file).
		await AssertCanaryNeverLeakedAsync("invented-nsx-auth-input-canary", credentialId);
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

	/// <summary>
	/// Issue #743 AC "resolved inputs and sudo settings reach the invocation": sudo
	/// policy comes from the CATALOG (frozen on the plan item, migration 0074), not from
	/// the credential row and not from the target kind. Each case seeds a credential
	/// whose own <c>sudo_enabled</c> DISAGREES with the catalog policy, so a
	/// credential-driven implementation would produce the opposite line -- proving the
	/// catalog is authoritative. Covers the three documented product shapes: Photon
	/// (sudo, passwordless), vIDM (sudo, password required), Aria (no sudo).
	/// </summary>
	[Theory]
	[InlineData(true, false, false)]  // Photon shape; credential says sudo-disabled, catalog wins.
	[InlineData(true, true, false)]   // vIDM shape; credential says sudo-disabled, catalog wins.
	[InlineData(false, true, true)]   // Aria shape; credential says sudo-ENABLED, catalog still wins (no --sudo).
	public async Task SshProductComponentJob_SudoPolicyComesFromCatalog_NotFromCredential(
		bool requiresSudo, bool sudoRequiresPassword, bool credentialSudoEnabled)
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		string suffix = $"{requiresSudo}-{sudoRequiresPassword}-{credentialSudoEnabled}";
		(Guid targetId, Guid credentialId) = await SeedSrgTargetAsync(
			$"invented-sudo-policy-secret-{suffix}", sudoEnabled: credentialSudoEnabled); // gitleaks:allow -- invented test canary
		(Guid executionProfileId, Guid baselineId, string _) = await SeedSshCatalogAndBaselineAsync(
			$"sudo-{suffix}", CatalogSelectorKinds.Target, CatalogOutputKinds.Hdf, selectorName: null, materializeOnDisk: true);

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = "ssh",
			selector_kind = "target",
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = "hdf",
			requires_sudo = requiresSudo,
			sudo_requires_password = sudoRequiresPassword,
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 6, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);

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
		string expected = $"sudo={requiresSudo} sudoRequiresPassword={sudoRequiresPassword}";
		Assert.True(await ScanLogContainsAsync(jobIds[0], expected),
			$"expected the ssh invocation to carry the CATALOG sudo policy ('{expected}'), not the credential's own flag.");

		// The operator's ONLY signal for the mismatch case (catalog demands sudo, the
		// stored credential is marked sudo-disabled): a job.log event at WARNING
		// severity naming the disagreement. Asserted in BOTH directions -- emitted
		// exactly when the two disagree that way, and never otherwise -- so the WARN
		// can neither regress into silence nor become noise on an agreeing pair.
		bool expectMismatchWarn = requiresSudo && !credentialSudoEnabled;
		Assert.Equal(
			expectMismatchWarn,
			await WarnLogContainsAsync(jobIds[0], "the catalog component requires sudo but the stored ssh credential is marked sudo-disabled"));
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

	/// <summary>
	/// Issue #586 (epic #582): a job carrying an AD HOC per-purpose
	/// <c>job_credential_bindings</c> snapshot (<c>IsRunSecret</c> true, no
	/// <c>credential_id</c>) decrypts the target's own <c>run_secrets</c> row -- keyed by
	/// <c>RunSecretKey.For(targetId, "vsphere-api")</c> -- and never falls back to the
	/// target's STORED credential, even though this target has one (proven via the
	/// canary machinery, mirroring <see cref="RunSecret_UsedWhenJobCredentialIdIsNull_NeverFallsBackToStoredCredential"/>
	/// for the new per-purpose shape). Terminal run completion deletes the per-target
	/// row exactly as it deletes the legacy flat one -- the unconditional
	/// completion-transaction delete (<c>DeleteRunSecretIfPresentAsync</c>) is
	/// run_id-scoped, so it covers this shape with no code change (issue #586's
	/// migration/cleanup design point).
	/// </summary>
	[Fact]
	public async Task AdHocPurposeRunSecret_UsedWhenSnapshotIsRunSecret_NeverFallsBackToStoredCredential()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");
		(Guid targetId, _) = await SeedVsphereTargetAsync("invented-unused-stored-secret-586", username: "stored-user@example.internal");

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		const string adHocSecretValue = "invented-adhoc-purpose-canary-7b2f"; // gitleaks:allow — invented test canary, asserted never to reach the stub or any persistence surface
		await _runSecrets.StoreAsync(
			runId, RunSecretKey.For(targetId, CredentialPurposes.VSphereApi),
			new RunSecretCredential("adhoc-purpose-user@example.internal", adHocSecretValue), "tester", TimeSpan.FromHours(1), CancellationToken.None);

		string payload = JsonSerializer.Serialize(new { target_id = targetId });
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec(
				"scan", 3, TargetId: targetId, Payload: payload,
				CredentialBindings: [new JobCredentialBindingSpec(CredentialPurposes.VSphereApi, CredentialId: null, IsRunSecret: true)])],
			"tester", CancellationToken.None);

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

		// Terminal run completion deletes ALL of the run's run_secrets rows, including
		// this per-target/per-purpose one -- proving the unconditional
		// DeleteRunSecretIfPresentAsync delete (run_id-scoped, not shape-aware) covers
		// the new shape with no code change.
		using DecryptedRunSecret? afterTerminal = await _runSecrets.DecryptAsync(
			runId, RunSecretKey.For(targetId, CredentialPurposes.VSphereApi), jobIds[0], "test-verify", CancellationToken.None);
		Assert.Null(afterTerminal);

		await AssertCanaryNeverLeakedAsync(adHocSecretValue, credentialId: null);
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

	/// <summary>
	/// Issue #744 AC "preserve raw HDF/CKL when metadata correction or upload fails":
	/// a convert-stage failure (SAF conversion itself failing, the stub's 'failure'
	/// mode) must never delete or truncate the raw HDF report already persisted by the
	/// InSpec stage -- the evidence artifact survives a downstream failure so an
	/// operator can retry or inspect it, and the job fails honestly (state=failed)
	/// rather than silently discarding what was already collected.
	/// </summary>
	[Fact]
	public async Task ConvertStageFailure_PreservesRawHdfArtifact_FailsHonestly()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "failure");
		(Guid targetId, Guid credentialId) = await SeedVsphereTargetAsync("invented-preserve-hdf-canary");

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

		// Fails honestly -- never masquerades as succeeded.
		Assert.Equal("failed", await GetJobFieldAsync(jobIds[0], "state"));
		Assert.Equal("converting", await GetJobFieldAsync(jobIds[0], "stage"));

		// The raw HDF (or attested report) evidence artifact is never destroyed by the
		// convert failure -- it is exactly what a later retry/resume reads back.
		string hdfPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.json");
		string attestedPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.attested.json");
		Assert.True(
			File.Exists(hdfPath) || File.Exists(attestedPath),
			"expected the raw HDF or attested report to survive a convert-stage failure.");

		// No CKL was ever produced (the failure happened before any CKL existed) --
		// distinct from the "CKL exists but upload failed" preservation case, which
		// StigManagerUploadApiTests' Retry_RepeatFailure_Returns200NotError already
		// covers (the CKL is never deleted there either).
		string cklPath = Path.Combine(_artifactDirectory, $"{jobIds[0]:N}.ckl");
		Assert.False(File.Exists(cklPath));
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

	/// <summary>
	/// Issue #1068 / PR #1224 review round 1 finding 3: same seed as
	/// <see cref="SeedVsphereTargetAsync"/> but with the two facts the convert stage
	/// derives CKL asset identity from under the test's control -- the target's own
	/// name and its <c>connection.host</c> -- so both arms of the handler's
	/// <c>IPAddress.TryParse</c> Ip-vs-Fqdn split can be exercised end to end.
	/// </summary>
	private async Task<(Guid TargetId, Guid CredentialId)> SeedVsphereTargetWithAssetFactsAsync(
		string secretValue, string targetName, string connectionHost)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-scan-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared,
			sudoEnabled: false, CancellationToken.None, "administrator@example.internal"))!.Value;
		await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);

		string connectionJson = JsonSerializer.Serialize(new { host = connectionHost });
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, targetName, connectionJson, credentialId, CancellationToken.None);
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
