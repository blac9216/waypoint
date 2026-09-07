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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1480, migration 0111, against real Postgres: re-indexing an unchanged
/// listing never duplicates a <c>vks_library_items</c> row (upsert keyed on
/// <c>(source, name)</c>), and a genuine dimension/etag/sha256 change on a
/// previously-seen name updates the same row rather than inserting a second one.
/// Fixtures below are entirely invented (CLAUDE.md sanitization rules) -- no real
/// item name or build id from the public VKS library or a real depot catalog.
/// </summary>
[Collection("Postgres")]
public sealed class VksLibraryIndexRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private VksLibraryIndexRepository _repository = null!;

	public VksLibraryIndexRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_repository = new VksLibraryIndexRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task UpsertItemsAsync_ReIndexOfUnchangedListing_YieldsNoDuplicateRows()
	{
		string name = $"ob-99990001-photon-5-amd64-v1.30.2---vmware.1-vkr.1-{Guid.NewGuid():N}";
		VksItemDimensions dimensions = new("photon", "5", "amd64", "1.30.2", "1", false, VksReleaseLines.Vkr, "1", "99990001");
		DateTimeOffset firstIndex = DateTimeOffset.UtcNow.AddMinutes(-10);
		VksLibraryItem firstObservation = VksLibraryItem.FromIndexObservation(
			name, VksItemSources.Public, itemUuid: Guid.NewGuid().ToString(), dimensions, VksNamingEras.Current,
			VksParseStatuses.Parsed, new VksChangeToken("etag-fixture-abc123"), sha256: null, sizeBytes: 3_700_000_000,
			createdUpstream: DateTimeOffset.UtcNow.AddDays(-30), observedAt: firstIndex);

		await _repository.UpsertItemsAsync([firstObservation], CancellationToken.None);

		DateTimeOffset secondIndex = DateTimeOffset.UtcNow;
		VksLibraryItem secondObservation = firstObservation with { Id = Guid.NewGuid(), DiscoveredAt = secondIndex, LastSeenAt = secondIndex };

		await _repository.UpsertItemsAsync([secondObservation], CancellationToken.None);

		IReadOnlyList<VksLibraryItem> items = await _repository.GetItemsAsync(CancellationToken.None);
		VksLibraryItem[] matching = [.. items.Where(i => i.Name == name)];
		Assert.Single(matching);
		Assert.Equal(firstObservation.Id, matching[0].Id);
		Assert.True(matching[0].LastSeenAt >= secondIndex.AddSeconds(-1));
		// Postgres timestamptz has microsecond precision; .NET DateTimeOffset has
		// tick (100ns) precision, so compare with a tolerance rather than exact equality.
		Assert.True((matching[0].DiscoveredAt - firstIndex).Duration() < TimeSpan.FromMilliseconds(1));
	}

	/// <summary>
	/// #1796: the 17-assignment <c>ON CONFLICT (source, name) DO UPDATE SET</c> clause
	/// in <see cref="VksLibraryIndexRepository"/> is exercised with values that
	/// genuinely change on every updatable column -- <c>item_uuid</c>, every
	/// <see cref="VksItemDimensions"/> member, <c>naming_era</c>, <c>parse_status</c>,
	/// <c>etag</c>, <c>sha256</c>, and <c>size_bytes</c> -- proving a single row
	/// survives with the new values, the original <c>id</c>, an advanced
	/// <c>last_seen_at</c>, and an <c>discovered_at</c> that the update clause never
	/// touches.
	/// <para>
	/// Both observations are coherent states of the model, in the one direction that
	/// keeps them so: the row is first seen quarantined (<c>parse_status='unparsed'</c>,
	/// <c>naming_era='unparsed'</c>, every dimension NULL -- 0111's and
	/// <see cref="VksItemDimensions"/>' documented unparsed shape), then re-seen parsed
	/// once the grammar covers its era, which is exactly #1031's quarantine-then-parse
	/// story. The reverse (populated dimensions carried alongside
	/// <c>parse_status='unparsed'</c>) is a state both docs call impossible, so it is
	/// never pinned here (round-2 review note N3).
	/// </para>
	/// </summary>
	[Fact]
	public async Task UpsertItemsAsync_ChangedDimensionsOnSeenName_UpdatesTheSameRow()
	{
		string name = $"ob-99990009-photon-5-amd64-v1.30.2---vmware.1-vkr.1-{Guid.NewGuid():N}";
		DateTimeOffset firstIndex = DateTimeOffset.UtcNow.AddMinutes(-10);
		VksLibraryItem firstObservation = VksLibraryItem.FromIndexObservation(
			name, VksItemSources.Public, itemUuid: Guid.NewGuid().ToString(), VksItemDimensions.Empty, VksNamingEras.Unparsed,
			VksParseStatuses.Unparsed, new VksChangeToken("etag-fixture-original"), sha256: null, sizeBytes: 1000,
			createdUpstream: DateTimeOffset.UtcNow.AddDays(-30), observedAt: firstIndex);

		await _repository.UpsertItemsAsync([firstObservation], CancellationToken.None);

		VksItemDimensions changedDimensions = new("ubuntu", "24.04", "arm64", "1.31.1", "2", true, VksReleaseLines.Tkg, "9", "99990010");
		DateTimeOffset secondIndex = DateTimeOffset.UtcNow;
		DateTimeOffset changedCreatedUpstream = DateTimeOffset.UtcNow.AddDays(-1);
		VksLibraryItem changedObservation = firstObservation with
		{
			Id = Guid.NewGuid(),
			ItemUuid = Guid.NewGuid().ToString(),
			Dimensions = changedDimensions,
			NamingEra = VksNamingEras.Current,
			ParseStatus = VksParseStatuses.Parsed,
			Etag = new VksChangeToken("etag-fixture-changed"),
			Sha256 = "deadbeefcafe",
			SizeBytes = 2_000_000,
			CreatedUpstream = changedCreatedUpstream,
			DiscoveredAt = secondIndex,
			LastSeenAt = secondIndex,
		};

		await _repository.UpsertItemsAsync([changedObservation], CancellationToken.None);

		IReadOnlyList<VksLibraryItem> items = await _repository.GetItemsAsync(CancellationToken.None);
		VksLibraryItem[] matching = [.. items.Where(i => i.Name == name)];
		Assert.Single(matching);
		VksLibraryItem stored = matching[0];

		// id is a stable database identity that a conflicting upsert must never
		// overwrite -- Postgres keeps the original row's id even though this test's
		// own new observation carries a fresh one.
		Assert.Equal(firstObservation.Id, stored.Id);
		Assert.Equal(changedObservation.ItemUuid, stored.ItemUuid);
		Assert.Equal(changedDimensions, stored.Dimensions);
		Assert.Equal(VksNamingEras.Current, stored.NamingEra);
		Assert.Equal(VksParseStatuses.Parsed, stored.ParseStatus);
		Assert.Equal("etag-fixture-changed", stored.Etag?.Value);
		Assert.Equal("deadbeefcafe", stored.Sha256);
		Assert.Equal(2_000_000, stored.SizeBytes);
		Assert.True((stored.CreatedUpstream!.Value - changedCreatedUpstream).Duration() < TimeSpan.FromMilliseconds(1));
		Assert.True(stored.LastSeenAt >= secondIndex.AddSeconds(-1));
		// discovered_at is excluded from DO UPDATE SET on purpose (see the type's own
		// doc comment) -- it must stay pinned to the first observation even though
		// this test's changed observation carries a different DiscoveredAt.
		Assert.True((stored.DiscoveredAt - firstIndex).Duration() < TimeSpan.FromMilliseconds(1));
	}

	[Fact]
	public async Task UpsertItemsAsync_SameNameDifferentSource_IsTwoIndependentRows()
	{
		string name = $"ob-99990002-ubuntu-24.04-amd64-v1.31.1---vmware.1-vkr.2-{Guid.NewGuid():N}";
		VksItemDimensions dimensions = new("ubuntu", "24.04", "amd64", "1.31.1", "1", false, VksReleaseLines.Vkr, "2", "99990002");
		VksLibraryItem publicItem = VksLibraryItem.FromIndexObservation(
			name, VksItemSources.Public, itemUuid: Guid.NewGuid().ToString(), dimensions, VksNamingEras.Current,
			VksParseStatuses.Parsed, new VksChangeToken("etag-fixture-v1"), sha256: null, sizeBytes: 1000,
			createdUpstream: null, observedAt: DateTimeOffset.UtcNow);
		VksLibraryItem depotItem = publicItem with { Id = Guid.NewGuid(), Source = VksItemSources.Depot, ItemUuid = null, Etag = null, Sha256 = "deadbeef" };

		await _repository.UpsertItemsAsync([publicItem, depotItem], CancellationToken.None);

		IReadOnlyList<VksLibraryItem> items = await _repository.GetItemsAsync(CancellationToken.None);
		Assert.Equal(2, items.Count(i => i.Name == name));
		Assert.Contains(items, i => i.Name == name && i.Source == VksItemSources.Public);
		Assert.Contains(items, i => i.Name == name && i.Source == VksItemSources.Depot && i.Sha256 == "deadbeef");
	}

	[Fact]
	public async Task UpsertItemsAsync_UnparseableName_IsStoredWithNullDimensions_NeverDropped()
	{
		string name = $"completely-bogus-name-{Guid.NewGuid():N}";
		VksLibraryItem item = VksLibraryItem.FromIndexObservation(
			name, VksItemSources.Public, itemUuid: Guid.NewGuid().ToString(), VksItemDimensions.Empty,
			VksNamingEras.Unparsed, VksParseStatuses.Unparsed, etag: new VksChangeToken("etag-fixture-unparsed"),
			sha256: null, sizeBytes: null, createdUpstream: null, observedAt: DateTimeOffset.UtcNow);

		await _repository.UpsertItemsAsync([item], CancellationToken.None);

		IReadOnlyList<VksLibraryItem> items = await _repository.GetItemsAsync(CancellationToken.None);
		VksLibraryItem stored = items.Single(i => i.Name == name);
		Assert.Equal(VksParseStatuses.Unparsed, stored.ParseStatus);
		Assert.Equal(VksNamingEras.Unparsed, stored.NamingEra);
		Assert.Null(stored.Dimensions.Distro);
		Assert.Null(stored.Dimensions.K8sVersion);
	}
}
