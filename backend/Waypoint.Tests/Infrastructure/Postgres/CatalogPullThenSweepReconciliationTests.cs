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
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Pagination;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
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
/// <c>depot-mini</c> fixture (issue #1696): "pull" is simulated by parsing the fixture's
/// catalog document with the real <see cref="VendorProductVersionCatalogParser"/> and
/// upserting through the real <see cref="DepotArtifactRepository"/> (the full
/// <c>CatalogPullJobHandler</c> also decrypts credentials, authenticates, and promotes
/// the on-disk catalog -- none of that machinery affects catalog IDENTITY, which is
/// the one thing this test is proving); "sweep" runs the REAL, unmodified
/// <c>Invoke-WaypointCatalogIndex</c> (issue #1503) over an independent materialization
/// of the SAME fixture via the shared parity runner script
/// (<see cref="Parity.DepotMiniCatalogParityContractTests"/>'s own sibling), and each
/// <c>ArtifactPresence</c> record is upserted exactly as
/// <c>CatalogIndexJobHandler.ProcessSweepOutputAsync</c> would.
///
/// A pre-existing row under the LEGACY pre-#1784 bare-fileName identity is seeded
/// before the pull step, proving <c>CatalogPullJobHandler</c>'s reconciliation rename
/// (<see cref="IDepotArtifactRepository.RekeyAsync"/> -- never a delete, design #16
/// section 2's never-auto-remove policy) folds it onto the new identity on the very
/// next pull -- no migration, no one-time backfill.
/// </summary>
[Collection("Postgres")]
public sealed class CatalogPullThenSweepReconciliationTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private readonly ITestOutputHelper _output;
	private DepotArtifactRepository _artifacts = null!;

	public CatalogPullThenSweepReconciliationTests(PostgresFixture fixture, ITestOutputHelper output)
	{
		_fixture = fixture;
		_output = output;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetArtifactsAsync();
		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task PullThenSweep_OverDepotMini_YieldsOneRowPerCatalogArtifact_WithTheSweepsStatus()
	{
		using DepotMiniFixture fixture = new();

		// Seed a row under the LEGACY pre-#1784 identity (bare fileName) for
		// vcsa-patch.iso, simulating a pull that ran before this fix shipped.
		const string legacyIdentity = "vcsa-patch.iso";
		await _artifacts.UpsertAsync(new DepotArtifactUpsert(legacyIdentity, "aa11", DepotArtifactStatuses.Indexed, "{}"), CancellationToken.None);

		// "Pull": parse with the real parser, reconciling any legacy-identity row onto
		// the new identity (rename, never delete) BEFORE upserting -- exactly the order
		// CatalogPullJobHandler uses.
		IReadOnlyList<DepotArtifactUpsert> pulled = VendorProductVersionCatalogParser.Parse(fixture.CatalogJson);
		foreach (DepotArtifactUpsert upsert in pulled)
		{
			string legacy = upsert.RelativePath[(upsert.RelativePath.LastIndexOf('/') + 1)..];
			if (!string.Equals(legacy, upsert.RelativePath, StringComparison.Ordinal))
			{
				await _artifacts.RekeyAsync(legacy, upsert.RelativePath, CancellationToken.None);
			}

			await _artifacts.UpsertAsync(upsert, CancellationToken.None);
		}

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

	private sealed record PowerShellSweepRecord(string RecordType, string RelativePath, string? Status);
}
