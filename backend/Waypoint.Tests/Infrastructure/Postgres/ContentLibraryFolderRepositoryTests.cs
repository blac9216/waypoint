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
/// Issue #1389 (migration 0113, epic #1185): the folder tree's round-trip, cycle
/// rejection, non-empty delete rejection, and repair-survival property against real
/// Postgres. #1398 (the on-disk repair/rebuild pass) does not exist yet, so
/// <see cref="Folders_AreEntirelyUnaffectedByRecreatingTheLibrarysOnDiskDirectory"/>
/// proves the AC as a schema property instead: folder rows carry no reference to
/// anything on disk, so deleting and recreating a library's directory out-of-band
/// (what any future repair pass would do) cannot touch them.
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryFolderRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ContentLibraryFolderRepository _folders = null!;
	private ContentLibraryRepository _libraries = null!;
	private string _rootPath = null!;

	public ContentLibraryFolderRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_rootPath = Directory.CreateTempSubdirectory("wp-content-library-folder-test").FullName;
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
			"TRUNCATE TABLE content_library_item_folders, content_library_folders, content_libraries RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedLibraryAsync(string name)
	{
		(_, ContentLibrary? library) = await _libraries.CreateAsync(name, CancellationToken.None);
		return library!.Id;
	}

	[Fact]
	public async Task CreateAsync_ThenListWithItems_RoundTripsARootAndAChildFolder()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-tree");

		(ContentLibraryFolderCreateOutcome rootOutcome, ContentLibraryFolder? root) =
			await _folders.CreateAsync(libraryId, null, "Root", CancellationToken.None);
		Assert.Equal(ContentLibraryFolderCreateOutcome.Created, rootOutcome);

		(ContentLibraryFolderCreateOutcome childOutcome, ContentLibraryFolder? child) =
			await _folders.CreateAsync(libraryId, root!.Id, "Child", CancellationToken.None);
		Assert.Equal(ContentLibraryFolderCreateOutcome.Created, childOutcome);

		IReadOnlyList<ContentLibraryFolderWithItems> all = await _folders.ListWithItemsAsync(libraryId, CancellationToken.None);

		Assert.Equal(2, all.Count);
		Assert.Contains(all, f => f.Folder.Id == root.Id && f.Folder.ParentFolderId is null);
		Assert.Contains(all, f => f.Folder.Id == child!.Id && f.Folder.ParentFolderId == root.Id);
	}

	[Fact]
	public async Task CreateAsync_RejectsADuplicateRootName_EvenThoughParentFolderIdIsNullForBoth()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-root-dup");
		(_, ContentLibraryFolder? first) = await _folders.CreateAsync(libraryId, null, "Same", CancellationToken.None);
		Assert.NotNull(first);

		(ContentLibraryFolderCreateOutcome outcome, ContentLibraryFolder? second) =
			await _folders.CreateAsync(libraryId, null, "Same", CancellationToken.None);

		Assert.Equal(ContentLibraryFolderCreateOutcome.NameTaken, outcome);
		Assert.Null(second);
	}

	[Fact]
	public async Task CreateAsync_AllowsTheSameNameUnderDifferentParents()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-parent-scoped");
		(_, ContentLibraryFolder? parentA) = await _folders.CreateAsync(libraryId, null, "A", CancellationToken.None);
		(_, ContentLibraryFolder? parentB) = await _folders.CreateAsync(libraryId, null, "B", CancellationToken.None);

		(ContentLibraryFolderCreateOutcome outcomeA, _) = await _folders.CreateAsync(libraryId, parentA!.Id, "Same", CancellationToken.None);
		(ContentLibraryFolderCreateOutcome outcomeB, _) = await _folders.CreateAsync(libraryId, parentB!.Id, "Same", CancellationToken.None);

		Assert.Equal(ContentLibraryFolderCreateOutcome.Created, outcomeA);
		Assert.Equal(ContentLibraryFolderCreateOutcome.Created, outcomeB);
	}

	[Fact]
	public async Task CreateAsync_UnknownLibrary_ReturnsLibraryNotFound()
	{
		(ContentLibraryFolderCreateOutcome outcome, _) = await _folders.CreateAsync(Guid.NewGuid(), null, "X", CancellationToken.None);
		Assert.Equal(ContentLibraryFolderCreateOutcome.LibraryNotFound, outcome);
	}

	[Fact]
	public async Task UpdateAsync_MovingAFolderUnderItsOwnDescendant_IsRejectedAsACycle()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-cycle");
		(_, ContentLibraryFolder? grandparent) = await _folders.CreateAsync(libraryId, null, "Grandparent", CancellationToken.None);
		(_, ContentLibraryFolder? parent) = await _folders.CreateAsync(libraryId, grandparent!.Id, "Parent", CancellationToken.None);
		(_, ContentLibraryFolder? child) = await _folders.CreateAsync(libraryId, parent!.Id, "Child", CancellationToken.None);

		// Grandparent -> Child would place an ancestor under its own descendant.
		ContentLibraryFolderUpdateOutcome outcome =
			await _folders.UpdateAsync(grandparent.Id, "Grandparent", child!.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderUpdateOutcome.CycleRejected, outcome);
	}

	[Fact]
	public async Task UpdateAsync_MovingAFolderUnderItself_IsRejectedAsACycle()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-self-cycle");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Solo", CancellationToken.None);

		ContentLibraryFolderUpdateOutcome outcome =
			await _folders.UpdateAsync(folder!.Id, "Solo", folder.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderUpdateOutcome.CycleRejected, outcome);
	}

	[Fact]
	public async Task UpdateAsync_RenamesAndMovesToRoot()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-rename-move");
		(_, ContentLibraryFolder? parent) = await _folders.CreateAsync(libraryId, null, "Parent", CancellationToken.None);
		(_, ContentLibraryFolder? child) = await _folders.CreateAsync(libraryId, parent!.Id, "Child", CancellationToken.None);

		ContentLibraryFolderUpdateOutcome outcome = await _folders.UpdateAsync(child!.Id, "Renamed", null, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderUpdateOutcome.Updated, outcome);
		ContentLibraryFolder? updated = await _folders.GetAsync(child.Id, CancellationToken.None);
		Assert.Equal("Renamed", updated!.Name);
		Assert.Null(updated.ParentFolderId);
	}

	/// <summary>
	/// F1 (round 2): the same race class N1 (round 1) fixed on <c>AssignItemAsync</c>,
	/// symmetric here. This test holds the folder row locked in a second
	/// connection/transaction -- exactly the lock <c>DeleteAsync</c> takes -- proves
	/// <c>UpdateAsync</c> blocks behind it, then deletes the folder and commits. Before
	/// the fix, <c>UpdateAsync</c> read the folder's library_id with a plain SELECT
	/// before taking any lock, so a concurrent delete committed in that window left the
	/// folder absent from the locked sibling scope with nothing re-checking that the
	/// UPDATE itself still had a row to touch -- it silently affected zero rows and
	/// still returned <c>Updated</c>. This must resolve to <c>NotFound</c> instead.
	/// </summary>
	[Fact]
	public async Task UpdateAsync_ConcurrentWithDeleteOfTheSameFolder_ResolvesToNotFoundWithoutFalseSuccess()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-update-race");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Race", CancellationToken.None);

		await using NpgsqlConnection lockConnection = new(_fixture.ConnectionString);
		await lockConnection.OpenAsync();
		await using NpgsqlTransaction lockTransaction = await lockConnection.BeginTransactionAsync();
		await using (NpgsqlCommand lockCommand = new(
			"SELECT 1 FROM content_library_folders WHERE id = $1 FOR UPDATE", lockConnection, lockTransaction))
		{
			lockCommand.Parameters.AddWithValue(folder!.Id);
			await lockCommand.ExecuteScalarAsync();
		}

		Task<ContentLibraryFolderUpdateOutcome> updateTask =
			_folders.UpdateAsync(folder.Id, "Renamed", null, CancellationToken.None);

		// UpdateAsync must be blocked behind the held row lock, not racing ahead.
		Task firstToComplete = await Task.WhenAny(updateTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
		Assert.NotSame(updateTask, firstToComplete);

		await using (NpgsqlCommand deleteCommand = new(
			"DELETE FROM content_library_folders WHERE id = $1", lockConnection, lockTransaction))
		{
			deleteCommand.Parameters.AddWithValue(folder.Id);
			await deleteCommand.ExecuteNonQueryAsync();
		}

		await lockTransaction.CommitAsync();

		ContentLibraryFolderUpdateOutcome outcome = await updateTask;
		Assert.Equal(ContentLibraryFolderUpdateOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task DeleteAsync_RejectsAFolderWithAChildFolder()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-nonempty-child");
		(_, ContentLibraryFolder? parent) = await _folders.CreateAsync(libraryId, null, "Parent", CancellationToken.None);
		await _folders.CreateAsync(libraryId, parent!.Id, "Child", CancellationToken.None);

		ContentLibraryFolderDeleteOutcome outcome = await _folders.DeleteAsync(parent.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderDeleteOutcome.NotEmpty, outcome);
	}

	[Fact]
	public async Task DeleteAsync_RejectsAFolderWithAnAssignedItem()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-nonempty-item");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Holds-item", CancellationToken.None);
		await _folders.AssignItemAsync(libraryId, Guid.NewGuid(), folder!.Id, CancellationToken.None);

		ContentLibraryFolderDeleteOutcome outcome = await _folders.DeleteAsync(folder.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderDeleteOutcome.NotEmpty, outcome);
	}

	[Fact]
	public async Task DeleteAsync_DeletesAnEmptyFolder()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-empty-folder");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Empty", CancellationToken.None);

		ContentLibraryFolderDeleteOutcome outcome = await _folders.DeleteAsync(folder!.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryFolderDeleteOutcome.Deleted, outcome);
		Assert.Null(await _folders.GetAsync(folder.Id, CancellationToken.None));
	}

	[Fact]
	public async Task AssignItemAsync_MovesAnItemBetweenFoldersAndUnassignsToRoot()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-item-move");
		(_, ContentLibraryFolder? folderA) = await _folders.CreateAsync(libraryId, null, "A", CancellationToken.None);
		(_, ContentLibraryFolder? folderB) = await _folders.CreateAsync(libraryId, null, "B", CancellationToken.None);
		Guid itemId = Guid.NewGuid();

		Assert.Equal(
			ContentLibraryItemAssignmentOutcome.Assigned,
			await _folders.AssignItemAsync(libraryId, itemId, folderA!.Id, CancellationToken.None));
		Assert.Contains(itemId, (await _folders.ListWithItemsAsync(libraryId, CancellationToken.None)).Single(f => f.Folder.Id == folderA.Id).ItemIds);

		// Single-parent: re-assigning moves it, never duplicates.
		Assert.Equal(
			ContentLibraryItemAssignmentOutcome.Assigned,
			await _folders.AssignItemAsync(libraryId, itemId, folderB!.Id, CancellationToken.None));
		IReadOnlyList<ContentLibraryFolderWithItems> afterMove = await _folders.ListWithItemsAsync(libraryId, CancellationToken.None);
		Assert.DoesNotContain(itemId, afterMove.Single(f => f.Folder.Id == folderA.Id).ItemIds);
		Assert.Contains(itemId, afterMove.Single(f => f.Folder.Id == folderB.Id).ItemIds);

		Assert.Equal(
			ContentLibraryItemAssignmentOutcome.Unassigned,
			await _folders.AssignItemAsync(libraryId, itemId, null, CancellationToken.None));
		IReadOnlyList<ContentLibraryFolderWithItems> afterUnassign = await _folders.ListWithItemsAsync(libraryId, CancellationToken.None);
		Assert.All(afterUnassign, f => Assert.DoesNotContain(itemId, f.ItemIds));
	}

	[Fact]
	public async Task AssignItemAsync_UnknownFolder_ReturnsFolderNotFound()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-item-badfolder");
		ContentLibraryItemAssignmentOutcome outcome =
			await _folders.AssignItemAsync(libraryId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
		Assert.Equal(ContentLibraryItemAssignmentOutcome.FolderNotFound, outcome);
	}

	/// <summary>
	/// N1: <c>AssignItemAsync</c>'s folder-existence check takes <c>FOR UPDATE</c>
	/// (matching <c>DeleteAsync</c>'s own lock), so a concurrent delete of the exact
	/// folder being assigned into cannot race the assign's INSERT into an unhandled
	/// SQLSTATE 23503 (a 500). This test holds the folder row locked in a second
	/// connection/transaction -- exactly the lock <c>DeleteAsync</c> takes -- proves
	/// <c>AssignItemAsync</c> blocks behind it rather than proceeding, then deletes the
	/// folder and commits, mirroring the interleaving from the review's finding: the
	/// assign must resolve cleanly to <c>FolderNotFound</c>, never throw.
	/// </summary>
	[Fact]
	public async Task AssignItemAsync_ConcurrentWithDeleteOfTheSameFolder_ResolvesToFolderNotFoundWithoutThrowing()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-item-race");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Race", CancellationToken.None);

		await using NpgsqlConnection lockConnection = new(_fixture.ConnectionString);
		await lockConnection.OpenAsync();
		await using NpgsqlTransaction lockTransaction = await lockConnection.BeginTransactionAsync();
		await using (NpgsqlCommand lockCommand = new(
			"SELECT 1 FROM content_library_folders WHERE id = $1 FOR UPDATE", lockConnection, lockTransaction))
		{
			lockCommand.Parameters.AddWithValue(folder!.Id);
			await lockCommand.ExecuteScalarAsync();
		}

		Task<ContentLibraryItemAssignmentOutcome> assignTask =
			_folders.AssignItemAsync(libraryId, Guid.NewGuid(), folder.Id, CancellationToken.None);

		// AssignItemAsync must be blocked behind the held row lock, not racing ahead.
		Task firstToComplete = await Task.WhenAny(assignTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
		Assert.NotSame(assignTask, firstToComplete);

		// Now delete the folder under the lock and commit, mirroring DeleteAsync's own
		// sequence (lock, check empty, delete, commit).
		await using (NpgsqlCommand deleteCommand = new(
			"DELETE FROM content_library_folders WHERE id = $1", lockConnection, lockTransaction))
		{
			deleteCommand.Parameters.AddWithValue(folder.Id);
			await deleteCommand.ExecuteNonQueryAsync();
		}

		await lockTransaction.CommitAsync();

		ContentLibraryItemAssignmentOutcome outcome = await assignTask;
		Assert.Equal(ContentLibraryItemAssignmentOutcome.FolderNotFound, outcome);
	}

	/// <summary>
	/// Issue #1389 AC (delivered as a schema property since #1398's repair/rebuild
	/// pass does not exist yet): folder rows and item assignments carry no reference
	/// to any on-disk path, so deleting and recreating a library's directory
	/// out-of-band -- exactly what a future repair pass does to the filesystem side --
	/// leaves every folder row and item assignment completely untouched.
	/// </summary>
	[Fact]
	public async Task Folders_AreEntirelyUnaffectedByRecreatingTheLibrarysOnDiskDirectory()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-repair-survival");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Survives", CancellationToken.None);
		Guid itemId = Guid.NewGuid();
		await _folders.AssignItemAsync(libraryId, itemId, folder!.Id, CancellationToken.None);

		ContentLibrary library = (await _libraries.GetAsync(libraryId, CancellationToken.None))!;
		Directory.Delete(library.DiskPath, recursive: true);
		Directory.CreateDirectory(library.DiskPath);

		Assert.NotNull(await _folders.GetAsync(folder.Id, CancellationToken.None));
		IReadOnlyList<ContentLibraryFolderWithItems> after = await _folders.ListWithItemsAsync(libraryId, CancellationToken.None);
		Assert.Contains(itemId, after.Single(f => f.Folder.Id == folder.Id).ItemIds);
	}

	[Fact]
	public async Task DeleteLibrary_CascadesFoldersAndItemAssignments()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-cascade");
		(_, ContentLibraryFolder? folder) = await _folders.CreateAsync(libraryId, null, "Doomed", CancellationToken.None);
		await _folders.AssignItemAsync(libraryId, Guid.NewGuid(), folder!.Id, CancellationToken.None);

		// Bypass the API-level non-empty guard: this proves the DB's own ON DELETE
		// CASCADE, not ContentLibraryFolderRepository.DeleteAsync's rejection path.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM content_libraries WHERE id = $1", connection);
		delete.Parameters.AddWithValue(libraryId);
		await delete.ExecuteNonQueryAsync();

		Assert.Empty(await _folders.ListWithItemsAsync(libraryId, CancellationToken.None));
		Assert.Null(await _folders.GetAsync(folder.Id, CancellationToken.None));
	}
}
