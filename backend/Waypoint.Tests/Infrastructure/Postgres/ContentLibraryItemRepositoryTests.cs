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
using Waypoint.Core.ContentLibraries;
using Waypoint.Infrastructure.ContentLibraries;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1396 (migration 0133, epic #1185): the item identity table's round-trip,
/// UUID-reuse-on-update, and version-increment behavior against real Postgres, plus
/// the new FK migration 0133 adds onto <c>content_library_item_folders.item_id</c>
/// (0113 shipped that column with no FK -- see 0133's header for why CASCADE).
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryItemRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ContentLibraryItemRepository _items = null!;
	private ContentLibraryFolderRepository _folders = null!;
	private ContentLibraryRepository _libraries = null!;
	private string _rootPath = null!;

	public ContentLibraryItemRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_rootPath = Directory.CreateTempSubdirectory("wp-content-library-item-test").FullName;
		_items = new ContentLibraryItemRepository(_fixture.ConnectionString);
		_folders = new ContentLibraryFolderRepository(_fixture.ConnectionString);
		_libraries = new ContentLibraryRepository(_fixture.ConnectionString, _rootPath);
	}

	public Task DisposeAsync()
	{
		try
		{
			Directory.Delete(_rootPath, recursive: true);
		}
		catch (IOException)
		{
		}

		return Task.CompletedTask;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE content_library_item_folders, content_library_items, content_library_folders, content_libraries RESTART IDENTITY CASCADE",
			connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedLibraryAsync(string name)
	{
		(_, ContentLibrary? library) = await _libraries.CreateAsync(name, CancellationToken.None);
		return library!.Id;
	}

	private static IReadOnlyList<ContentLibraryItemFileWrite> OneFile(string name = "disk.iso", long size = 42, string hash = "deadbeef") =>
		[new ContentLibraryItemFileWrite(name, size, hash)];

	[Fact]
	public async Task AddAsync_ThenGetAsync_RoundTripsANewItemAtVersionOne()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-items");
		Guid itemId = Guid.NewGuid();

		(ContentLibraryItemAddOutcome outcome, ContentLibraryItem? added) = await _items.AddAsync(
			itemId, libraryId, itemId.ToString("N"), "disk.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);

		Assert.Equal(ContentLibraryItemAddOutcome.Added, outcome);
		Assert.Equal(1, added!.Version);
		Assert.Equal(itemId, added.Id);

		ContentLibraryItem? fetched = await _items.GetAsync(libraryId, itemId, CancellationToken.None);
		Assert.NotNull(fetched);
		Assert.Equal("disk.iso", fetched!.Name);
		Assert.Single(fetched.Files);
		Assert.Equal("disk.iso", fetched.Files[0].Name);
	}

	[Fact]
	public async Task AddAsync_ReturnsLibraryNotFound_ForAnUnknownLibrary()
	{
		(ContentLibraryItemAddOutcome outcome, ContentLibraryItem? item) = await _items.AddAsync(
			Guid.NewGuid(), Guid.NewGuid(), "x", "disk.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);

		Assert.Equal(ContentLibraryItemAddOutcome.LibraryNotFound, outcome);
		Assert.Null(item);
	}

	[Fact]
	public async Task UpdateAsync_KeepsIdAndDirectoryName_AndIncrementsVersion()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-update");
		Guid itemId = Guid.NewGuid();
		string directoryName = itemId.ToString("N");
		await _items.AddAsync(itemId, libraryId, directoryName, "disk.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);

		ContentLibraryItemUpdateOutcome outcome = await _items.UpdateAsync(
			libraryId, itemId, "disk.iso", ContentLibraryItemTypes.Iso, "updated", OneFile(hash: "newhash"), CancellationToken.None);
		Assert.Equal(ContentLibraryItemUpdateOutcome.Updated, outcome);

		ContentLibraryItem updated = (await _items.GetAsync(libraryId, itemId, CancellationToken.None))!;
		Assert.Equal(itemId, updated.Id);
		Assert.Equal(directoryName, updated.DirectoryName);
		Assert.Equal(2, updated.Version);
		Assert.Equal("updated", updated.Description);
		Assert.Equal("newhash", updated.Files[0].ContentHash);
	}

	[Fact]
	public async Task UpdateAsync_ReturnsNotFound_ForAnUnknownItem() =>
		Assert.Equal(
			ContentLibraryItemUpdateOutcome.NotFound,
			await _items.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), "x", ContentLibraryItemTypes.Other, "", OneFile(), CancellationToken.None));

	[Fact]
	public async Task ListAsync_ReturnsEveryItemInTheLibrary_ScopedToThatLibrary()
	{
		Guid libraryOne = await SeedLibraryAsync("vcsp-list-one");
		Guid libraryTwo = await SeedLibraryAsync("vcsp-list-two");
		Guid itemA = Guid.NewGuid();
		Guid itemB = Guid.NewGuid();
		Guid itemC = Guid.NewGuid();
		await _items.AddAsync(itemA, libraryOne, itemA.ToString("N"), "a.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);
		await _items.AddAsync(itemB, libraryOne, itemB.ToString("N"), "b.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);
		await _items.AddAsync(itemC, libraryTwo, itemC.ToString("N"), "c.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);

		IReadOnlyList<ContentLibraryItem> items = await _items.ListAsync(libraryOne, CancellationToken.None);

		Assert.Equal(2, items.Count);
		Assert.Equal(new[] { itemA, itemB }.OrderBy(id => id), items.Select(i => i.Id).OrderBy(id => id));
	}

	[Fact]
	public async Task RemoveAsync_DeletesTheRow()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-remove");
		Guid itemId = Guid.NewGuid();
		await _items.AddAsync(itemId, libraryId, itemId.ToString("N"), "disk.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);

		Assert.Equal(ContentLibraryItemRemoveOutcome.Removed, await _items.RemoveAsync(libraryId, itemId, CancellationToken.None));
		Assert.Null(await _items.GetAsync(libraryId, itemId, CancellationToken.None));
	}

	[Fact]
	public async Task RemoveAsync_ReturnsNotFound_WhenAlreadyGone() =>
		Assert.Equal(ContentLibraryItemRemoveOutcome.NotFound, await _items.RemoveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

	/// <summary>
	/// Migration 0133's FK closes the gap 0113 left open: a
	/// <c>content_library_item_folders</c> row assigned to a real item is dropped
	/// automatically (ON DELETE CASCADE) when that item is removed, rather than either
	/// blocking the item's removal or surviving as a dangling reference.
	/// </summary>
	[Fact]
	public async Task RemoveAsync_CascadesTheItemsFolderAssignment()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-cascade");
		Guid itemId = Guid.NewGuid();
		await _items.AddAsync(itemId, libraryId, itemId.ToString("N"), "disk.iso", ContentLibraryItemTypes.Iso, "", OneFile(), CancellationToken.None);
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Isos", CancellationToken.None);
		Assert.Equal(ContentLibraryItemAssignmentOutcome.Assigned, await _folders.AssignItemAsync(libraryId, itemId, folder!.Id, CancellationToken.None));

		await _items.RemoveAsync(libraryId, itemId, CancellationToken.None);

		IReadOnlyList<ContentLibraryFolderWithItems> tree = await _folders.ListWithItemsAsync(libraryId, CancellationToken.None);
		Assert.DoesNotContain(itemId, tree.Single(f => f.Folder.Id == folder.Id).ItemIds);
	}
}
