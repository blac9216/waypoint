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
using Waypoint.Core.ComplianceContent;
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
	private readonly ICatalogRepository _catalog;

	/// <summary>
	/// Issue #1000: <paramref name="catalog"/> is required so <see cref="SetConfiguredFactAsync"/>
	/// can resolve catalog linkage from the Admin-configured exact version the same way
	/// <see cref="Waypoint.Infrastructure.Execution.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync"/>
	/// already does for the discovered fact -- both now call the same
	/// <see cref="Waypoint.Core.Components.CatalogLinkageResolver"/>.
	/// </summary>
	public ComponentRepository(string connectionString, ICatalogRepository catalog)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(catalog);
		_connectionString = connectionString;
		_catalog = catalog;
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
		Guid targetId, IReadOnlyList<DiscoveredComponent> items, CancellationToken cancellationToken, bool advanceAbsence = true)
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
			//
			// Issue #985: catalog_component_id is deliberately NOT COALESCE-preserved
			// like discovered_fact/display_name above -- DiscoverJobHandler now
			// re-resolves catalog linkage from scratch every discovery pass and always
			// supplies its current answer (a real id, or null when unlinked/ambiguous
			// this pass), so a straight EXCLUDED assignment is required: a version
			// change that newly fails to link (or that now resolves to a different
			// catalog component) must overwrite a stale id rather than have COALESCE
			// preserve it forever once first set.
			//
			// Issue #1000: that "always overwrite" rule is only honest when discovery
			// actually rendered a version opinion this pass (item.ExactVersion is
			// non-null -- today, only esxi hosts). A component discovery can never
			// version (the synthetic vcenter root, any vm) always reports
			// ExactVersion=null/CatalogComponentId=null by construction
			// (DiscoverJobHandler.MapToComponents), so an unconditional EXCLUDED
			// assignment would clobber a link the CONFIGURED-fact path
			// (ComponentRepository.SetConfiguredFactAsync) may have independently
			// established -- exactly the permanent-catalog_incompatible bug #1000
			// reports. The CASE below preserves the existing catalog_component_id
			// whenever this pass has no discovered exact version to relink/unlink from;
			// when discovery DOES have a version this pass, #985's original
			// always-overwrite semantics (relink or honestly unlink on a version
			// change) are unchanged.
			bool discoveryHasVersionOpinion = factJson is not null;
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
					    catalog_component_id = CASE WHEN $8 THEN EXCLUDED.catalog_component_id ELSE components.catalog_component_id END,
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
				upsert.Parameters.AddWithValue(discoveryHasVersionOpinion);

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
					        catalog_component_id = CASE WHEN $7 THEN EXCLUDED.catalog_component_id ELSE components.catalog_component_id END,
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
				upsert.Parameters.AddWithValue(discoveryHasVersionOpinion);

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
		//
		// Issue #865: skipped entirely when advanceAbsence is false -- a partial
		// discovery boundary (some subtree failed to enumerate) upserts what it DID see
		// above (unverified-cache refresh) but must not read its incomplete view as
		// proof that anything else is gone (ADR-0023 "neither claims completeness nor
		// advances absence").
		int markedAbsent = 0;
		if (advanceAbsence)
		{
			await using NpgsqlCommand loadCandidates = new(
				"SELECT id, vendor_identity, catalog_component_key, parent_component_id FROM components WHERE parent_target_id = $1 AND lifecycle <> 'retired'",
				connection, transaction);
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

	/// <summary>
	/// Admin configured-fact write. <paramref name="exactVersion"/> null/whitespace
	/// CLEARS the configured fact (issue #1000 AC "clearing the configured version must
	/// honestly unlink") -- the caller (<see cref="Waypoint.Api.Controllers.ComponentsController.Put"/>)
	/// is responsible for rejecting an empty body outright; this method's own null
	/// handling exists for that clear path specifically, not as another way to send an
	/// empty string through.
	///
	/// Issue #1000: this now also resolves <c>catalog_component_id</c> the same way
	/// <see cref="Waypoint.Infrastructure.Execution.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync"/>
	/// resolves it for the discovered fact -- both call the shared
	/// <see cref="Waypoint.Core.Components.CatalogLinkageResolver"/>, never a forked
	/// copy of the exact-match/ambiguity rule. Runs here, at write time, rather than
	/// only at matcher time: PUT is this fact's ONLY writer (unlike discovery, which
	/// re-runs on a schedule and so gets #985's "re-evaluate every pass" self-healing
	/// for free), so resolving once at write time is the only point that will ever
	/// evaluate a call that never repeats -- deferring to a read-time/matcher-time
	/// resolution would leave a component that is never rediscovered permanently
	/// unlinked even after an Admin sets a now-matching configured version.
	///
	/// Precedence when a discovered fact also exists (ADR-0023: two independent
	/// provenances, "[Waypoint] ... never guesses a winner"), mirroring exactly the
	/// effective-version rule <see cref="Waypoint.Core.Components.ComponentCapabilityMatcher"/>
	/// already applies for the FACT (this is that same rule, reused for the LINK): if
	/// discovered and configured agree, the shared value drives linkage; if they
	/// disagree (<c>fact_conflict</c>), linkage resolves to unlinked -- not a guess at
	/// which provenance wins, and consistent with the matcher's own hard fail on
	/// <c>FactConflict</c> regardless of any link value; if only the configured fact is
	/// present (the vCenter root, VMs -- discovery structurally never supplies one),
	/// the configured value alone drives linkage, which is this issue's actual fix.
	/// </summary>
	public async Task<ComponentWriteOutcome> SetConfiguredFactAsync(Guid componentId, string? exactVersion, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		bool clearing = string.IsNullOrWhiteSpace(exactVersion);
		string? factJson = clearing
			? null
			: JsonSerializer.Serialize(new { exact_version = exactVersion, observed_at = DateTimeOffset.UtcNow }, FactSerializerOptions);

		// Read the row's identity key and current discovered fact BEFORE this write --
		// needed to compute the effective version for linkage and to know whether this
		// write disagrees with an existing discovered fact.
		await using NpgsqlCommand select = new(
			"SELECT catalog_component_key, discovered_fact->>'exact_version' FROM components WHERE id = $1 FOR UPDATE",
			connection, transaction);
		select.Parameters.AddWithValue(componentId);
		string? catalogComponentKey = null;
		string? discoveredExactVersion = null;
		bool found = false;
		await using (NpgsqlDataReader existing = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await existing.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				found = true;
				catalogComponentKey = existing.GetString(0);
				discoveredExactVersion = existing.IsDBNull(1) ? null : existing.GetString(1);
			}
		}

		if (!found)
		{
			// The reader above is fully disposed by the `await using` block's own
			// closing brace before this runs -- rolling back while it was still open
			// (the original bug here) throws NpgsqlOperationInProgressException, since
			// Npgsql allows only one in-flight command per connection at a time.
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return ComponentWriteOutcome.NotFound;
		}

		bool conflict = !clearing && discoveredExactVersion is not null
			&& !string.Equals(discoveredExactVersion, exactVersion, StringComparison.Ordinal);

		// Same effective-version rule as ComponentCapabilityMatcher.ResolveExactVersion:
		// both present and agreeing -> that value; both present and conflicting -> no
		// effective version (unlinked); only one present -> that one; neither -> none.
		string? effectiveVersion = conflict
			? null
			: (clearing ? null : exactVersion) ?? discoveredExactVersion;

		// An ambiguous-match warning has no PUT-time delivery channel analogous to
		// DiscoverJobHandler's job.log event (there is no in-flight job here) --
		// resolving to unlinked is still the correct, honest outcome (ADR-0022 "never
		// guesses a winner"), and GET /components/{id}/capability already reports "not
		// linked to a known catalog component" for it, same as the routine zero-match
		// case. Discarded rather than invented a new delivery path out of scope for
		// this issue.
		(Guid? catalogComponentId, string? _) = await CatalogLinkageResolver
			.ResolveAsync(_catalog, catalogComponentKey!, effectiveVersion, cancellationToken)
			.ConfigureAwait(false);

		await using NpgsqlCommand update = new(
			"""
			UPDATE components
			SET configured_fact = $2::jsonb,
			    fact_conflict = $3,
			    catalog_component_id = $4
			WHERE id = $1
			""", connection, transaction);
		update.Parameters.AddWithValue(componentId);
		update.Parameters.AddWithValue((object?)factJson ?? DBNull.Value);
		update.Parameters.AddWithValue(conflict);
		update.Parameters.AddWithValue((object?)catalogComponentId ?? DBNull.Value);
		await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		if (factJson is not null)
		{
			await using NpgsqlCommand observation = new(
				"""
				INSERT INTO component_observations (component_id, source, observed_fact, outcome)
				VALUES ($1, 'configured', $2::jsonb, $3)
				""", connection, transaction);
			observation.Parameters.AddWithValue(componentId);
			observation.Parameters.AddWithValue(factJson);
			observation.Parameters.AddWithValue(conflict ? "conflict" : "recorded");
			await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

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
