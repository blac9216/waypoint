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
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Catalog;

/// <inheritdoc cref="IUnknownCatalogFileRepository"/>
public sealed class UnknownCatalogFileRepository : IUnknownCatalogFileRepository
{
	private readonly string _connectionString;
	private readonly IJobEventPublisher? _events;

	/// <summary>
	/// <paramref name="events"/> is optional (default null, same "best-effort
	/// observability, not every caller needs it" convention as
	/// <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository"/>'s own
	/// <c>IJobEventPublisher?</c> constructor parameter) so every existing
	/// registration/test that constructs this type with just a connection string
	/// keeps compiling unchanged.
	/// </summary>
	public UnknownCatalogFileRepository(string connectionString, IJobEventPublisher? events = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
		_events = events;
	}

	/// <summary>
	/// <c>relative_path</c> is the table's unique key (migration 0100). Insert-or-
	/// touch-last-seen only: on conflict, <c>size_bytes</c> is refreshed and
	/// <c>last_seen_at</c> advances to now, but <c>first_seen_at</c> is left alone --
	/// there is deliberately no statement anywhere in this type that removes a row
	/// (design decision Q11: alert instead of drop).
	///
	/// Issue #1495 AC3: a genuinely new unknown file (not a re-touch of one already
	/// on record) emits <see cref="JobEventTypes.SystemNotice"/> through the same
	/// best-effort event sink <c>CatalogIndexJobHandler</c> uses for auth failures
	/// (<c>jobs.note is a sink too</c>, security.md control 1) -- appliance-wide
	/// (job_id/run_id both null, matching the type's scope doc) since no job/run
	/// context exists at this layer. <c>(xmax = 0)</c> is the standard Postgres
	/// "did this row just get inserted, or did the ON CONFLICT branch fire"
	/// discriminator: a freshly inserted row's system <c>xmax</c> is still zero,
	/// while the ON CONFLICT DO UPDATE path sets it to the current transaction's id.
	/// This slice does not yet have a caller (the presence sweep, #1503/#1512, is
	/// out of scope here) -- proven directly against this repository's own
	/// unit/integration tests until that wiring lands.
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
			RETURNING id, (xmax = 0) AS inserted
			""", connection);
		command.Parameters.AddWithValue(relativePath);
		command.Parameters.AddWithValue((object?)sizeBytes ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		Guid id = reader.GetGuid(0);
		bool wasNewlyInserted = reader.GetBoolean(1);
		await reader.DisposeAsync().ConfigureAwait(false);

		if (wasNewlyInserted && _events is not null)
		{
			string payload = JsonSerializer.Serialize(new
			{
				kind = "catalog.unknown_file",
				relative_path = relativePath,
				size_bytes = sizeBytes,
			});
			await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
		}

		return id;
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
