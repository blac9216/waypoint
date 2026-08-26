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
using Waypoint.Core.Components;

namespace Waypoint.Infrastructure.Components;

/// <summary>
/// Storage for <c>components</c>/<c>component_observations</c> (migration 0054):
/// plain Npgsql, no ORM -- same convention as every other repository in this codebase
/// (cf. <see cref="Waypoint.Infrastructure.Discovery.InventoryRepository"/>, the flat
/// inventory-cache analogue one layer below this stable-identity layer).
/// </summary>
public sealed class ComponentRepository : IComponentRepository
{
	private const string ProjectionSql = """
		SELECT id, parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
		       vendor_identity, display_name, lifecycle, configured_fact, discovered_fact, fact_conflict,
		       first_seen_at, last_seen_at, continuous_absence_since, retired_at, created_at, updated_at
		FROM components
		""";

	private static readonly JsonSerializerOptions FactSerializerOptions = new(JsonSerializerDefaults.Web);

	private readonly string _connectionString;

	public ComponentRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<Component>> ListForTargetAsync(Guid targetId, bool includeRetired, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		string retiredClause = includeRetired ? string.Empty : " AND lifecycle <> 'retired'";
		await using NpgsqlCommand command = new(
			$"{ProjectionSql} WHERE parent_target_id = $1{retiredClause} ORDER BY catalog_component_key, created_at", connection);
		command.Parameters.AddWithValue(targetId);

		List<Component> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<Component?> GetAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(componentId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<ComponentUpsertOutcome> UpsertDiscoveredAsync(
		Guid targetId, IReadOnlyList<DiscoveredComponent> items, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(items);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// vendor identity (or, absent one, catalog key) -> row id, seeded as each item
		// upserts, so a later item's ParentVendorIdentity resolves to an id from earlier
		// in THIS pass without a round trip -- same pattern InventoryRepository uses for
		// ParentMoref.
		Dictionary<string, Guid> idByVendorIdentity = new(StringComparer.Ordinal);
		List<(string? VendorIdentity, string CatalogKey, Guid? ParentComponentId)> seen = [];

		int upserted = 0;
		int reconnected = 0;
		foreach (DiscoveredComponent item in items)
		{
			Guid? parentComponentId = null;
			if (item.ParentVendorIdentity is not null)
			{
				if (!idByVendorIdentity.TryGetValue(item.ParentVendorIdentity, out Guid resolvedParentId))
				{
					// A child reported before its parent, or an orphaned reference the
					// discovery layer never actually enumerated -- skip this one item
					// rather than failing the whole pass (InventoryRepository precedent).
					continue;
				}

				parentComponentId = resolvedParentId;
			}

			string? factJson = item.ExactVersion is null
				? null
				: JsonSerializer.Serialize(new { exact_version = item.ExactVersion, observed_at = DateTimeOffset.UtcNow }, FactSerializerOptions);

			// Issue #840: a single atomic statement replaces the former check-then-insert
			// (a separate existence SELECT, a separate lifecycle SELECT, then a branch to
			// UPDATE or INSERT) -- three round trips that raced under concurrent discovery
			// of the same identity (two runner replicas, or a manual refresh overlapping a
			// scheduled one, both reconciling the same target). The leading `prior` CTE
			// captures whatever lifecycle the row had (if any) BEFORE this statement's own
			// write touches it, in the same snapshot as the upsert itself -- giving an
			// atomic, race-free "was this a genuine reconnect" signal without a second
			// query. Migration 0054 backs both identity cases with a real unique
			// constraint/index, so both branches below bind to a real ON CONFLICT target:
			//   * vendor_identity IS NOT NULL: components_vendor_identity_unique, the
			//     plain UNIQUE (parent_target_id, catalog_component_key, vendor_identity)
			//     table constraint -- valid here because vendor_identity is NOT NULL, so
			//     there is no Postgres NULL-distinctness gap for this branch.
			//   * vendor_identity IS NULL: idx_components_no_vendor_identity_unique, the
			//     COALESCE-sentinel partial unique index -- ON CONFLICT names the exact
			//     indexed expression list plus a matching WHERE predicate, required for
			//     Postgres to bind ON CONFLICT to a partial index.
			Guid rowId;
			bool wasReconnect;
			if (item.VendorIdentity is not null)
			{
				await using NpgsqlCommand upsert = new(
					"""
					WITH prior AS (
					    SELECT lifecycle FROM components
					    WHERE parent_target_id = $1 AND catalog_component_key = $4 AND vendor_identity = $5
					)
					INSERT INTO components (parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
					                         vendor_identity, display_name, lifecycle, discovered_fact, last_seen_at)
					VALUES ($1, $2, $3, $4, $5, $6, 'active', $7::jsonb, now())
					ON CONFLICT (parent_target_id, catalog_component_key, vendor_identity) DO UPDATE SET
					    catalog_component_id = COALESCE(EXCLUDED.catalog_component_id, components.catalog_component_id),
					    display_name = EXCLUDED.display_name,
					    lifecycle = 'active',
					    discovered_fact = COALESCE(EXCLUDED.discovered_fact, components.discovered_fact),
					    fact_conflict = CASE WHEN EXCLUDED.discovered_fact IS NULL THEN components.fact_conflict
					                          ELSE (components.configured_fact IS NOT NULL AND components.configured_fact->>'exact_version' <> EXCLUDED.discovered_fact->>'exact_version')
					                     END,
					    last_seen_at = now(),
					    continuous_absence_since = NULL
					RETURNING id, (SELECT lifecycle FROM prior) AS prior_lifecycle
					""", connection, transaction);
				upsert.Parameters.AddWithValue(targetId);
				upsert.Parameters.AddWithValue((object?)parentComponentId ?? DBNull.Value);
				upsert.Parameters.AddWithValue((object?)item.CatalogComponentId ?? DBNull.Value);
				upsert.Parameters.AddWithValue(item.CatalogComponentKey);
				upsert.Parameters.AddWithValue(item.VendorIdentity);
				upsert.Parameters.AddWithValue(item.DisplayName);
				upsert.Parameters.AddWithValue((object?)factJson ?? DBNull.Value);

				await using NpgsqlDataReader reader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
				rowId = reader.GetGuid(0);
				string? priorLifecycle = reader.IsDBNull(1) ? null : reader.GetString(1);
				wasReconnect = priorLifecycle is not null && priorLifecycle != Waypoint.Core.Components.ComponentLifecycleStates.Active;
			}
			else
			{
				await using NpgsqlCommand upsert = new(
					"""
					WITH prior AS (
					    SELECT lifecycle FROM components
					    WHERE parent_target_id = $1
					      AND COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid) = COALESCE($2, '00000000-0000-0000-0000-000000000000'::uuid)
					      AND catalog_component_key = $3
					      AND vendor_identity IS NULL
					)
					INSERT INTO components (parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
					                         vendor_identity, display_name, lifecycle, discovered_fact, last_seen_at)
					VALUES ($1, $2, $6, $3, NULL, $4, 'active', $5::jsonb, now())
					ON CONFLICT (parent_target_id, COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid), catalog_component_key)
					    WHERE vendor_identity IS NULL
					    DO UPDATE SET
					        catalog_component_id = COALESCE(EXCLUDED.catalog_component_id, components.catalog_component_id),
					        display_name = EXCLUDED.display_name,
					        lifecycle = 'active',
					        discovered_fact = COALESCE(EXCLUDED.discovered_fact, components.discovered_fact),
					        fact_conflict = CASE WHEN EXCLUDED.discovered_fact IS NULL THEN components.fact_conflict
					                              ELSE (components.configured_fact IS NOT NULL AND components.configured_fact->>'exact_version' <> EXCLUDED.discovered_fact->>'exact_version')
					                         END,
					        last_seen_at = now(),
					        continuous_absence_since = NULL
					RETURNING id, (SELECT lifecycle FROM prior) AS prior_lifecycle
					""", connection, transaction);
				upsert.Parameters.AddWithValue(targetId);
				upsert.Parameters.AddWithValue((object?)parentComponentId ?? DBNull.Value);
				upsert.Parameters.AddWithValue(item.CatalogComponentKey);
				upsert.Parameters.AddWithValue(item.DisplayName);
				upsert.Parameters.AddWithValue((object?)factJson ?? DBNull.Value);
				upsert.Parameters.AddWithValue((object?)item.CatalogComponentId ?? DBNull.Value);

				await using NpgsqlDataReader reader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
				rowId = reader.GetGuid(0);
				string? priorLifecycle = reader.IsDBNull(1) ? null : reader.GetString(1);
				wasReconnect = priorLifecycle is not null && priorLifecycle != Waypoint.Core.Components.ComponentLifecycleStates.Active;
			}

			if (factJson is not null)
			{
				await using NpgsqlCommand observation = new(
					"""
					INSERT INTO component_observations (component_id, source, observed_fact, outcome)
					VALUES ($1, 'discovered', $2::jsonb, 'recorded')
					""", connection, transaction);
				observation.Parameters.AddWithValue(rowId);
				observation.Parameters.AddWithValue(factJson);
				await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			string identityKey = item.VendorIdentity ?? $"{parentComponentId}:{item.CatalogComponentKey}";
			idByVendorIdentity[identityKey] = rowId;
			seen.Add((item.VendorIdentity, item.CatalogComponentKey, parentComponentId));

			if (wasReconnect)
			{
				reconnected++;
			}
			else
			{
				upserted++;
			}
		}

		// Mark absent: any component under this target that is currently active (or
		// already absent -- to preserve/extend continuous_absence_since is handled by
		// only setting continuous_absence_since when it is currently NULL) and was not
		// reported this pass. Retired components are left retired (ADR-0023: retirement
		// is not re-derived every pass).
		int markedAbsent = 0;
		await using (NpgsqlCommand loadCandidates = new(
			"SELECT id, vendor_identity, catalog_component_key, parent_component_id FROM components WHERE parent_target_id = $1 AND lifecycle <> 'retired'",
			connection, transaction))
		{
			loadCandidates.Parameters.AddWithValue(targetId);
			List<(Guid Id, string? VendorIdentity, string CatalogKey, Guid? ParentComponentId)> candidates = [];
			await using (NpgsqlDataReader reader = await loadCandidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
			{
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					candidates.Add((
						reader.GetGuid(0),
						reader.IsDBNull(1) ? null : reader.GetString(1),
						reader.GetString(2),
						reader.IsDBNull(3) ? null : reader.GetGuid(3)));
				}
			}

			foreach ((Guid id, string? vendorIdentity, string catalogKey, Guid? parentComponentId) in candidates)
			{
				bool wasSeen = seen.Any(s => s.VendorIdentity == vendorIdentity && s.CatalogKey == catalogKey && s.ParentComponentId == parentComponentId);
				if (wasSeen)
				{
					continue;
				}

				await using NpgsqlCommand markAbsent = new(
					"""
					UPDATE components
					SET lifecycle = 'absent',
					    continuous_absence_since = COALESCE(continuous_absence_since, now())
					WHERE id = $1
					""", connection, transaction);
				markAbsent.Parameters.AddWithValue(id);
				markedAbsent += await markAbsent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

				await using NpgsqlCommand observation = new(
					"""
					INSERT INTO component_observations (component_id, source, observed_fact, outcome)
					VALUES ($1, 'discovered', $2::jsonb, 'absent')
					""", connection, transaction);
				observation.Parameters.AddWithValue(id);
				observation.Parameters.AddWithValue(JsonSerializer.Serialize(new { observed_at = DateTimeOffset.UtcNow }, FactSerializerOptions));
				await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new ComponentUpsertOutcome(upserted, markedAbsent, reconnected);
	}

	public async Task<ComponentWriteOutcome> SetConfiguredFactAsync(Guid componentId, string exactVersion, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(exactVersion);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string factJson = JsonSerializer.Serialize(new { exact_version = exactVersion, observed_at = DateTimeOffset.UtcNow }, FactSerializerOptions);

		await using NpgsqlCommand update = new(
			"""
			UPDATE components
			SET configured_fact = $2::jsonb,
			    fact_conflict = (discovered_fact IS NOT NULL AND discovered_fact->>'exact_version' <> $3)
			WHERE id = $1
			RETURNING fact_conflict
			""", connection, transaction);
		update.Parameters.AddWithValue(componentId);
		update.Parameters.AddWithValue(factJson);
		update.Parameters.AddWithValue(exactVersion);

		object? result = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		if (result is null)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return ComponentWriteOutcome.NotFound;
		}

		bool conflict = (bool)result;
		await using NpgsqlCommand observation = new(
			"""
			INSERT INTO component_observations (component_id, source, observed_fact, outcome)
			VALUES ($1, 'configured', $2::jsonb, $3)
			""", connection, transaction);
		observation.Parameters.AddWithValue(componentId);
		observation.Parameters.AddWithValue(factJson);
		observation.Parameters.AddWithValue(conflict ? "conflict" : "recorded");
		await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return ComponentWriteOutcome.Ok;
	}

	public async Task<int> RetireContinuouslyAbsentAsync(TimeSpan threshold, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			UPDATE components
			SET lifecycle = 'retired', retired_at = now()
			WHERE lifecycle = 'absent' AND continuous_absence_since IS NOT NULL AND continuous_absence_since <= now() - $1::interval
			""", connection);
		command.Parameters.AddWithValue(threshold);

		return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<ComponentWriteOutcome> PurgeRetiredAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"DELETE FROM components WHERE id = $1 AND lifecycle = 'retired'", connection);
		command.Parameters.AddWithValue(componentId);

		int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		return affected > 0 ? ComponentWriteOutcome.Ok : ComponentWriteOutcome.NotFound;
	}

	public async Task<IReadOnlyList<ComponentObservation>> ListObservationsAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			SELECT id, component_id, source, observed_fact, outcome, observed_at
			FROM component_observations
			WHERE component_id = $1
			ORDER BY observed_at DESC
			""", connection);
		command.Parameters.AddWithValue(componentId);

		List<ComponentObservation> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(new ComponentObservation(
				reader.GetGuid(0),
				reader.GetGuid(1),
				reader.GetString(2),
				DeserializeFact(reader.GetString(3)) ?? new ComponentFact(string.Empty, reader.GetFieldValue<DateTimeOffset>(5), null),
				reader.GetString(4),
				reader.GetFieldValue<DateTimeOffset>(5)));
		}

		return items;
	}

	private static Component Map(NpgsqlDataReader reader)
	{
		return new Component(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.IsDBNull(2) ? null : reader.GetGuid(2),
			reader.IsDBNull(3) ? null : reader.GetGuid(3),
			reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetString(5),
			reader.GetString(6),
			reader.GetString(7),
			reader.IsDBNull(8) ? null : DeserializeFact(reader.GetString(8)),
			reader.IsDBNull(9) ? null : DeserializeFact(reader.GetString(9)),
			reader.GetBoolean(10),
			reader.GetFieldValue<DateTimeOffset>(11),
			reader.GetFieldValue<DateTimeOffset>(12),
			reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
			reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
			reader.GetFieldValue<DateTimeOffset>(15),
			reader.GetFieldValue<DateTimeOffset>(16));
	}

	private static ComponentFact? DeserializeFact(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("exact_version", out JsonElement versionElement))
		{
			return null;
		}

		string exactVersion = versionElement.GetString() ?? string.Empty;
		DateTimeOffset observedAt = root.TryGetProperty("observed_at", out JsonElement observedAtElement) && observedAtElement.TryGetDateTimeOffset(out DateTimeOffset parsed)
			? parsed
			: DateTimeOffset.UtcNow;
		string? rawEvidenceReference = root.TryGetProperty("raw_evidence_reference", out JsonElement evidenceElement) ? evidenceElement.GetString() : null;

		return new ComponentFact(exactVersion, observedAt, rawEvidenceReference);
	}
}
