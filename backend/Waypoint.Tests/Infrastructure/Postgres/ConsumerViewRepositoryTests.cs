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
/// Issue #1464, migration 0131: <c>consumer_views</c> CRUD against real Postgres --
/// the ordered platform array round-trips, a partial PUT leaves unspecified columns
/// alone, the name-unique index and the exactly-one-default partial unique index both
/// translate to the typed conflict exceptions the controller maps to 409.
/// </summary>
[Collection("Postgres")]
public sealed class ConsumerViewRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ConsumerViewRepository _repository = null!;

	public ConsumerViewRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new ConsumerViewRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM consumer_views", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task CreateAsync_ThenGetAsync_RoundTripsOrderedPlatforms()
	{
		ConsumerView created = await _repository.CreateAsync(
			"7.0 only", ["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], false, CancellationToken.None);

		ConsumerView? fetched = await _repository.GetAsync(created.Id, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal("7.0 only", fetched!.Name);
		Assert.Equal(["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], fetched.Platforms);
		Assert.False(fetched.IsDefault);
	}

	[Fact]
	public async Task ListAsync_ReturnsNewestFirst()
	{
		ConsumerView first = await _repository.CreateAsync("First", ["embeddedEsx-7.0-INTL"], false, CancellationToken.None);
		await Task.Delay(10);
		ConsumerView second = await _repository.CreateAsync("Second", ["embeddedEsx-8.0-INTL"], false, CancellationToken.None);

		IReadOnlyList<ConsumerView> items = await _repository.ListAsync(CancellationToken.None);

		Assert.Equal(2, items.Count);
		Assert.Equal(second.Id, items[0].Id);
		Assert.Equal(first.Id, items[1].Id);
	}

	[Fact]
	public async Task UpdateAsync_ChangingNameOnly_LeavesPlatformsAndDefaultUntouched()
	{
		ConsumerView created = await _repository.CreateAsync(
			"Original", ["embeddedEsx-7.0-INTL"], true, CancellationToken.None);

		ConsumerView? updated = await _repository.UpdateAsync(
			created.Id, name: "Renamed", platforms: null, isDefault: null, CancellationToken.None);

		Assert.NotNull(updated);
		Assert.Equal("Renamed", updated!.Name);
		Assert.Equal(["embeddedEsx-7.0-INTL"], updated.Platforms);
		Assert.True(updated.IsDefault);
	}

	[Fact]
	public async Task UpdateAsync_UnknownId_ReturnsNull()
	{
		ConsumerView? updated = await _repository.UpdateAsync(
			Guid.NewGuid(), name: "New name", platforms: null, isDefault: null, CancellationToken.None);

		Assert.Null(updated);
	}

	[Fact]
	public async Task DeleteAsync_ExistingRow_RemovesItAndReturnsTrue()
	{
		ConsumerView created = await _repository.CreateAsync("To delete", [], false, CancellationToken.None);

		bool deleted = await _repository.DeleteAsync(created.Id, CancellationToken.None);

		Assert.True(deleted);
		Assert.Null(await _repository.GetAsync(created.Id, CancellationToken.None));
	}

	[Fact]
	public async Task DeleteAsync_UnknownId_ReturnsFalse()
	{
		bool deleted = await _repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
		Assert.False(deleted);
	}

	[Fact]
	public async Task CreateAsync_DuplicateName_ThrowsNameConflict()
	{
		await _repository.CreateAsync("Duplicate", [], false, CancellationToken.None);

		await Assert.ThrowsAsync<ConsumerViewNameConflictException>(
			() => _repository.CreateAsync("Duplicate", [], false, CancellationToken.None));
	}

	/// <summary>
	/// Issue #1464 AC "exactly one default view": the database-layer half. A second
	/// <c>is_default = true</c> insert hits migration 0131's partial unique index and
	/// must translate to the typed conflict exception, not an unmapped Postgres error.
	/// </summary>
	[Fact]
	public async Task CreateAsync_SecondDefault_ThrowsDefaultConflict()
	{
		await _repository.CreateAsync("First default", [], true, CancellationToken.None);

		await Assert.ThrowsAsync<ConsumerViewDefaultConflictException>(
			() => _repository.CreateAsync("Second default", [], true, CancellationToken.None));
	}

	[Fact]
	public async Task UpdateAsync_SettingDefaultWhenAnotherAlreadyIsDefault_ThrowsDefaultConflict()
	{
		await _repository.CreateAsync("First default", [], true, CancellationToken.None);
		ConsumerView second = await _repository.CreateAsync("Not default", [], false, CancellationToken.None);

		await Assert.ThrowsAsync<ConsumerViewDefaultConflictException>(
			() => _repository.UpdateAsync(second.Id, name: null, platforms: null, isDefault: true, CancellationToken.None));
	}

	[Fact]
	public async Task CreateAsync_MultipleNonDefaultViews_Succeeds()
	{
		await _repository.CreateAsync("A", [], false, CancellationToken.None);
		ConsumerView second = await _repository.CreateAsync("B", [], false, CancellationToken.None);

		Assert.False(second.IsDefault);
	}

	/// <summary>Issue #1464 AC, PR #1816 round 1 Spec finding 1 (repository, bypassing the API): a view with an empty platform set is explicitly legal and round-trips as such -- it means "all platforms, no filtering," the same meaning the seeded default row's empty set carries.</summary>
	[Fact]
	public async Task CreateAsync_EmptyPlatforms_RoundTripsAsAllPlatforms()
	{
		ConsumerView created = await _repository.CreateAsync("Unfiltered", [], false, CancellationToken.None);

		ConsumerView? fetched = await _repository.GetAsync(created.Id, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Empty(fetched!.Platforms);
	}

	/// <summary>
	/// Issue #1464 AC "exactly one default view at any time" (PR #1816 round 1 Spec
	/// finding 1): the DELETE half, proven by calling the repository directly --
	/// bypassing the controller's own check entirely, same "database enforces it too"
	/// convention as <see cref="CreateAsync_SecondDefault_ThrowsDefaultConflict"/>.
	/// Deleting the sole default row must never succeed; it must throw so the caller
	/// can map it to 409, never silently leave zero defaults.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_SoleDefault_ThrowsSoleDefaultException()
	{
		ConsumerView created = await _repository.CreateAsync("Only default", [], true, CancellationToken.None);

		await Assert.ThrowsAsync<ConsumerViewSoleDefaultException>(
			() => _repository.DeleteAsync(created.Id, CancellationToken.None));

		Assert.NotNull(await _repository.GetAsync(created.Id, CancellationToken.None));
	}

	/// <summary>A non-default row deletes normally even while a different row holds the default -- only the sole default row itself is refused.</summary>
	[Fact]
	public async Task DeleteAsync_NonDefaultRowWhileAnotherIsDefault_Succeeds()
	{
		await _repository.CreateAsync("The default", [], true, CancellationToken.None);
		ConsumerView other = await _repository.CreateAsync("Not default", [], false, CancellationToken.None);

		bool deleted = await _repository.DeleteAsync(other.Id, CancellationToken.None);

		Assert.True(deleted);
	}

	/// <summary>
	/// Issue #1464 AC "exactly one default view at any time" (PR #1816 round 1 Spec
	/// finding 1): the UPDATE/clear half, proven by calling the repository directly --
	/// bypassing the controller's own check entirely. A single
	/// <c>UpdateAsync(isDefault: false)</c> on the sole default row must never succeed;
	/// it must throw so the caller can map it to 409, never silently leave zero
	/// defaults.
	/// </summary>
	[Fact]
	public async Task UpdateAsync_ClearingIsDefaultOnSoleDefault_ThrowsSoleDefaultException()
	{
		ConsumerView created = await _repository.CreateAsync("Only default", [], true, CancellationToken.None);

		await Assert.ThrowsAsync<ConsumerViewSoleDefaultException>(
			() => _repository.UpdateAsync(created.Id, name: null, platforms: null, isDefault: false, CancellationToken.None));

		ConsumerView? unchanged = await _repository.GetAsync(created.Id, CancellationToken.None);
		Assert.NotNull(unchanged);
		Assert.True(unchanged!.IsDefault);
	}

	/// <summary>An update that leaves <c>is_default</c> unspecified (null) on the sole default row is NOT a refusal -- only an explicit <c>false</c> is.</summary>
	[Fact]
	public async Task UpdateAsync_LeavingIsDefaultUnspecifiedOnSoleDefault_Succeeds()
	{
		ConsumerView created = await _repository.CreateAsync("Only default", [], true, CancellationToken.None);

		ConsumerView? updated = await _repository.UpdateAsync(
			created.Id, name: "Renamed", platforms: null, isDefault: null, CancellationToken.None);

		Assert.NotNull(updated);
		Assert.True(updated!.IsDefault);
		Assert.Equal("Renamed", updated.Name);
	}

	/// <summary>Setting <c>is_default: false</c> on a row that is NOT currently the default is an ordinary no-op update, not a refusal.</summary>
	[Fact]
	public async Task UpdateAsync_ClearingIsDefaultOnANonDefaultRow_Succeeds()
	{
		await _repository.CreateAsync("The default", [], true, CancellationToken.None);
		ConsumerView other = await _repository.CreateAsync("Not default", [], false, CancellationToken.None);

		ConsumerView? updated = await _repository.UpdateAsync(
			other.Id, name: null, platforms: null, isDefault: false, CancellationToken.None);

		Assert.NotNull(updated);
		Assert.False(updated!.IsDefault);
	}
}
