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
/// alone, the name-unique index translates to the typed conflict exception the
/// controller maps to 409, and the exactly-one-default invariant holds in both
/// directions -- the default MOVES atomically when another row is marked default, and
/// refuses to be cleared or deleted off the row that holds it. Every test starts from
/// migration 0131's seeded default row, the state a real deployment is in (PR #1816
/// round 2 Spec finding 1).
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
		await ResetToSeededStateAsync();
		_repository = new ConsumerViewRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// Resets to the state a real deployment is actually in: migration 0131's seeded
	/// default row present and holding the default, and nothing else (PR #1816 round 2
	/// Spec finding 1 -- both suites previously did a bare `DELETE FROM consumer_views`,
	/// so every CRUD test ran from a zero-default state production can never reach,
	/// which is exactly why a total default-transfer deadlock survived two review
	/// rounds). The seeded row is re-inserted and re-promoted rather than assumed
	/// intact, because tests legitimately move the default off it and then delete it.
	/// </summary>
	private async Task ResetToSeededStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			DELETE FROM consumer_views WHERE id <> '00000000-0000-0000-0000-000000000001';
			INSERT INTO consumer_views (id, name, platforms, is_default)
			VALUES ('00000000-0000-0000-0000-000000000001', 'Default (unfiltered)', '{}', true)
			ON CONFLICT (id) DO NOTHING;
			UPDATE consumer_views SET name = 'Default (unfiltered)', platforms = '{}', is_default = true
			WHERE id = '00000000-0000-0000-0000-000000000001';
			""", connection);
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

		// Three rows, not two: the fixture starts from migration 0131's seeded default
		// row (PR #1816 round 2 Spec finding 1), which is the oldest and so sorts last.
		Assert.Equal(3, items.Count);
		Assert.Equal(second.Id, items[0].Id);
		Assert.Equal(first.Id, items[1].Id);
		Assert.Equal(ConsumerView.DefaultViewId, items[2].Id);
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
	/// PR #1816 round 2 Spec finding 1: the fixture itself now starts from the
	/// production-reachable state -- migration 0131's seeded row present and holding
	/// the default. Pinned as its own test so a future fixture regression back to
	/// `DELETE FROM consumer_views` fails loudly instead of silently re-hiding the
	/// deadlock class of bug this round fixed.
	/// </summary>
	[Fact]
	public async Task Fixture_StartsFromTheMigrationSeededDefaultRow()
	{
		IReadOnlyList<ConsumerView> items = await _repository.ListAsync(CancellationToken.None);

		ConsumerView only = Assert.Single(items);
		Assert.Equal(ConsumerView.DefaultViewId, only.Id);
		Assert.True(only.IsDefault);
	}

	/// <summary>
	/// Issue #1464 AC 2 ("exactly one view CAN BE MARKED as the default at any time"),
	/// PR #1816 round 2 Spec finding 1: creating a view with <c>is_default: true</c>
	/// while the seeded row holds the default MOVES the default -- it is not a 409.
	/// Exactly one default before, exactly one after, and it is the new row.
	/// </summary>
	[Fact]
	public async Task CreateAsync_WithIsDefaultTrue_TransfersTheDefaultFromTheSeededRow()
	{
		ConsumerView created = await _repository.CreateAsync("Operator view", [], true, CancellationToken.None);

		Assert.True(created.IsDefault);
		IReadOnlyList<ConsumerView> items = await _repository.ListAsync(CancellationToken.None);
		ConsumerView defaultView = Assert.Single(items.Where(view => view.IsDefault));
		Assert.Equal(created.Id, defaultView.Id);
		Assert.False(items.Single(view => view.Id == ConsumerView.DefaultViewId).IsDefault);
	}

	/// <summary>
	/// Issue #1464 AC 2, PR #1816 round 2 Spec finding 1 -- the PUT half of the same
	/// move, which round 1 refused with 409 <c>default_already_set</c> and thereby made
	/// the seeded default immovable. Promoting an existing non-default row must succeed,
	/// demote the incumbent in the same transaction, and leave exactly one default.
	/// </summary>
	[Fact]
	public async Task UpdateAsync_PromotingAnotherView_TransfersTheDefault()
	{
		ConsumerView other = await _repository.CreateAsync("Operator view", [], false, CancellationToken.None);

		ConsumerView? promoted = await _repository.UpdateAsync(
			other.Id, name: null, platforms: null, isDefault: true, CancellationToken.None);

		Assert.NotNull(promoted);
		Assert.True(promoted!.IsDefault);
		IReadOnlyList<ConsumerView> items = await _repository.ListAsync(CancellationToken.None);
		ConsumerView defaultView = Assert.Single(items.Where(view => view.IsDefault));
		Assert.Equal(other.Id, defaultView.Id);
		Assert.False(items.Single(view => view.Id == ConsumerView.DefaultViewId).IsDefault);
	}

	/// <summary>Re-marking the row that ALREADY holds the default is an ordinary no-op success, not a self-conflict -- the demote step skips the promotion target by id.</summary>
	[Fact]
	public async Task UpdateAsync_PromotingTheRowThatIsAlreadyDefault_KeepsItDefault()
	{
		ConsumerView? promoted = await _repository.UpdateAsync(
			ConsumerView.DefaultViewId, name: null, platforms: null, isDefault: true, CancellationToken.None);

		Assert.NotNull(promoted);
		Assert.True(promoted!.IsDefault);
		Assert.Single((await _repository.ListAsync(CancellationToken.None)).Where(view => view.IsDefault));
	}

	/// <summary>
	/// The transfer's rollback half: promoting an id that does not exist must not
	/// demote the incumbent on its way to the 404 -- otherwise a typo'd PUT would leave
	/// the table with zero defaults, the very state the invariant forbids.
	/// </summary>
	[Fact]
	public async Task UpdateAsync_PromotingAnUnknownId_ReturnsNullAndLeavesTheIncumbentDefault()
	{
		ConsumerView? promoted = await _repository.UpdateAsync(
			Guid.NewGuid(), name: null, platforms: null, isDefault: true, CancellationToken.None);

		Assert.Null(promoted);
		ConsumerView defaultView = Assert.Single(
			(await _repository.ListAsync(CancellationToken.None)).Where(view => view.IsDefault));
		Assert.Equal(ConsumerView.DefaultViewId, defaultView.Id);
	}

	/// <summary>
	/// The "at most one" database guard is unchanged by the transfer work: a raw INSERT
	/// that bypasses the repository's transaction entirely still hits migration 0131's
	/// partial unique index. This is what <c>ConsumerViewDefaultConflictException</c>
	/// (409 <c>default_already_set</c>) now exists for -- a writer that never took the
	/// transfer lock -- rather than the ordinary promotion path.
	/// </summary>
	[Fact]
	public async Task RawSecondDefaultInsert_StillViolatesThePartialUniqueIndex()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO consumer_views (name, platforms, is_default) VALUES ('Raw second default', '{}', true)",
			connection);

		PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

		Assert.Equal("23505", error.SqlState);
		Assert.Equal("idx_consumer_views_default_unique", error.ConstraintName);
	}

	/// <summary>The seeded row is the STARTING default, not a pinned one: once the default has been moved off it, it is an ordinary row and deletes normally.</summary>
	[Fact]
	public async Task DeleteAsync_SeededRowAfterTheDefaultMovedAway_Succeeds()
	{
		ConsumerView other = await _repository.CreateAsync("Operator view", [], true, CancellationToken.None);

		bool deleted = await _repository.DeleteAsync(ConsumerView.DefaultViewId, CancellationToken.None);

		Assert.True(deleted);
		ConsumerView defaultView = Assert.Single(
			(await _repository.ListAsync(CancellationToken.None)).Where(view => view.IsDefault));
		Assert.Equal(other.Id, defaultView.Id);
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
	/// convention as <see cref="RawSecondDefaultInsert_StillViolatesThePartialUniqueIndex"/>.
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
