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
/// Issue #1406, migration 0107: <c>download_retention_policies</c> against real
/// Postgres -- the seeded 'default' scope, upsert-by-scope-key semantics, and a
/// plain round-trip.
/// </summary>
[Collection("Postgres")]
public sealed class RetentionPolicyRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private RetentionPolicyRepository _repository = null!;

	public RetentionPolicyRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new RetentionPolicyRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM download_retention_policies WHERE scope_key <> 'default'", connection);
		await command.ExecuteNonQueryAsync();
		await using NpgsqlCommand reset = new(
			"""
			UPDATE download_retention_policies SET grace_period_days = 30, grace_max_refreshes = 0, manual_download_dial_default = 'review'
			WHERE scope_key = 'default'
			""", connection);
		await reset.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task GetByScopeKeyAsync_DefaultScope_IsSeededByTheMigration()
	{
		RetentionPolicy? policy = await _repository.GetByScopeKeyAsync(RetentionPolicyScopes.Default, CancellationToken.None);

		Assert.NotNull(policy);
		Assert.Equal(30, policy!.GracePeriodDays);
		Assert.Equal(0, policy.GraceMaxRefreshes);
		Assert.Equal(ManualDownloadDialOptions.Review, policy.ManualDownloadDialDefault);
	}

	[Fact]
	public async Task UpsertAsync_NewScope_ThenGetAsync_RoundTrips()
	{
		Guid id = await _repository.UpsertAsync("subscription-abc", 45, 2, ManualDownloadDialOptions.Keep, CancellationToken.None);

		RetentionPolicy? policy = await _repository.GetAsync(id, CancellationToken.None);

		Assert.NotNull(policy);
		Assert.Equal("subscription-abc", policy!.ScopeKey);
		Assert.Equal(45, policy.GracePeriodDays);
		Assert.Equal(2, policy.GraceMaxRefreshes);
		Assert.Equal(ManualDownloadDialOptions.Keep, policy.ManualDownloadDialDefault);
	}

	[Fact]
	public async Task UpsertAsync_ExistingScope_ReplacesTheValuesRatherThanDuplicating()
	{
		Guid first = await _repository.UpsertAsync("subscription-xyz", 10, 0, ManualDownloadDialOptions.AutoPrune, CancellationToken.None);
		Guid second = await _repository.UpsertAsync("subscription-xyz", 60, 3, ManualDownloadDialOptions.Review, CancellationToken.None);

		Assert.Equal(first, second);

		RetentionPolicy? policy = await _repository.GetByScopeKeyAsync("subscription-xyz", CancellationToken.None);
		Assert.NotNull(policy);
		Assert.Equal(60, policy!.GracePeriodDays);
		Assert.Equal(3, policy.GraceMaxRefreshes);
		Assert.Equal(ManualDownloadDialOptions.Review, policy.ManualDownloadDialDefault);

		IReadOnlyList<RetentionPolicy> all = await _repository.ListAsync(CancellationToken.None);
		Assert.Single(all, p => p.ScopeKey == "subscription-xyz");
	}
}
