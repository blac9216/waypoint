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
using Npgsql;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.SystemState;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #443 against real Postgres: <see cref="WorkerRegistryRepository"/>'s write path
/// (<see cref="IWorkerRegistryWriter.HeartbeatAsync"/>) exercised end to end through the
/// migrated <c>worker_registry</c> table and read back through
/// <see cref="IWorkerRegistryReader.ListAsync"/>. Closes the AC-4 write-side gap: the
/// <c>INSERT ... ON CONFLICT</c> upsert was previously only exercised by the live
/// compose bring-up. The read path's controller/shape wiring is covered by
/// <c>SystemApiTests</c>/<c>SystemEndpointTests</c>; this covers the upsert semantics
/// (first insert, update-on-conflict, JSONB capability/allowlist round-trip, and
/// last-seen advancing to drive the reader's staleness ordering).
/// </summary>
[Collection("Postgres")]
public sealed class WorkerRegistryRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private WorkerRegistryRepository _registry = null!;

	public WorkerRegistryRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_registry = new WorkerRegistryRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task HeartbeatAsync_FirstCall_InsertsRow_ReadBackRoundTrips()
	{
		string[] jobTypes = ["download", "catalog-index"];

		await _registry.HeartbeatAsync("download-runner-1", jobTypes, ready: true, CancellationToken.None);

		WorkerHeartbeat row = Assert.Single(await _registry.ListAsync(CancellationToken.None));
		Assert.Equal("download-runner-1", row.WorkerId);
		Assert.True(row.Ready);
		// JSONB allowlist survives the round trip in order.
		Assert.Equal(jobTypes, row.JobTypes);
	}

	[Fact]
	public async Task HeartbeatAsync_SecondCall_UpdatesInPlace_NoDuplicateRow()
	{
		await _registry.HeartbeatAsync("compliance-runner-1", ["scan"], ready: false, CancellationToken.None);
		DateTimeOffset firstSeen = (await _registry.ListAsync(CancellationToken.None))[0].LastSeenAt;

		// A change in both the allowlist and the readiness flag must overwrite the same
		// row (worker_id is the primary key; ON CONFLICT DO UPDATE), never append.
		string[] updatedJobTypes = ["scan", "remediate"];
		await _registry.HeartbeatAsync("compliance-runner-1", updatedJobTypes, ready: true, CancellationToken.None);

		WorkerHeartbeat row = Assert.Single(await _registry.ListAsync(CancellationToken.None));
		Assert.Equal("compliance-runner-1", row.WorkerId);
		Assert.True(row.Ready);
		Assert.Equal(updatedJobTypes, row.JobTypes);
		// last_seen_at is set to now() on every upsert, so the update advances it.
		Assert.True(row.LastSeenAt >= firstSeen, $"expected last_seen_at to advance: {row.LastSeenAt} < {firstSeen}");
	}

	[Fact]
	public async Task ListAsync_OrdersByLastSeenDescending_ForStalenessReads()
	{
		// Two distinct workers; the second one heartbeats last, so it must sort first
		// (ListAsync orders last_seen_at DESC -- the ordering GET /system's staleness
		// grouping relies on).
		await _registry.HeartbeatAsync("download-runner-1", ["download"], ready: true, CancellationToken.None);
		await ForceOlderLastSeenAsync("download-runner-1", TimeSpan.FromMinutes(5));
		await _registry.HeartbeatAsync("compliance-runner-1", ["scan"], ready: true, CancellationToken.None);

		IReadOnlyList<WorkerHeartbeat> rows = await _registry.ListAsync(CancellationToken.None);

		Assert.Equal(2, rows.Count);
		Assert.Equal("compliance-runner-1", rows[0].WorkerId);
		Assert.Equal("download-runner-1", rows[1].WorkerId);
		Assert.True(rows[0].LastSeenAt > rows[1].LastSeenAt);
	}

	private async Task ForceOlderLastSeenAsync(string workerId, TimeSpan age)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand update = new(
			"UPDATE worker_registry SET last_seen_at = now() - $2 WHERE worker_id = $1", connection);
		update.Parameters.AddWithValue(workerId);
		update.Parameters.AddWithValue(age);
		await update.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE worker_registry RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
