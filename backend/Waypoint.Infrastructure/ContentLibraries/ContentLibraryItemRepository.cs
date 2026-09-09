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

using System.Text.Json;
using Npgsql;
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Infrastructure.ContentLibraries;

/// <inheritdoc cref="IContentLibraryItemRepository"/>
public sealed class ContentLibraryItemRepository : IContentLibraryItemRepository
{
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

	private const string ProjectionSql =
		"SELECT id, library_id, directory_name, name, type, description, version, files, created_at, updated_at FROM content_library_items";

	private readonly string _connectionString;

	public ContentLibraryItemRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<(ContentLibraryItemAddOutcome Outcome, ContentLibraryItem? Item)> AddAsync(
		Guid id, Guid libraryId, string directoryName, string name, string type, string description,
		IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(type);
		ArgumentNullException.ThrowIfNull(files);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand libraryCheck = new("SELECT 1 FROM content_libraries WHERE id = $1", connection);
		libraryCheck.Parameters.AddWithValue(libraryId);
		if (await libraryCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
		{
			return (ContentLibraryItemAddOutcome.LibraryNotFound, null);
		}

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO content_library_items (id, library_id, directory_name, name, type, description, version, files)
			VALUES ($1, $2, $3, $4, $5, $6, 1, $7::jsonb)
			RETURNING id, library_id, directory_name, name, type, description, version, files, created_at, updated_at
			""", connection);
		insert.Parameters.AddWithValue(id);
		insert.Parameters.AddWithValue(libraryId);
		insert.Parameters.AddWithValue(directoryName);
		insert.Parameters.AddWithValue(name);
		insert.Parameters.AddWithValue(type);
		insert.Parameters.AddWithValue(description);
		insert.Parameters.AddWithValue(JsonSerializer.Serialize(files, SerializerOptions));

		await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return (ContentLibraryItemAddOutcome.Added, Map(reader));
	}

	public async Task<ContentLibraryItem?> GetAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1 AND library_id = $2", connection);
		command.Parameters.AddWithValue(itemId);
		command.Parameters.AddWithValue(libraryId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<ContentLibraryItem>> ListAsync(Guid libraryId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE library_id = $1 ORDER BY created_at", connection);
		command.Parameters.AddWithValue(libraryId);

		List<ContentLibraryItem> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}

		return results;
	}

	public async Task<ContentLibraryItemUpdateOutcome> UpdateAsync(
		Guid libraryId, Guid itemId, string name, string type, string description,
		IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(type);
		ArgumentNullException.ThrowIfNull(files);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand update = new(
			"""
			UPDATE content_library_items
			SET name = $1, type = $2, description = $3, files = $4::jsonb, version = version + 1
			WHERE id = $5 AND library_id = $6
			""", connection);
		update.Parameters.AddWithValue(name);
		update.Parameters.AddWithValue(type);
		update.Parameters.AddWithValue(description);
		update.Parameters.AddWithValue(JsonSerializer.Serialize(files, SerializerOptions));
		update.Parameters.AddWithValue(itemId);
		update.Parameters.AddWithValue(libraryId);

		int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		return affected == 0 ? ContentLibraryItemUpdateOutcome.NotFound : ContentLibraryItemUpdateOutcome.Updated;
	}

	public async Task<ContentLibraryItemRemoveOutcome> RemoveAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand delete = new(
			"DELETE FROM content_library_items WHERE id = $1 AND library_id = $2 RETURNING id", connection);
		delete.Parameters.AddWithValue(itemId);
		delete.Parameters.AddWithValue(libraryId);

		object? result = await delete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is null ? ContentLibraryItemRemoveOutcome.NotFound : ContentLibraryItemRemoveOutcome.Removed;
	}

	private static ContentLibraryItem Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetGuid(1),
		reader.GetString(2),
		reader.GetString(3),
		reader.GetString(4),
		reader.GetString(5),
		reader.GetInt64(6),
		JsonSerializer.Deserialize<List<ContentLibraryItemFileWrite>>(reader.GetString(7), SerializerOptions) ?? [],
		reader.GetFieldValue<DateTimeOffset>(8),
		reader.GetFieldValue<DateTimeOffset>(9));
}
