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
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1470, migration 0117: <c>esx_acquisition_subscriptions</c> CRUD against
/// real Postgres -- the array selection round-trips, a partial PATCH leaves
/// unspecified columns alone, and disabling a subscription is an in-place UPDATE
/// that never removes the row (the AC: "disabling a subscription doesn't delete its
/// history").
/// </summary>
[Collection("Postgres")]
public sealed class EsxAcquisitionSubscriptionRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private EsxAcquisitionSubscriptionRepository _repository = null!;

	public EsxAcquisitionSubscriptionRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new EsxAcquisitionSubscriptionRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM esx_acquisition_subscriptions", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task CreateAsync_ThenGetAsync_RoundTripsSelectedPlatforms()
	{
		EsxAcquisitionSubscription created = await _repository.CreateAsync(
			"Baseline ESX 8.0", ["esx-8.0-standard", "esx-8.0-hpe"], true, CancellationToken.None);

		EsxAcquisitionSubscription? fetched = await _repository.GetAsync(created.Id, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal("Baseline ESX 8.0", fetched!.Name);
		Assert.Equal(["esx-8.0-standard", "esx-8.0-hpe"], fetched.SelectedPlatforms);
		Assert.True(fetched.Enabled);
	}

	[Fact]
	public async Task ListAsync_ReturnsNewestFirst()
	{
		EsxAcquisitionSubscription first = await _repository.CreateAsync("First", ["esx-8.0-standard"], true, CancellationToken.None);
		await Task.Delay(10);
		EsxAcquisitionSubscription second = await _repository.CreateAsync("Second", ["esx-8.0-hpe"], true, CancellationToken.None);

		IReadOnlyList<EsxAcquisitionSubscription> items = await _repository.ListAsync(CancellationToken.None);

		Assert.Equal(2, items.Count);
		Assert.Equal(second.Id, items[0].Id);
		Assert.Equal(first.Id, items[1].Id);
	}

	[Fact]
	public async Task UpdateAsync_DisablingOnly_LeavesNameAndSelectionUntouched()
	{
		EsxAcquisitionSubscription created = await _repository.CreateAsync(
			"Baseline ESX 8.0", ["esx-8.0-standard"], true, CancellationToken.None);

		EsxAcquisitionSubscription? updated = await _repository.UpdateAsync(
			created.Id, name: null, selectedPlatforms: null, enabled: false, CancellationToken.None);

		Assert.NotNull(updated);
		Assert.False(updated!.Enabled);
		Assert.Equal("Baseline ESX 8.0", updated.Name);
		Assert.Equal(["esx-8.0-standard"], updated.SelectedPlatforms);

		// The row still exists -- disabling never deletes history (issue #1470 AC).
		EsxAcquisitionSubscription? stillThere = await _repository.GetAsync(created.Id, CancellationToken.None);
		Assert.NotNull(stillThere);
		Assert.False(stillThere!.Enabled);
	}

	[Fact]
	public async Task UpdateAsync_ChangingSelection_LeavesEnabledAndNameUntouched()
	{
		EsxAcquisitionSubscription created = await _repository.CreateAsync(
			"Baseline ESX 8.0", ["esx-8.0-standard"], true, CancellationToken.None);

		EsxAcquisitionSubscription? updated = await _repository.UpdateAsync(
			created.Id, name: null, selectedPlatforms: ["esx-8.0-hpe", "esx-8.0-dell"], enabled: null, CancellationToken.None);

		Assert.NotNull(updated);
		Assert.Equal(["esx-8.0-hpe", "esx-8.0-dell"], updated!.SelectedPlatforms);
		Assert.True(updated.Enabled);
		Assert.Equal("Baseline ESX 8.0", updated.Name);
	}

	[Fact]
	public async Task UpdateAsync_UnknownId_ReturnsNull()
	{
		EsxAcquisitionSubscription? updated = await _repository.UpdateAsync(
			Guid.NewGuid(), name: "New name", selectedPlatforms: null, enabled: null, CancellationToken.None);

		Assert.Null(updated);
	}
}
