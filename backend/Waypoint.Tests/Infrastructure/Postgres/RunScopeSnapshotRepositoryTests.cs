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

using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #733 (epic #726 Wave 2, ADR-0023), against a real PostgreSQL 16 container:
/// <c>run_scope_snapshots</c> (migration 0056) round-trips the frozen requested-versus-
/// resolved scope so run history can display both (issue #733 AC). One row per run
/// (UNIQUE run_id), ON DELETE CASCADE off <c>runs</c>.
/// </summary>
[Collection("Postgres")]
public sealed class RunScopeSnapshotRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private RunScopeSnapshotRepository _repository = null!;

	public RunScopeSnapshotRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE TABLE run_scope_snapshots, jobs, runs RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();

		_repository = new RunScopeSnapshotRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	[Fact]
	public async Task RecordAsync_ThenGetForRunAsync_RoundTripsRequestedAndResolvedScope()
	{
		Guid runId = await SeedRunAsync();
		Guid resolvedComponent = Guid.NewGuid();
		Guid omittedComponent = Guid.NewGuid();
		Guid omittedTarget = Guid.NewGuid();

		List<ScopeOmission> omissions =
		[
			new ScopeOmission(omittedComponent, omittedTarget, ScopeOmissionReasons.ComponentAbsent, "was not observed by the most recent refresh"),
		];

		string requestedJson = System.Text.Json.JsonSerializer.Serialize(
			new TargetScopeRequest(TargetScopeModes.Explicit, null, [resolvedComponent, omittedComponent]));

		await _repository.RecordAsync(runId, TargetScopeModes.Explicit, requestedJson, [resolvedComponent], omissions, CancellationToken.None);

		RunScopeSnapshot? snapshot = await _repository.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(snapshot);
		Assert.Equal(runId, snapshot!.RunId);
		Assert.Equal(TargetScopeModes.Explicit, snapshot.RequestedMode);

		// JSONB round-trips semantically, not byte-for-byte (Postgres re-formats
		// whitespace) -- compare parsed values rather than the raw string.
		using (System.Text.Json.JsonDocument original = System.Text.Json.JsonDocument.Parse(requestedJson))
		using (System.Text.Json.JsonDocument roundTrippedJson = System.Text.Json.JsonDocument.Parse(snapshot.RequestedScopeJson))
		{
			Assert.Equal(original.RootElement.GetProperty("mode").GetString(), roundTrippedJson.RootElement.GetProperty("mode").GetString());
			Assert.Equal(
				original.RootElement.GetProperty("component_ids").EnumerateArray().Select(e => e.GetGuid()),
				roundTrippedJson.RootElement.GetProperty("component_ids").EnumerateArray().Select(e => e.GetGuid()));
		}

		Assert.Equal([resolvedComponent], snapshot.ResolvedComponentIds);

		ScopeOmission roundTripped = Assert.Single(snapshot.Omissions);
		Assert.Equal(omittedComponent, roundTripped.ComponentId);
		Assert.Equal(omittedTarget, roundTripped.TargetId);
		Assert.Equal(ScopeOmissionReasons.ComponentAbsent, roundTripped.Reason);
		Assert.Equal("was not observed by the most recent refresh", roundTripped.Detail);
	}

	[Fact]
	public async Task GetForRunAsync_NoSnapshotRecorded_ReturnsNull()
	{
		Guid runId = await SeedRunAsync();

		Assert.Null(await _repository.GetForRunAsync(runId, CancellationToken.None));
	}

	[Fact]
	public async Task RecordAsync_EmptyResolvedSetAndNoOmissions_RoundTripsAsEmptyNotNull()
	{
		// The honest empty-explicit-selection case (issue #733 AC) must persist as a
		// genuine empty array, not a null that later code might conflate with "no
		// snapshot recorded at all."
		Guid runId = await SeedRunAsync();
		string requestedJson = System.Text.Json.JsonSerializer.Serialize(new TargetScopeRequest(TargetScopeModes.Explicit, null, []));

		await _repository.RecordAsync(runId, TargetScopeModes.Explicit, requestedJson, [], [], CancellationToken.None);

		RunScopeSnapshot? snapshot = await _repository.GetForRunAsync(runId, CancellationToken.None);

		Assert.NotNull(snapshot);
		Assert.Empty(snapshot!.ResolvedComponentIds);
		Assert.Empty(snapshot.Omissions);
	}

	[Fact]
	public async Task RunDeleted_CascadesTheSnapshotRow()
	{
		Guid runId = await SeedRunAsync();
		await _repository.RecordAsync(runId, TargetScopeModes.All, "{}", [], [], CancellationToken.None);

		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand delete = new("DELETE FROM runs WHERE id = $1", connection);
			delete.Parameters.AddWithValue(runId);
			await delete.ExecuteNonQueryAsync();
		}

		Assert.Null(await _repository.GetForRunAsync(runId, CancellationToken.None));
	}
}
