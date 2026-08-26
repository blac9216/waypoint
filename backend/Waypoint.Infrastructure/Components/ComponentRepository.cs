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

			// Check whether this exact identity already exists (including retired/absent)
			// so we can tell an upsert-of-a-new-row apart from a reconnect for the
			// outcome counters, and so a retired row is genuinely reconnected rather than
			// silently left retired (rediscovery reconnects per ADR-0023).
			Guid? existingId = await FindExistingIdAsync(connection, transaction, targetId, parentComponentId, item.CatalogComponentKey, item.VendorIdentity, cancellationToken).ConfigureAwait(false);
			bool wasNotActive = false;
			if (existingId is not null)
			{
				await using NpgsqlCommand checkLifecycle = new("SELECT lifecycle FROM components WHERE id = $1", connection, transaction);
				checkLifecycle.Parameters.AddWithValue(existingId.Value);
				object? lifecycleResult = await checkLifecycle.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
				wasNotActive = lifecycleResult is string lifecycle && lifecycle != Waypoint.Core.Components.ComponentLifecycleStates.Active;
			}

			Guid rowId;
			if (existingId is { } id)
			{
				await using NpgsqlCommand update = new(
					"""
					UPDATE components
					SET catalog_component_id = COALESCE($2, catalog_component_id),
					    display_name = $3,
					    lifecycle = 'active',
					    discovered_fact = COALESCE($4::jsonb, discovered_fact),
					    fact_conflict = CASE WHEN $4::jsonb IS NULL THEN fact_conflict
					                          ELSE (configured_fact IS NOT NULL AND configured_fact->>'exact_version' <> $4::jsonb->>'exact_version')
					                     END,
					    last_seen_at = now(),
					    continuous_absence_since = NULL
					WHERE id = $1
					RETURNING id
					""", connection, transaction);
				update.Parameters.AddWithValue(id);
				update.Parameters.AddWithValue((object?)item.CatalogComponentId ?? DBNull.Value);
				update.Parameters.AddWithValue(item.DisplayName);
				update.Parameters.AddWithValue((object?)factJson ?? DBNull.Value);
				rowId = (Guid)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
			}
			else
			{
				await using NpgsqlCommand insert = new(
					"""
					INSERT INTO components (parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
					                         vendor_identity, display_name, lifecycle, discovered_fact, last_seen_at)
					VALUES ($1, $2, $3, $4, $5, $6, 'active', $7::jsonb, now())
					RETURNING id
					""", connection, transaction);
				insert.Parameters.AddWithValue(targetId);
				insert.Parameters.AddWithValue((object?)parentComponentId ?? DBNull.Value);
				insert.Parameters.AddWithValue((object?)item.CatalogComponentId ?? DBNull.Value);
				insert.Parameters.AddWithValue(item.CatalogComponentKey);
				insert.Parameters.AddWithValue((object?)item.VendorIdentity ?? DBNull.Value);
				insert.Parameters.AddWithValue(item.DisplayName);
				insert.Parameters.AddWithValue((object?)factJson ?? DBNull.Value);
				rowId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
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

			if (existingId is null)
			{
				upserted++;
			}
			else if (wasNotActive)
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

	private static async Task<Guid?> FindExistingIdAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, Guid? parentComponentId, string catalogComponentKey, string? vendorIdentity, CancellationToken cancellationToken)
	{
		if (vendorIdentity is not null)
		{
			await using NpgsqlCommand command = new(
				"SELECT id FROM components WHERE parent_target_id = $1 AND catalog_component_key = $2 AND vendor_identity = $3",
				connection, transaction);
			command.Parameters.AddWithValue(targetId);
			command.Parameters.AddWithValue(catalogComponentKey);
			command.Parameters.AddWithValue(vendorIdentity);
			return (Guid?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		await using NpgsqlCommand noVendorCommand = new(
			"""
			SELECT id FROM components
			WHERE parent_target_id = $1
			  AND COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid) = COALESCE($2, '00000000-0000-0000-0000-000000000000'::uuid)
			  AND catalog_component_key = $3
			  AND vendor_identity IS NULL
			""", connection, transaction);
		noVendorCommand.Parameters.AddWithValue(targetId);
		noVendorCommand.Parameters.AddWithValue((object?)parentComponentId ?? DBNull.Value);
		noVendorCommand.Parameters.AddWithValue(catalogComponentKey);
		return (Guid?)await noVendorCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
