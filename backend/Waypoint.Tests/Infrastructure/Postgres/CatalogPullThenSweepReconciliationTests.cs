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
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1784: a stack that both connected-pulls the vendor catalog and runs the
/// offline presence sweep must write exactly ONE row per real artifact, not two under
/// two different identities with contradictory statuses (live validation, epic #1704
/// run 2 -- 1291 pull rows + 1291 sweep rows served as 2582 for 1291 real artifacts).
///
/// This test drives real Postgres through both write paths against the shared
/// <c>depot-mini</c> fixture (issue #1696). "Pull" runs the REAL, unmodified
/// <see cref="CatalogPullJobHandler"/> (not a hand-rolled re-implementation of its
/// parse/reconcile/upsert sequence -- PR #1805 round-1 review finding 2: a prior
/// version of this test copied the handler's legacy-identity derivation inline, which
/// meant nothing here actually exercised <see cref="IDepotArtifactRepository.RekeyManyAsync"/>'s
/// real call site) with a <see cref="FakeMetadataPuller"/> standing in for the vendor
/// tool process (writes the fixture's own catalog document to the staged depot path)
/// and a <see cref="FakeCatalogVerifier"/> standing in for signature authentication
/// (identity reconciliation is independent of that step; the real verifier is already
/// exercised end to end by <c>CatalogPullEndToEndTests</c>). "Sweep" runs the REAL,
/// unmodified <c>Invoke-WaypointCatalogIndex</c> (issue #1503) over an independent
/// materialization of the SAME fixture via the shared parity runner script
/// (<see cref="Parity.DepotMiniCatalogParityContractTests"/>'s own sibling), and each
/// <c>ArtifactPresence</c> record is upserted exactly as
/// <c>CatalogIndexJobHandler.ProcessSweepOutputAsync</c> would.
///
/// A pre-existing row under the LEGACY pre-#1784 bare-fileName identity is seeded
/// before the pull step, proving the handler's own reconciliation rename (never a
/// delete, design #16 section 2's never-auto-remove policy) folds it onto the new
/// identity on the very next pull -- no migration, no one-time backfill. Because the
/// pull now runs through the handler's own code path, a fake whose <c>RekeyManyAsync</c>
/// throws makes this test fail (verified by splice at fix time, restored -- see the
/// round-1 Fixes Applied comment); before this rewrite, the same splice left the test
/// green, which was exactly the defect.
/// </summary>
[Collection("Postgres")]
public sealed class CatalogPullThenSweepReconciliationTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly ITestOutputHelper _output;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-pull-sweep-key").FullName;
	private readonly string _toolStatePath = Directory.CreateTempSubdirectory("wp-pull-sweep-tool-state").FullName;
	private readonly string _depotPath = Directory.CreateTempSubdirectory("wp-pull-sweep-depot").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private DepotArtifactRepository _artifacts = null!;
	private JobQueueRepository _jobs = null!;
	private JobEventPublisher _events = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;

	public CatalogPullThenSweepReconciliationTests(PostgresFixture fixture, ITestOutputHelper output)
	{
		_fixture = fixture;
		_output = output;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetArtifactsAsync();
		await ResetEnrollmentAsync();

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		AesGcmEnvelopeCipher cipher = new(new FileMasterKeyProvider(keyPath));
		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);

		Guid? credentialId = await new CredentialCreationCoordinator(_fixture.ConnectionString, cipher, NullLogger<CredentialCreationCoordinator>.Instance)
			.CreateAsync("VCF Software Depot Activation Code", "depot-activation-code", "shared", sudoEnabled: false, username: null,
				Encoding.UTF8.GetBytes("invented-pull-then-sweep-canary"), "test-actor", CancellationToken.None); // gitleaks:allow — invented test canary
		Assert.NotNull(credentialId);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_toolStatePath, recursive: true);
		Directory.Delete(_depotPath, recursive: true);
	}

	[Fact]
	public async Task PullThenSweep_OverDepotMini_YieldsOneRowPerCatalogArtifact_WithTheSweepsStatus()
	{
		using DepotMiniFixture fixture = new();

		// Seed a row under the LEGACY pre-#1784 identity (bare fileName) for
		// vcsa-patch.iso, simulating a pull that ran before this fix shipped.
		const string legacyIdentity = "vcsa-patch.iso";
		await _artifacts.UpsertAsync(new DepotArtifactUpsert(legacyIdentity, "aa11", DepotArtifactStatuses.Indexed, "{}"), CancellationToken.None);

		// "Pull": the REAL CatalogPullJobHandler, over the depot-mini fixture's own
		// catalog document -- exercises the handler's own RekeyManyAsync call site,
		// not a copy of it.
		JobExecutionOutcome pullOutcome = await RunPullAsync(fixture.CatalogJson);
		Assert.Equal(JobOutcomeKind.Succeeded, pullOutcome.Kind);

		(IReadOnlyList<DepotArtifact> afterPull, long afterPullTotal) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		Assert.Equal(20, afterPullTotal); // depot-mini/README.md's catalog: VCENTER 6 + NSX 1 + ESXI 1 + TKG 12.
		Assert.DoesNotContain(afterPull, a => a.ExternalId == legacyIdentity); // the legacy row was reconciled away.
		Assert.Contains(afterPull, a => a.ExternalId == "PROD/COMP/VCENTER/vcsa-patch.iso" && a.Status == DepotArtifactStatuses.Indexed);

		// "Sweep": the REAL WaypointCatalogIndex.psm1 over an independent
		// materialization of the same fixture (content-derived hashes converge without
		// sharing a directory -- see the parity runner script's own doc comment).
		List<PowerShellSweepRecord> swept = RunPowerShellSweep();
		int upsertedFromSweep = 0;
		foreach (PowerShellSweepRecord record in swept.Where(r => r.RecordType == "ArtifactPresence"))
		{
			await _artifacts.UpsertAsync(new DepotArtifactUpsert(record.RelativePath, null, record.Status!, "{}"), CancellationToken.None);
			upsertedFromSweep++;
		}

		_output.WriteLine($"Sweep upserted {upsertedFromSweep} ArtifactPresence record(s).");

		(IReadOnlyList<DepotArtifact> afterSweep, long afterSweepTotal) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);

		// 20 catalog identities (pull ∪ sweep -- SAME identities, one row each) + 1 new
		// identity the sweep alone reports (PROD/metadata/upgrade_info.xml, never a
		// catalog entry) = 21. If pull and sweep still disagreed on identity (the
		// #1784 defect), this would be 40, not 21.
		Assert.Equal(21, afterSweepTotal);
		Assert.Equal(afterSweepTotal, afterSweep.Select(a => a.ExternalId).Distinct(StringComparer.Ordinal).Count());

		// The row the pull wrote as "indexed" now carries the sweep's own
		// determination -- one row, one (the sweep's) status, not two rows disagreeing.
		DepotArtifact vcsaPatch = Assert.Single(afterSweep, a => a.ExternalId == "PROD/COMP/VCENTER/vcsa-patch.iso");
		Assert.Equal(DepotArtifactStatuses.Present, vcsaPatch.Status);

		DepotArtifact nsxMissing = Assert.Single(afterSweep, a => a.ExternalId == "PROD/COMP/NSX/nsx-missing.ova");
		Assert.Equal(DepotArtifactStatuses.Missing, nsxMissing.Status);
	}

	/// <summary>
	/// Issue #1818: over depot-mini's 20-artifact catalog, with ZERO legacy-identity
	/// rows present (a steady-state stack -- every prior pull already reconciled
	/// them), the pre-#1818 per-artifact shape called <c>RekeyAsync</c> 20 times
	/// (the guard fires on identity SHAPE, not on whether a legacy row exists -- see
	/// <see cref="IDepotArtifactRepository.RekeyManyAsync"/>'s own doc comment).
	/// <see cref="CountingArtifactRepository"/> wraps the real repository and counts
	/// calls to <see cref="IDepotArtifactRepository.RekeyManyAsync"/> only (never
	/// per-artifact) -- asserts exactly 1, not 20.
	/// </summary>
	[Fact]
	public async Task Pull_OverDepotMini_WithZeroLegacyRows_CallsRekeyManyAsyncOnceNotOncePerArtifact()
	{
		using DepotMiniFixture fixture = new();
		CountingArtifactRepository counting = new(_artifacts);

		JobExecutionOutcome pullOutcome = await RunPullAsync(fixture.CatalogJson, counting);

		Assert.Equal(JobOutcomeKind.Succeeded, pullOutcome.Kind);
		Assert.Equal(1, counting.RekeyManyAsyncCallCount); // one bounded call, not one per artifact (20).

		(IReadOnlyList<DepotArtifact> afterPull, long afterPullTotal) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		Assert.Equal(20, afterPullTotal);
		_ = afterPull;
	}

	/// <summary>Wraps a real <see cref="IDepotArtifactRepository"/>, counting only <see cref="RekeyManyAsync"/> calls -- proves the pull path's batching, not its per-artifact upsert count.</summary>
	private sealed class CountingArtifactRepository(IDepotArtifactRepository inner) : IDepotArtifactRepository
	{
		public int RekeyManyAsyncCallCount { get; private set; }

		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) =>
			inner.UpsertAsync(artifact, cancellationToken);

		public Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken)
		{
			RekeyManyAsyncCallCount++;
			return inner.RekeyManyAsync(renames, cancellationToken);
		}

		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
			inner.GetByIdAsync(id, cancellationToken);

		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken) =>
			inner.ListAsync(filter, page, cancellationToken);
	}

	/// <summary>Drives the real handler for the "pull" half of the scenario, standing in only for the vendor tool process and signature authentication (neither affects catalog identity). <paramref name="artifacts"/> defaults to the real repository -- overridable so a test can wrap it (e.g. <see cref="CountingArtifactRepository"/>).</summary>
	private async Task<JobExecutionOutcome> RunPullAsync(string catalogJson, IDepotArtifactRepository? artifacts = null)
	{
		ManagedToolOptions toolOptions = new() { ToolStatePath = _toolStatePath };
		CatalogOptions catalogOptions = new() { DepotPath = _depotPath };
		CatalogPullStateRepository pullState = new(_fixture.ConnectionString);
		CatalogPullJobHandler handler = new(
			new ValidatedEnrollmentRepository(), new NoOpIdentityTool(), new FakeMetadataPuller(catalogJson), new FakeCatalogVerifier(),
			artifacts ?? _artifacts, pullState, _secretStore, _credentials, _redactor, Options.Create(catalogOptions), Options.Create(toolOptions));

		Guid runId = await _jobs.CreateRunAsync("catalog-pull", "{}", credentialId: null, "test-actor", CancellationToken.None);
		JobSpec spec = new("catalog-pull", 1, TargetId: null, TargetName: "depot", Payload: "{}");
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, [spec], "test-actor", CancellationToken.None);
		ClaimedJob? claimed = await _jobs.ClaimJobAsync(
			"worker-test", TimeSpan.FromMinutes(5), new HashSet<string>(StringComparer.Ordinal) { "catalog-pull" }, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(jobIds[0], claimed!.Id);

		JobExecutionContext context = new(claimed, "worker-test", _events, _jobs, JobShape.Simple);
		return await handler.ExecuteAsync(context, CancellationToken.None);
	}

	private sealed class ValidatedEnrollmentRepository : IDepotEnrollmentRepository
	{
		public Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken) =>
			Task.FromResult<DepotEnrollment?>(new DepotEnrollment(
				DepotEnrollmentStates.Validated, "WPT-0001-DEPOT-ID", DateTimeOffset.UtcNow, "WPT-0001-DEPOT-ID", DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow));

		public Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
		public Task SetPairedAsync(string assetId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
		public Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
		public Task ResetAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
	}

	private sealed class NoOpIdentityTool : IDepotIdentityTool
	{
		public Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
		public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected during a pull.");
	}

	/// <summary>Stands in for the vendor tool process: writes the fixture's own catalog document to the staged depot path the handler hands it.</summary>
	private sealed class FakeMetadataPuller(string catalogJson) : IManagedToolMetadataPuller
	{
		public Task<CatalogPullResult> PullAsync(string depotPath, string activationCodePath, CancellationToken cancellationToken)
		{
			string metadataDir = Path.Combine(depotPath, "PROD", "metadata", "productVersionCatalog", "v1");
			Directory.CreateDirectory(metadataDir);
			File.WriteAllText(Path.Combine(metadataDir, "productVersionCatalog.json"), catalogJson);
			return Task.FromResult(CatalogPullResult.Ok());
		}
	}

	/// <summary>Stands in for signature authentication -- identity reconciliation does not depend on it; the real verifier is exercised end to end by <c>CatalogPullEndToEndTests</c>.</summary>
	private sealed class FakeCatalogVerifier : IManagedToolCatalogVerifier
	{
		public Task<ManagedToolCatalogAuthenticationResult> AuthenticateCatalogAsync(string repositoryRoot, CancellationToken cancellationToken) =>
			Task.FromResult(ManagedToolCatalogAuthenticationResult.Ok());

		public Task<ManagedToolCatalogVerificationResult> VerifyAsync(string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken) =>
			throw new NotSupportedException("catalog-pull only uses AuthenticateCatalogAsync.");
	}

	private static List<PowerShellSweepRecord> RunPowerShellSweep()
	{
		string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		string runnerScript = Path.Combine(repoRoot, "backend", "Waypoint.Tests", "Assets", "DepotMiniParityRunner", "Invoke-DepotMiniParitySweep.ps1");
		Assert.True(File.Exists(runnerScript), $"expected the parity runner script at '{runnerScript}'");

		string outputPath = Path.Combine(Path.GetTempPath(), $"wp-depot-mini-pull-then-sweep-{Guid.NewGuid():N}.json");
		try
		{
			ProcessStartInfo startInfo = new("pwsh")
			{
				ArgumentList = { "-NoProfile", "-File", runnerScript, "-RepoRoot", repoRoot, "-OutputPath", outputPath },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			using Process process = Process.Start(startInfo)!;
			string stderr = process.StandardError.ReadToEnd();
			process.WaitForExit(120_000);

			Assert.True(process.HasExited, "PowerShell parity runner did not exit within 120s");
			Assert.True(process.ExitCode == 0, $"PowerShell parity runner failed (exit {process.ExitCode}):\n{stderr}");

			string json = File.ReadAllText(outputPath);
			return JsonSerializer.Deserialize<List<PowerShellSweepRecord>>(json)!;
		}
		finally
		{
			File.Delete(outputPath);
		}
	}

	private async Task ResetArtifactsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}

	private async Task ResetEnrollmentAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'validated', depot_id = 'WPT-0001-DEPOT-ID', depot_id_generated_at = now(),
			    paired_asset_id = 'WPT-0001-DEPOT-ID', paired_at = now(), last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private sealed record PowerShellSweepRecord(string RecordType, string RelativePath, string? Status);
}
