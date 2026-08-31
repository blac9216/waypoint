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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Round-1 review finding 3 on issue #1503/PR #1629: <see cref="CatalogIndexJobHandlerEndToEndTests"/>
/// drives the real <see cref="CatalogIndexJobHandler"/> against
/// <c>WaypointCatalogIndexStubModule.psm1</c>, a hand-written stand-in for
/// <c>Invoke-WaypointCatalogIndex</c> itself -- so it cannot catch a contract break
/// between the handler's <c>TryParseArtifact</c> and the REAL, rewritten module's
/// output shape (the #1503 rewrite dropped the <c>ExternalId</c> property the live
/// handler required, so a real job indexed zero artifacts while still reporting
/// success). This suite instead preloads the REAL <c>WaypointCatalogIndex.psm1</c>
/// (only its sibling-repo dot-source target, <c>Get-FileManifest</c>, is faked --
/// see <c>Assets/WaypointCatalogIndexRealModuleFake</c>) through the full
/// fan-out -&gt; dispatch -&gt; handler -&gt; upsert loop, so a regression in the
/// module/handler contract fails a real assertion instead of a green stub-driven
/// suite.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the depot dir.
public sealed class CatalogIndexJobHandlerRealModuleEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string LoggingModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointLogging", "WaypointLogging.psm1");

	private static readonly string CatalogIndexModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointCatalogIndex", "WaypointCatalogIndex.psm1");

	private static readonly string FakeCommonPath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointCatalogIndexRealModuleFake", "WaypointCatalogIndexRealModuleFake.ps1");

	private readonly PostgresFixture _fixture;
	private readonly string _depotDirectory = Directory.CreateTempSubdirectory("wp-catalog-real").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CatalogIndexJobHandler _handler = null!;
	private DepotArtifactRepository _artifacts = null!;
	private string? _previousCommonPathEnv;

	public CatalogIndexJobHandlerRealModuleEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;

		Assert.True(File.Exists(Path.GetFullPath(LoggingModulePath)), $"expected the adapter at '{Path.GetFullPath(LoggingModulePath)}'");
		Assert.True(File.Exists(Path.GetFullPath(CatalogIndexModulePath)), $"expected WaypointCatalogIndex.psm1 at '{Path.GetFullPath(CatalogIndexModulePath)}'");
		Assert.True(File.Exists(FakeCommonPath), $"expected the fake common script at '{FakeCommonPath}'");
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetArtifactsAsync();

		string catalogPath = Path.Combine(_depotDirectory, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
		await File.WriteAllTextAsync(
			catalogPath,
			"""
			{ "patches": { "VCENTER": [ { "productVersion": "9.1.0.5210.25573614",
			  "artifacts": { "bundles": [ { "binaries": [
			    { "fileName": "vcsa-patch.iso", "checksum": "AAAA", "size": 100 } ] } ] } } ] } }
			""");

		// The module reads WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH at import time
		// (module-scoped $Script:VcfDownloadManagerCommonPath) -- must be set before
		// the runspace pool preloads the module below.
		_previousCommonPathEnv = Environment.GetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH");
		Environment.SetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH", FakeCommonPath);

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(Path.GetFullPath(LoggingModulePath));
		powerShellOptions.ModulePreloadPaths.Add(Path.GetFullPath(CatalogIndexModulePath));
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);

		CatalogOptions catalogOptions = new() { DepotPath = _depotDirectory };
		_handler = new CatalogIndexJobHandler(executor, _artifacts, _redactor, Options.Create(catalogOptions), wrappedPsOptions);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH", _previousCommonPathEnv);
		Directory.Delete(_depotDirectory, recursive: true);
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
	/// The real module, driven through the real handler, against a real (fabricated)
	/// on-disk catalog: one matched artifact must reach <c>depot_artifacts</c>. Before
	/// the finding-3 fix (module dropped <c>ExternalId</c>), this asserted 0 -- the
	/// job still reported "done", the exact silent failure the review caught.
	/// </summary>
	[Fact]
	public async Task SyncToDispatchToHandler_WithRealModule_IndexesTheMatchedArtifact()
	{
		Guid runId = await _repository.CreateRunAsync("catalog-index", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("catalog-index", 1, TargetName: "depot")], "tester", CancellationToken.None);

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

		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest(), CancellationToken.None);
		Assert.Equal(1, total);
		DepotArtifact artifact = Assert.Single(items);

		// Round-2 review finding 2: ExternalId must be the depot-relative path
		// (PROD/COMP/<Product>/<fileName>), not the bare catalog fileName -- the live
		// CatalogIndexJobHandler.TryParseArtifact persists it straight through as
		// DepotArtifactUpsert.RelativePath (#1488's rekeyed identity), so a bare value
		// here would duplicate rows on every subsequent sweep instead of updating them.
		Assert.Equal("PROD/COMP/VCENTER/vcsa-patch.iso", artifact.ExternalId);
		Assert.Equal("present", artifact.Status);
	}

	/// <summary>
	/// <see cref="PostgresFixture.ResetJobEngineDataAsync"/> deliberately does not
	/// truncate <c>depot_artifacts</c> (it is not job-engine data, and other suites
	/// seed/read it independently) -- see <c>CatalogPullEndToEndTests</c>'s matching
	/// helper. This suite's row-count assertion needs a known-empty starting table,
	/// not whatever rows a prior test in this shared Postgres instance left behind.
	/// </summary>
	private async Task ResetArtifactsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}
}
