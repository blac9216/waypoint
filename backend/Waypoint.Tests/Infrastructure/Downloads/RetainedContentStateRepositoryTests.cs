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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1406, migration 0107: <c>download_retained_content_state</c> against real
/// Postgres -- round-trip through <see cref="RetainedContentStateRepository"/>, and
/// that the repository enforces <see cref="RetainedContentStateTransitions"/> rather
/// than trusting the caller (issue AC: "invalid state transitions... are rejected").
/// </summary>
[Collection("Postgres")]
public sealed class RetainedContentStateRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private RetainedContentStateRepository _repository = null!;

	public RetainedContentStateRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new RetainedContentStateRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand deleteState = new("DELETE FROM download_retained_content_state", connection);
		await deleteState.ExecuteNonQueryAsync();
		await using NpgsqlCommand deleteArtifacts = new("DELETE FROM depot_artifacts", connection);
		await deleteArtifacts.ExecuteNonQueryAsync();
	}

	private static async Task<Guid> InsertDepotArtifactAsync(NpgsqlConnection connection, string externalId)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO depot_artifacts (external_id) VALUES ($1) RETURNING id", connection);
		command.Parameters.AddWithValue(externalId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	[Fact]
	public async Task EnsureTrackedAsync_ThenGetAsync_RoundTrips()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-round-trip");

		Guid id = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		RetainedContentState? state = await _repository.GetAsync(id, CancellationToken.None);
		Assert.NotNull(state);
		Assert.Equal(artifactId, state!.DepotArtifactId);
		Assert.Equal(RetainedContentStates.Tracked, state.State);
		Assert.Null(state.PinnedBy);
		Assert.Null(state.PurgedAt);
	}

	[Fact]
	public async Task EnsureTrackedAsync_CalledTwiceForSameArtifact_IsIdempotent()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-idempotent");

		Guid first = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		Guid second = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		Assert.Equal(first, second);
	}

	[Fact]
	public async Task TransitionAsync_LegalMove_UpdatesState()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-transition");
		Guid id = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		await _repository.TransitionAsync(id, RetainedContentStates.Grace, CancellationToken.None);

		RetainedContentState? state = await _repository.GetAsync(id, CancellationToken.None);
		Assert.NotNull(state);
		Assert.Equal(RetainedContentStates.Grace, state!.State);
		Assert.NotNull(state.GraceStartedAt);
	}

	[Fact]
	public async Task TransitionAsync_PurgedToTracked_ThrowsInvalidOperationException()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-purged-guard");
		Guid id = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.Grace, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.PendingPurge, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.Purged, CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _repository.TransitionAsync(id, RetainedContentStates.Tracked, CancellationToken.None));

		RetainedContentState? state = await _repository.GetAsync(id, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Purged, state!.State);
	}

	[Fact]
	public async Task PinAsync_FromTracked_RecordsPinMetadata()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-pin");
		Guid id = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		await _repository.PinAsync(id, "operator-1", "keep for audit", CancellationToken.None);

		RetainedContentState? state = await _repository.GetAsync(id, CancellationToken.None);
		Assert.NotNull(state);
		Assert.Equal(RetainedContentStates.Pinned, state!.State);
		Assert.Equal("operator-1", state.PinnedBy);
		Assert.NotNull(state.PinnedAt);
		Assert.Equal("keep for audit", state.PinNote);
	}

	[Fact]
	public async Task PinAsync_OnAlreadyPurgedContent_ThrowsInvalidOperationException()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid artifactId = await InsertDepotArtifactAsync(connection, "retained-content-pin-purged-guard");
		Guid id = await _repository.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.Grace, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.PendingPurge, CancellationToken.None);
		await _repository.TransitionAsync(id, RetainedContentStates.Purged, CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _repository.PinAsync(id, "operator-1", null, CancellationToken.None));
	}

	[Fact]
	public async Task ListByStateAsync_ReturnsOnlyMatchingState()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid trackedArtifactId = await InsertDepotArtifactAsync(connection, "retained-content-list-tracked");
		Guid gracedArtifactId = await InsertDepotArtifactAsync(connection, "retained-content-list-grace");
		Guid trackedId = await _repository.EnsureTrackedAsync(trackedArtifactId, null, CancellationToken.None);
		Guid gracedId = await _repository.EnsureTrackedAsync(gracedArtifactId, null, CancellationToken.None);
		await _repository.TransitionAsync(gracedId, RetainedContentStates.Grace, CancellationToken.None);

		IReadOnlyList<RetainedContentState> tracked = await _repository.ListByStateAsync(RetainedContentStates.Tracked, CancellationToken.None);

		Assert.Contains(tracked, s => s.Id == trackedId);
		Assert.DoesNotContain(tracked, s => s.Id == gracedId);
	}
}
