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

/// <inheritdoc cref="IContentLibraryRepository"/>
public sealed class ContentLibraryRepository : IContentLibraryRepository
{
	private const string ProjectionSql = "SELECT id, name, disk_path, created_at, updated_at FROM content_libraries";

	private readonly string _connectionString;
	private readonly string _rootPath;

	public ContentLibraryRepository(string connectionString, string rootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		_connectionString = connectionString;
		_rootPath = rootPath;
	}

	public async Task<(ContentLibraryCreateOutcome Outcome, ContentLibrary? Library)> CreateAsync(string name, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		string diskPath = Path.Combine(_rootPath, name);
		// Directory.CreateDirectory is idempotent -- when this call loses a create race
		// against an already-existing library of the same name, diskPath is that SAME
		// directory, not a fresh one. Remember whether it pre-existed so the orphan
		// cleanup below can never delete a real library's directory out from under it.
		bool diskPathAlreadyExisted = Directory.Exists(diskPath);
		Directory.CreateDirectory(diskPath);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO content_libraries (name, disk_path)
			VALUES ($1, $2)
			ON CONFLICT (name) DO NOTHING
			RETURNING id, name, disk_path, created_at, updated_at
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(diskPath);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return (ContentLibraryCreateOutcome.Created, Map(reader));
		}

		// Name already taken. Only remove diskPath when THIS call created it fresh
		// (diskPathAlreadyExisted is false) -- otherwise diskPath is an existing
		// library's real directory (created by an earlier CreateAsync, or racing
		// concurrently for the same name) and must never be touched here. Best-effort:
		// a failure to remove a genuinely orphaned empty directory is swallowed, not
		// thrown -- it is harmless clutter, not a correctness problem.
		if (!diskPathAlreadyExisted)
		{
			try
			{
				Directory.Delete(diskPath);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		return (ContentLibraryCreateOutcome.NameTaken, null);
	}

	public async Task<ContentLibrary?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<ContentLibrary>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY name", connection);

		List<ContentLibrary> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}

		return results;
	}

	public async Task<ContentLibraryDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		ContentLibrary? existing = await GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return ContentLibraryDeleteOutcome.NotFound;
		}

		// Emptiness is checked against the real directory, not a DB-tracked item count --
		// this slice has no items table yet (#1396). Directory.Exists is false either
		// when nothing was ever written there or someone removed it out-of-band; both
		// count as "empty" for this check's purpose.
		if (Directory.Exists(existing.DiskPath) && Directory.EnumerateFileSystemEntries(existing.DiskPath).Any())
		{
			return ContentLibraryDeleteOutcome.NotEmpty;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM content_libraries WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		if (result is null)
		{
			return ContentLibraryDeleteOutcome.NotFound;
		}

		if (Directory.Exists(existing.DiskPath))
		{
			Directory.Delete(existing.DiskPath);
		}

		return ContentLibraryDeleteOutcome.Deleted;
	}

	private static ContentLibrary Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.GetFieldValue<DateTimeOffset>(3),
		reader.GetFieldValue<DateTimeOffset>(4));
}
