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
		string diskPath = ResolveDiskPath(name);

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

		// The DB's own UNIQUE constraints are the SOLE serializer for a concurrent
		// create/create race -- nothing on disk is touched, read, or sampled before
		// this INSERT resolves. `ON CONFLICT (name) DO NOTHING` is the arbiter for the
		// `name` constraint and yields a losing caller a graceful zero-row result; but
		// `disk_path` is deterministically derived from `name` (ResolveDiskPath), so
		// two concurrent inserts for the same name ALSO collide on `disk_path` --  a
		// second, distinct UNIQUE constraint the ON CONFLICT clause's single arbiter
		// cannot cover (Postgres accepts only one conflict target per INSERT). That
		// collision surfaces as a genuine 23505 unique_violation rather than a
		// zero-row result; it means exactly the same thing (this call lost the race)
		// and is handled identically. Either way, a losing caller returns NameTaken
		// having never created a directory of its own, so there is nothing for it to
		// clean up and no way for it to reach (let alone delete) the winner's
		// directory.
		ContentLibrary library;
		try
		{
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return (ContentLibraryCreateOutcome.NameTaken, null);
			}

			library = Map(reader);
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return (ContentLibraryCreateOutcome.NameTaken, null);
		}

		try
		{
			Directory.CreateDirectory(diskPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// This call won the name and its row exists, but the directory that row
			// promises could not be provisioned. Compensate by removing the row again
			// (best effort against a further failure here would just re-litigate the
			// same problem) so a failed create never leaves a row without a
			// directory -- the interface's documented contract -- then rethrow so the
			// caller still observes the failure.
			await using NpgsqlCommand cleanup = new("DELETE FROM content_libraries WHERE id = $1", connection);
			cleanup.Parameters.AddWithValue(library.Id);
			await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			throw;
		}

		return (ContentLibraryCreateOutcome.Created, library);
	}

	/// <summary>
	/// Derives a library's on-disk directory from its operator-supplied name and
	/// validates it before any filesystem or database call is made. Two independent
	/// checks, not one: <see cref="Path.GetFileName"/> equality (plus the literal
	/// <c>.</c>/<c>..</c> segments, which <see cref="Path.GetFileName"/> alone does not
	/// reject) enforces that <paramref name="name"/> is a single path segment with no
	/// separators, and the resolved-path prefix check catches anything the segment
	/// check might miss (belt-and-suspenders, not a substitute for it) --
	/// <see cref="ContentLibrariesController"/>'s <c>NamePattern</c> regex is the
	/// operator-facing 400 for the same input one layer up, but this is the guard that
	/// actually stands between an operator-controlled string and a real filesystem
	/// path, because this is the code that touches the filesystem.
	/// </summary>
	private string ResolveDiskPath(string name)
	{
		if (name is "." or ".." || Path.GetFileName(name) != name || Path.IsPathRooted(name))
		{
			throw new ArgumentException($"'{name}' is not a valid content library name.", nameof(name));
		}

		string rootFullPath = Path.GetFullPath(_rootPath);
		string diskPath = Path.GetFullPath(Path.Combine(rootFullPath, name));
		string rootWithSeparator = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
			? rootFullPath
			: rootFullPath + Path.DirectorySeparatorChar;
		if (!diskPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
		{
			throw new ArgumentException($"'{name}' does not resolve inside the content-library root.", nameof(name));
		}

		return diskPath;
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

		// Directory removed BEFORE the row, deliberately unguarded: if a file appears
		// between the emptiness check above and this call, or the unlink is denied,
		// Directory.Delete throws HERE and the row delete below never runs -- the row
		// and the (still real) directory both survive, which is recoverable. The
		// alternative order (row first) is what turns the same failure into an
		// unrecoverable row-gone-directory-behind orphan, which is the state this
		// repository's documented contract says a delete never produces.
		if (Directory.Exists(existing.DiskPath))
		{
			Directory.Delete(existing.DiskPath);
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM content_libraries WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is null ? ContentLibraryDeleteOutcome.NotFound : ContentLibraryDeleteOutcome.Deleted;
	}

	private static ContentLibrary Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.GetFieldValue<DateTimeOffset>(3),
		reader.GetFieldValue<DateTimeOffset>(4));
}
