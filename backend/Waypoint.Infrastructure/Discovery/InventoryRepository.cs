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
using Waypoint.Core.Discovery;

namespace Waypoint.Infrastructure.Discovery;

/// <summary>
/// Storage for <c>inventory_items</c> (issue #21, epic #13, migration 0011): plain
/// Npgsql, no ORM -- same convention as every other repository in this codebase.
/// </summary>
public sealed class InventoryRepository
{
	private const string ProjectionSql = """
		SELECT id, target_id, parent_id, type, moref, name, build, maintenance_mode, last_seen_at, removed_at, created_at, updated_at, version, instance_uuid
		FROM inventory_items
		""";

	private readonly string _connectionString;

	public InventoryRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// Lists the current (non-removed) inventory tree for a target, ordered so callers
	/// can build the tree in a single forward pass (clusters/hosts before their
	/// children -- matches insertion/discovery order via <c>created_at</c>).
	/// </summary>
	public async Task<IReadOnlyList<InventoryItem>> ListAsync(Guid targetId, bool includeRemoved, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		string removedClause = includeRemoved ? string.Empty : " AND removed_at IS NULL";
		await using NpgsqlCommand command = new(
			$"{ProjectionSql} WHERE target_id = $1{removedClause} ORDER BY type, created_at", connection);
		command.Parameters.AddWithValue(targetId);

		List<InventoryItem> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	/// <summary>
	/// Applies one discovery pass: upserts every discovered item by (target_id, moref)
	/// -- the re-discovery-is-idempotent path (issue #21 acceptance criterion, "no
	/// duplicate hosts") -- and marks every previously-seen, not-yet-removed item under
	/// this target that this pass did NOT report as removed (<c>removed_at = now()</c>).
	/// A moref that reappears in a later pass is un-marked (<c>removed_at</c> reset to
	/// NULL) rather than inserted as a new row.
	///
	/// Runs as a single transaction: a partial upsert never leaves the target's
	/// inventory in a mixed old/new state visible to a concurrent
	/// <c>GET /targets/{id}/inventory</c> read.
	///
	/// <paramref name="items"/> must be parent-before-child ordered (see
	/// <see cref="DiscoveredInventoryItem"/>'s doc comment) -- each item's
	/// <c>ParentMoref</c> is resolved against parent rows already upserted earlier in
	/// this same pass.
	///
	/// <paramref name="advanceAbsence"/> gates ONLY the removal-detection block below
	/// (issue #865, ADR-0023): the caller passes <c>false</c> for a discovery pass its
	/// PowerShell boundary reported as incomplete (some subtree failed to enumerate),
	/// so the items this pass DID see are still upserted/un-removed as unverified-cache
	/// refreshes, but nothing not seen this pass is marked removed -- a partial
	/// boundary must never be read as "the rest is confirmed gone." The per-item upsert
	/// loop itself is unchanged either way.
	/// </summary>
	public async Task<InventoryUpsertOutcome> UpsertDiscoveryResultsAsync(
		Guid targetId, IReadOnlyList<DiscoveredInventoryItem> items, CancellationToken cancellationToken, bool advanceAbsence = true)
	{
		ArgumentNullException.ThrowIfNull(items);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// moref -> row id, seeded as each item upserts, so a later item's ParentMoref
		// resolves to an id from earlier in THIS pass without a round-trip per lookup.
		Dictionary<string, Guid> idByMoref = new(StringComparer.Ordinal);
		List<string> seenMorefs = [];

		int upserted = 0;
		foreach (DiscoveredInventoryItem item in items)
		{
			Guid? parentId = null;
			if (item.ParentMoref is not null)
			{
				if (!idByMoref.TryGetValue(item.ParentMoref, out Guid resolvedParentId))
				{
					// A child reported before its parent (or an orphaned ParentMoref the
					// PowerShell layer never actually enumerated) is a malformed discovery
					// result -- skip this one item rather than failing the whole pass, same
					// "one bad row must not block the rest" principle CatalogIndexJobHandler
					// applies to a malformed artifact row.
					continue;
				}

				parentId = resolvedParentId;
			}

			await using NpgsqlCommand upsert = new(
				"""
				INSERT INTO inventory_items (target_id, parent_id, type, moref, name, build, maintenance_mode, last_seen_at, removed_at, version, instance_uuid)
				VALUES ($1, $2, $3, $4, $5, $6, $7, now(), NULL, $8, $9)
				ON CONFLICT (target_id, moref) DO UPDATE SET
					parent_id = EXCLUDED.parent_id,
					type = EXCLUDED.type,
					name = EXCLUDED.name,
					build = EXCLUDED.build,
					maintenance_mode = EXCLUDED.maintenance_mode,
					last_seen_at = now(),
					removed_at = NULL,
					version = EXCLUDED.version,
					instance_uuid = EXCLUDED.instance_uuid
				RETURNING id
				""", connection, transaction);
			upsert.Parameters.AddWithValue(targetId);
			upsert.Parameters.AddWithValue((object?)parentId ?? DBNull.Value);
			upsert.Parameters.AddWithValue(item.Type);
			upsert.Parameters.AddWithValue(item.Moref);
			upsert.Parameters.AddWithValue(item.Name);
			upsert.Parameters.AddWithValue((object?)item.Build ?? DBNull.Value);
			upsert.Parameters.AddWithValue((object?)item.MaintenanceMode ?? DBNull.Value);
			upsert.Parameters.AddWithValue((object?)item.Version ?? DBNull.Value);
			upsert.Parameters.AddWithValue((object?)item.InstanceUuid ?? DBNull.Value);

			object? result = await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is Guid rowId)
			{
				idByMoref[item.Moref] = rowId;
				seenMorefs.Add(item.Moref);
				upserted++;
			}
		}

		// Removal detection: any not-yet-removed row under this target whose moref this
		// pass did not report gets soft-deleted. An empty pass (0 items -- e.g. vCenter
		// unreachable would have failed the job before reaching here, but an
		// empty-but-successful enumeration is possible) marks everything removed, which
		// is the correct read: this pass genuinely saw nothing.
		//
		// Issue #865: skipped entirely when advanceAbsence is false -- a partial pass's
		// unseen rows keep whatever removed_at they already had (ADR-0023 unverified
		// cache), rather than this pass's incomplete view of "who's still there" being
		// read as authoritative.
		int removed = 0;
		if (advanceAbsence)
		{
			await using NpgsqlCommand removeStale = new(
				"""
				UPDATE inventory_items
				SET removed_at = now()
				WHERE target_id = $1 AND removed_at IS NULL AND NOT (moref = ANY($2))
				""", connection, transaction);
			removeStale.Parameters.AddWithValue(targetId);
			removeStale.Parameters.AddWithValue(seenMorefs.ToArray());
			removed = await removeStale.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new InventoryUpsertOutcome(upserted, removed);
	}

	private static InventoryItem Map(NpgsqlDataReader reader)
	{
		return new InventoryItem(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.IsDBNull(2) ? null : reader.GetGuid(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetString(5),
			reader.IsDBNull(6) ? null : reader.GetString(6),
			reader.IsDBNull(7) ? null : reader.GetBoolean(7),
			reader.GetFieldValue<DateTimeOffset>(8),
			reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
			reader.GetFieldValue<DateTimeOffset>(10),
			reader.GetFieldValue<DateTimeOffset>(11),
			reader.IsDBNull(12) ? null : reader.GetString(12),
			reader.IsDBNull(13) ? null : reader.GetString(13));
	}
}
