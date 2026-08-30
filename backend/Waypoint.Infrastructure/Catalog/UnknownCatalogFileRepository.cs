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
using Waypoint.Core.Catalog;

namespace Waypoint.Infrastructure.Catalog;

/// <inheritdoc cref="IUnknownCatalogFileRepository"/>
public sealed class UnknownCatalogFileRepository : IUnknownCatalogFileRepository
{
	private readonly string _connectionString;

	public UnknownCatalogFileRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// <c>relative_path</c> is the table's unique key (migration 0100). Insert-or-
	/// touch-last-seen only: on conflict, <c>size_bytes</c> is refreshed and
	/// <c>last_seen_at</c> advances to now, but <c>first_seen_at</c> is left alone --
	/// there is deliberately no statement anywhere in this type that removes a row
	/// (design decision Q11: alert instead of drop).
	/// </summary>
	public async Task<Guid> RecordSeenAsync(string relativePath, long? sizeBytes, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO unknown_catalog_files (relative_path, size_bytes)
			VALUES ($1, $2)
			ON CONFLICT (relative_path) DO UPDATE SET
				size_bytes = EXCLUDED.size_bytes,
				last_seen_at = now()
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(relativePath);
		command.Parameters.AddWithValue((object?)sizeBytes ?? DBNull.Value);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
	}

	public async Task<IReadOnlyList<UnknownCatalogFile>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, relative_path, size_bytes, first_seen_at, last_seen_at
			FROM unknown_catalog_files
			ORDER BY last_seen_at DESC, id
			""", connection);

		List<UnknownCatalogFile> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(new UnknownCatalogFile(
				reader.GetGuid(0),
				reader.GetString(1),
				reader.IsDBNull(2) ? null : reader.GetInt64(2),
				reader.GetFieldValue<DateTimeOffset>(3),
				reader.GetFieldValue<DateTimeOffset>(4)));
		}

		return items;
	}
}
