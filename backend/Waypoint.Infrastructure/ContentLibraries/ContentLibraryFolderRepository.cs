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

using Npgsql;
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Infrastructure.ContentLibraries;

/// <inheritdoc cref="IContentLibraryFolderRepository"/>
public sealed class ContentLibraryFolderRepository : IContentLibraryFolderRepository
{
	private readonly string _connectionString;

	public ContentLibraryFolderRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<(ContentLibraryFolderCreateOutcome Outcome, ContentLibraryFolder? Folder)> CreateAsync(
		Guid libraryId, Guid? parentFolderId, string name, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Locks every existing folder row of this library before validating anything
		// against it -- see the interface's type-level remarks.
		List<(Guid Id, Guid? ParentFolderId)> siblingScope = await LockLibraryFoldersAsync(connection, transaction, libraryId, cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand libraryCheck = new("SELECT 1 FROM content_libraries WHERE id = $1", connection, transaction))
		{
			libraryCheck.Parameters.AddWithValue(libraryId);
			if (await libraryCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				return (ContentLibraryFolderCreateOutcome.LibraryNotFound, null);
			}
		}

		if (parentFolderId is Guid parentId && !siblingScope.Any(f => f.Id == parentId))
		{
			return (ContentLibraryFolderCreateOutcome.ParentNotFound, null);
		}

		if (siblingScope.Any(f => f.ParentFolderId == parentFolderId))
		{
			// Cheap pre-check against the row set we already locked; the partial unique
			// indexes below are still the real arbiter for a name we somehow missed.
			bool nameTaken = false;
			await using (NpgsqlCommand nameCheck = new(
				parentFolderId is null
					? "SELECT 1 FROM content_library_folders WHERE library_id = $1 AND parent_folder_id IS NULL AND name = $2"
					: "SELECT 1 FROM content_library_folders WHERE library_id = $1 AND parent_folder_id = $2 AND name = $3",
				connection, transaction))
			{
				nameCheck.Parameters.AddWithValue(libraryId);
				if (parentFolderId is Guid pid)
				{
					nameCheck.Parameters.AddWithValue(pid);
				}

				nameCheck.Parameters.AddWithValue(name);
				nameTaken = await nameCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
			}

			if (nameTaken)
			{
				return (ContentLibraryFolderCreateOutcome.NameTaken, null);
			}
		}

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO content_library_folders (library_id, parent_folder_id, name)
			VALUES ($1, $2, $3)
			RETURNING id, library_id, parent_folder_id, name, created_at
			""", connection, transaction);
		insert.Parameters.AddWithValue(libraryId);
		insert.Parameters.AddWithValue((object?)parentFolderId ?? DBNull.Value);
		insert.Parameters.AddWithValue(name);

		try
		{
			await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			ContentLibraryFolder folder = Map(reader);
			await reader.DisposeAsync().ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return (ContentLibraryFolderCreateOutcome.Created, folder);
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return (ContentLibraryFolderCreateOutcome.NameTaken, null);
		}
	}

	public async Task<IReadOnlyList<ContentLibraryFolderWithItems>> ListWithItemsAsync(Guid libraryId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		Dictionary<Guid, List<Guid>> itemsByFolder = [];
		await using (NpgsqlCommand itemsCommand = new(
			"SELECT folder_id, item_id FROM content_library_item_folders WHERE library_id = $1", connection))
		{
			itemsCommand.Parameters.AddWithValue(libraryId);
			await using NpgsqlDataReader reader = await itemsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				Guid folderId = reader.GetGuid(0);
				if (!itemsByFolder.TryGetValue(folderId, out List<Guid>? items))
				{
					items = [];
					itemsByFolder[folderId] = items;
				}

				items.Add(reader.GetGuid(1));
			}
		}

		List<ContentLibraryFolderWithItems> results = [];
		await using NpgsqlCommand foldersCommand = new(
			"SELECT id, library_id, parent_folder_id, name, created_at FROM content_library_folders WHERE library_id = $1 ORDER BY name",
			connection);
		foldersCommand.Parameters.AddWithValue(libraryId);
		await using NpgsqlDataReader folderReader = await foldersCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await folderReader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			ContentLibraryFolder folder = Map(folderReader);
			IReadOnlyList<Guid> itemIds = itemsByFolder.TryGetValue(folder.Id, out List<Guid>? items) ? items : [];
			results.Add(new ContentLibraryFolderWithItems(folder, itemIds));
		}

		return results;
	}

	public async Task<ContentLibraryFolder?> GetAsync(Guid folderId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT id, library_id, parent_folder_id, name, created_at FROM content_library_folders WHERE id = $1", connection);
		command.Parameters.AddWithValue(folderId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<ContentLibraryFolderUpdateOutcome> UpdateAsync(
		Guid folderId, string name, Guid? parentFolderId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		Guid libraryId;
		await using (NpgsqlCommand find = new("SELECT library_id FROM content_library_folders WHERE id = $1", connection, transaction))
		{
			find.Parameters.AddWithValue(folderId);
			object? result = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				return ContentLibraryFolderUpdateOutcome.NotFound;
			}

			libraryId = (Guid)result;
		}

		List<(Guid Id, Guid? ParentFolderId)> siblingScope = await LockLibraryFoldersAsync(connection, transaction, libraryId, cancellationToken).ConfigureAwait(false);

		if (parentFolderId is Guid parentId)
		{
			if (parentId == folderId)
			{
				return ContentLibraryFolderUpdateOutcome.CycleRejected;
			}

			if (!siblingScope.Any(f => f.Id == parentId))
			{
				return ContentLibraryFolderUpdateOutcome.ParentNotFound;
			}

			// Walks the proposed new parent's ancestor chain against the just-locked
			// row set: if the folder being moved appears in that chain, the move would
			// place it under its own descendant.
			Guid? cursor = parentId;
			HashSet<Guid> visited = [];
			while (cursor is Guid cursorId)
			{
				if (cursorId == folderId)
				{
					return ContentLibraryFolderUpdateOutcome.CycleRejected;
				}

				if (!visited.Add(cursorId))
				{
					break; // Defensive: a pre-existing cycle should be impossible, never loop forever on one.
				}

				cursor = siblingScope.SingleOrDefault(f => f.Id == cursorId).ParentFolderId;
			}
		}

		await using NpgsqlCommand update = new(
			"UPDATE content_library_folders SET name = $1, parent_folder_id = $2 WHERE id = $3", connection, transaction);
		update.Parameters.AddWithValue(name);
		update.Parameters.AddWithValue((object?)parentFolderId ?? DBNull.Value);
		update.Parameters.AddWithValue(folderId);

		try
		{
			await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return ContentLibraryFolderUpdateOutcome.Updated;
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return ContentLibraryFolderUpdateOutcome.NameTaken;
		}
	}

	public async Task<ContentLibraryFolderDeleteOutcome> DeleteAsync(Guid folderId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand lockRow = new("SELECT id FROM content_library_folders WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockRow.Parameters.AddWithValue(folderId);
			if (await lockRow.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				return ContentLibraryFolderDeleteOutcome.NotFound;
			}
		}

		await using (NpgsqlCommand emptyCheck = new(
			"""
			SELECT
				EXISTS(SELECT 1 FROM content_library_folders WHERE parent_folder_id = $1)
				OR EXISTS(SELECT 1 FROM content_library_item_folders WHERE folder_id = $1)
			""", connection, transaction))
		{
			emptyCheck.Parameters.AddWithValue(folderId);
			if ((bool)(await emptyCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
			{
				return ContentLibraryFolderDeleteOutcome.NotEmpty;
			}
		}

		await using NpgsqlCommand delete = new("DELETE FROM content_library_folders WHERE id = $1", connection, transaction);
		delete.Parameters.AddWithValue(folderId);
		await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return ContentLibraryFolderDeleteOutcome.Deleted;
	}

	public async Task<ContentLibraryItemAssignmentOutcome> AssignItemAsync(
		Guid libraryId, Guid itemId, Guid? folderId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand libraryCheck = new("SELECT 1 FROM content_libraries WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			libraryCheck.Parameters.AddWithValue(libraryId);
			if (await libraryCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				return ContentLibraryItemAssignmentOutcome.LibraryNotFound;
			}
		}

		if (folderId is null)
		{
			await using NpgsqlCommand deleteAssignment = new(
				"DELETE FROM content_library_item_folders WHERE library_id = $1 AND item_id = $2", connection, transaction);
			deleteAssignment.Parameters.AddWithValue(libraryId);
			deleteAssignment.Parameters.AddWithValue(itemId);
			await deleteAssignment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return ContentLibraryItemAssignmentOutcome.Unassigned;
		}

		await using (NpgsqlCommand folderCheck = new(
			"SELECT 1 FROM content_library_folders WHERE id = $1 AND library_id = $2", connection, transaction))
		{
			folderCheck.Parameters.AddWithValue(folderId.Value);
			folderCheck.Parameters.AddWithValue(libraryId);
			if (await folderCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				return ContentLibraryItemAssignmentOutcome.FolderNotFound;
			}
		}

		await using NpgsqlCommand upsert = new(
			"""
			INSERT INTO content_library_item_folders (library_id, item_id, folder_id)
			VALUES ($1, $2, $3)
			ON CONFLICT (library_id, item_id) DO UPDATE SET folder_id = excluded.folder_id, assigned_at = now()
			""", connection, transaction);
		upsert.Parameters.AddWithValue(libraryId);
		upsert.Parameters.AddWithValue(itemId);
		upsert.Parameters.AddWithValue(folderId.Value);
		await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return ContentLibraryItemAssignmentOutcome.Assigned;
	}

	/// <summary>
	/// Locks every folder row of <paramref name="libraryId"/> with <c>FOR UPDATE</c>
	/// and returns the (id, parent_folder_id) pairs needed for the sibling-name and
	/// cycle checks that follow -- see the interface's type-level remarks for why
	/// locking the whole library's tree, rather than just the row(s) being touched, is
	/// what makes those checks trustworthy against a concurrent structural mutation.
	/// </summary>
	private static async Task<List<(Guid Id, Guid? ParentFolderId)>> LockLibraryFoldersAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid libraryId, CancellationToken cancellationToken)
	{
		List<(Guid Id, Guid? ParentFolderId)> rows = [];
		await using NpgsqlCommand lockCommand = new(
			"SELECT id, parent_folder_id FROM content_library_folders WHERE library_id = $1 FOR UPDATE", connection, transaction);
		lockCommand.Parameters.AddWithValue(libraryId);
		await using NpgsqlDataReader reader = await lockCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
		}

		return rows;
	}

	private static ContentLibraryFolder Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetGuid(1),
		reader.IsDBNull(2) ? null : reader.GetGuid(2),
		reader.GetString(3),
		reader.GetFieldValue<DateTimeOffset>(4));
}
