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
/// Issue #1705 Option B (defense in depth): a row whose status the
/// <c>depot_artifacts_status_check</c> constraint rejects must be skipped and
/// counted, not allowed to abort the whole <c>catalog-index</c> job -- the exact
/// wholesale-failure mode #1705 found live before migration 0129 widened the
/// constraint for <c>'missing'</c>. This suite exercises the surviving general case
/// (a status the constraint still does not allow) through the real dispatcher/
/// handler/repository loop via <c>WaypointCatalogIndexBadStatusStubModule</c>, which
/// emits two valid rows and one deliberately-rejected one.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class CatalogIndexJobHandlerRejectedRowEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointCatalogIndexBadStatusStubModule", "WaypointCatalogIndexBadStatusStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-catalog-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private CatalogIndexJobHandler _handler = null!;
	private DepotArtifactRepository _artifacts = null!;
	private UnknownCatalogFileRepository _unknownFiles = null!;

	public CatalogIndexJobHandlerRejectedRowEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

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

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_unknownFiles = new UnknownCatalogFileRepository(_fixture.ConnectionString);

		CatalogOptions catalogOptions = new() { DepotPath = "/invented/depot" };
		_handler = new CatalogIndexJobHandler(executor, _artifacts, _unknownFiles, _redactor, Options.Create(catalogOptions), wrappedPsOptions);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
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
	/// The one rejected row (an invalid status the constraint has never allowed) is
	/// skipped and counted -- the job still ends <c>done</c> (not
	/// <c>completed_with_failures</c>/failed), both valid rows persist, and a
	/// <c>job.log</c> warning names the rejected artifact.
	/// </summary>
	[Fact]
	public async Task OneRejectedRow_IsSkippedAndCounted_RemainingRowsPersist_JobStillSucceeds()
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

		// The shared [Collection("Postgres")] fixture's depot_artifacts table is not
		// truncated between every test class, so this asserts on this test's own
		// wp1705-badstatus-* identities rather than a table-wide row count.
		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter("VCF", null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		Assert.Contains(items, item => item.ExternalId == "wp1705-badstatus-artifact-1");
		Assert.Contains(items, item => item.ExternalId == "wp1705-badstatus-artifact-2");
		Assert.DoesNotContain(items, item => item.ExternalId == "wp1705-badstatus-artifact-bad");

		Assert.True(await WarningJobLogExistsAsync(jobIds[0]), await DumpJobLogAsync(jobIds[0]));
		_ = total;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task<bool> WarningJobLogExistsAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"""
			SELECT count(*) FROM job_events
			WHERE job_id = $1 AND event_type = $2 AND payload->>'severity' = 'warning'
			""", connection);
		query.Parameters.AddWithValue(jobId);
		query.Parameters.AddWithValue(JobEventTypes.JobLog);
		return (long)(await query.ExecuteScalarAsync())! > 0;
	}

	private async Task<string> DumpJobLogAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT event_type, payload::text FROM job_events WHERE job_id = $1 ORDER BY seq", connection);
		query.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
		List<string> lines = [];
		while (await reader.ReadAsync())
		{
			lines.Add($"{reader.GetString(0)}: {reader.GetString(1)}");
		}

		return string.Join(" | ", lines);
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
