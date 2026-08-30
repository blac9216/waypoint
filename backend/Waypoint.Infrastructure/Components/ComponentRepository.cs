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

		List<Component> resolved = new(items.Count);
		foreach (Component item in items)
		{
			resolved.Add(await WithLinkedDisplayNameAsync(item, cancellationToken).ConfigureAwait(false));
		}

		return resolved;
	}

	public async Task<Component?> GetAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(componentId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		Component? found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
		return found is null ? null : await WithLinkedDisplayNameAsync(found, cancellationToken).ConfigureAwait(false);
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

			// Issue #1081: build rides alongside exact_version in the same JSONB fact --
			// still only written when this pass actually rendered a version opinion
			// (never a build-only fact), and honestly null when the pass could not
			// observe a build (e.g. a vm, which never carries one at all).
			// Issue #1063: derived_from_parent rides alongside exact_version/build in the
			// same JSONB fact (same additive-field precedent build itself set in #1081) --
			// true only for a VM component whose fact MapToComponents copied from its
			// parent vCenter's own fact, false (the default read back by DeserializeFact)
			// for every directly-observed fact.
			string? factJson = item.ExactVersion is null
				? null
				: JsonSerializer.Serialize(
					new { exact_version = item.ExactVersion, observed_at = DateTimeOffset.UtcNow, build = item.Build, derived_from_parent = item.DerivedFromParent },
					FactSerializerOptions);

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
			// non-null -- esxi hosts, and, since issue #1081, the vcenter root when the
			// pass observed the appliance's own `content.about` version). A component
			// discovery can never version (any vm, or the vcenter root on a pass that
			// could not observe its own identity/version) always reports
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

			// Issue #1063 (round-1 review, blocker 1): both DO UPDATE branches below add
			// ONE narrow exception to the two retain-last-known rules above -- the
			// COALESCE that keeps a prior discovered_fact across a pass with no version
			// opinion, and #1000's CASE that keeps the catalog link with it. A fact
			// carrying `derived_from_parent: true` (only ever a VM's, copied from this
			// same pass's `vcenter` root fact -- see DiscoverJobHandler.MapToComponents)
			// is CLEARED instead when the pass produced no fact for it, rather than
			// retained:
			//   * A directly OBSERVED fact (an esxi host's, the vcenter root's own) was
			//     genuinely measured on that component at least once, so retaining it
			//     across a boundary that could not re-observe it is last-known-good --
			//     #1000's rationale, deliberately UNCHANGED here.
			//   * A DERIVED fact never was. It is a copy of a parent fact, and when the
			//     parent has no fact this pass (issue #1115's exact-name-only Enhanced
			//     Linked Mode session-match miss, or any boundary where the appliance's
			//     `content.about` was unavailable) there is nothing left it is a copy
			//     OF. Retaining it would leave the VM version-present, still stamped
			//     `derived_from_parent: true` -- indistinguishable on the wire from a
			//     fresh derivation, `observed_at` the only tell -- and still catalog
			//     linked, i.e. still selectable and PLANNABLE against a baseline the
			//     current parent version may no longer support. Epic #726 section 3:
			//     absent facts stay honestly absent, and provenance never misrepresents
			//     when a fact was derived.
			// The clear is expressed on the stored row's own flag rather than on
			// anything about this pass's item, so it is exactly as narrow as the
			// provenance flag itself: no observed fact can ever match it. The link is
			// cleared only when no configured_fact exists -- an Admin PUT resolves
			// linkage from its own version (SetConfiguredFactAsync), and #1000's whole
			// point is that a discovery pass with no version opinion must not clobber
			// it. fact_conflict clears with the fact, since a removed discovered fact
			// cannot disagree with anything.

			// Issue #1081 (round-1 review, blocker 1): a ROOT component that was
			// previously discovered WITHOUT an identity and is now discovered WITH one
			// must be ADOPTED in place, not duplicated. The two upsert branches below
			// bind to two DIFFERENT unique constraints
			// (components_vendor_identity_unique vs the null-identity partial index), so
			// once a pass starts supplying an identity for a key it had previously left
			// null -- exactly what #1081 does for the `vcenter` root -- the identity
			// branch's ON CONFLICT simply does not see the pre-existing null-identity
			// row, and BOTH constraints happily permit a second root alongside it. The
			// consequence is not cosmetic: DiscoverJobHandler's declared-service root
			// lookup would then have two candidates and resolve the tie by created_at,
			// picking the older, unlinked, absent one and reporting
			// declared_services_upserted: 0 forever on every target discovered before
			// this shipped.
			//
			// Adoption (an UPDATE that stamps the identity onto the existing row) is
			// chosen over post-hoc dedup or a data migration because it is the only
			// option that preserves the row's whole history: first_seen_at, any
			// Admin-set configured_fact, the catalog_component_id the configured-fact
			// path may have established, every component_observations row, and -- most
			// importantly -- the parent_component_id FKs of children already
			// materialized under it (#741/#743's declared services). A dedup would have
			// to re-point all of those by hand; a migration cannot run at all, because
			// the identity is not knowable at migration time -- it is only learned from
			// the appliance at discovery time.
			//
			// Deliberately scoped to ROOTS (parentComponentId is null). A root is a
			// singleton per (target, catalog_component_key) -- one appliance per target
			// -- so "the null-identity row for this key" is unambiguously the same
			// entity that just reported an identity. Non-root siblings (several esxi
			// hosts under one target) have no such guarantee: an arbitrary identified
			// sibling must not swallow the shared null-identity placeholder, so they
			// keep the previous behaviour untouched.
			//
			// The NOT EXISTS guard keeps this safe on an appliance that already ran a
			// build carrying #1081's identity change WITHOUT this fix and so already has
			// both rows on disk: adoption would violate components_vendor_identity_unique
			// there, so it is skipped and the stale placeholder is retired instead by the
			// statement that runs after the upsert below.
			if (item.VendorIdentity is not null && parentComponentId is null)
			{
				await using NpgsqlCommand adopt = new(
					"""
					UPDATE components SET vendor_identity = $3
					WHERE parent_target_id = $1
					  AND parent_component_id IS NULL
					  AND catalog_component_key = $2
					  AND vendor_identity IS NULL
					  AND NOT EXISTS (
					      SELECT 1 FROM components existing
					      WHERE existing.parent_target_id = $1
					        AND existing.catalog_component_key = $2
					        AND existing.vendor_identity = $3)
					""", connection, transaction);
				adopt.Parameters.AddWithValue(targetId);
				adopt.Parameters.AddWithValue(item.CatalogComponentKey);
				adopt.Parameters.AddWithValue(item.VendorIdentity);
				await adopt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

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
					    catalog_component_id = CASE WHEN $8 THEN EXCLUDED.catalog_component_id
					                                 WHEN components.discovered_fact->>'derived_from_parent' = 'true' AND components.configured_fact IS NULL THEN NULL
					                                 ELSE components.catalog_component_id END,
					    display_name = EXCLUDED.display_name,
					    lifecycle = 'active',
					    discovered_fact = CASE WHEN EXCLUDED.discovered_fact IS NOT NULL THEN EXCLUDED.discovered_fact
					                            WHEN components.discovered_fact->>'derived_from_parent' = 'true' THEN NULL
					                            ELSE components.discovered_fact END,
					    fact_conflict = CASE WHEN EXCLUDED.discovered_fact IS NOT NULL
					                              THEN (components.configured_fact IS NOT NULL AND components.configured_fact->>'exact_version' <> EXCLUDED.discovered_fact->>'exact_version')
					                          WHEN components.discovered_fact->>'derived_from_parent' = 'true' THEN false
					                          ELSE components.fact_conflict
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
					        catalog_component_id = CASE WHEN $7 THEN EXCLUDED.catalog_component_id
					                                     WHEN components.discovered_fact->>'derived_from_parent' = 'true' AND components.configured_fact IS NULL THEN NULL
					                                     ELSE components.catalog_component_id END,
					        display_name = EXCLUDED.display_name,
					        lifecycle = 'active',
					        discovered_fact = CASE WHEN EXCLUDED.discovered_fact IS NOT NULL THEN EXCLUDED.discovered_fact
					                                WHEN components.discovered_fact->>'derived_from_parent' = 'true' THEN NULL
					                                ELSE components.discovered_fact END,
					        fact_conflict = CASE WHEN EXCLUDED.discovered_fact IS NOT NULL
					                                  THEN (components.configured_fact IS NOT NULL AND components.configured_fact->>'exact_version' <> EXCLUDED.discovered_fact->>'exact_version')
					                              WHEN components.discovered_fact->>'derived_from_parent' = 'true' THEN false
					                              ELSE components.fact_conflict
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

			// Issue #1081 (round-1 review, blocker 1), second half: on an appliance that
			// already has BOTH shapes on disk the adoption above is correctly skipped,
			// which would leave the stale null-identity root visible to the
			// declared-service root lookup (which reads includeRetired: true). Retire it
			// so exactly one root remains a live candidate. A no-op in the normal case,
			// because adoption already left no null-identity root behind.
			if (item.VendorIdentity is not null && parentComponentId is null)
			{
				await using NpgsqlCommand retireStale = new(
					"""
					UPDATE components SET lifecycle = 'retired', continuous_absence_since = COALESCE(continuous_absence_since, now())
					WHERE parent_target_id = $1
					  AND parent_component_id IS NULL
					  AND catalog_component_key = $2
					  AND vendor_identity IS NULL
					  AND lifecycle <> 'retired'
					""", connection, transaction);
				retireStale.Parameters.AddWithValue(targetId);
				retireStale.Parameters.AddWithValue(item.CatalogComponentKey);
				await retireStale.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
			// Issue #741: catalog-declared children (vendor_identity IS NULL beneath a
			// parent component -- the named VCSA service rows) are excluded from this
			// sweep. A discovery boundary structurally never enumerates them (no MoRef,
			// no upstream object), so "not reported this pass" is not evidence of
			// absence for them; their lifecycle is owned by
			// SyncCatalogDeclaredChildrenAsync's catalog-vs-linkage reconciliation, which
			// the discovery handler runs right after this upsert.
			await using NpgsqlCommand loadCandidates = new(
				"""
				SELECT id, vendor_identity, catalog_component_key, parent_component_id FROM components
				WHERE parent_target_id = $1 AND lifecycle <> 'retired'
				  AND NOT (vendor_identity IS NULL AND parent_component_id IS NOT NULL)
				""",
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

	public async Task<CatalogDeclaredChildSyncOutcome> SyncCatalogDeclaredChildrenAsync(
		Guid targetId, Guid parentComponentId, IReadOnlyList<CatalogDeclaredChild> declared, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(declared);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		CatalogDeclaredChildSyncOutcome outcome = await SyncCatalogDeclaredChildrenCoreAsync(
			connection, transaction, targetId, parentComponentId, declared, cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return outcome;
	}

	/// <summary>
	/// Issue #741: the shared transactional body of
	/// <see cref="SyncCatalogDeclaredChildrenAsync"/> -- also invoked inside
	/// <see cref="SetConfiguredFactAsync"/>'s own transaction when a root connection
	/// component's linkage changes, so a configured-version write and its declared-child
	/// consequence commit or roll back together. Upserts bind to migration 0054's
	/// <c>idx_components_no_vendor_identity_unique</c> partial index (the exact
	/// no-vendor-identity identity case ADR-0023 defines for catalog-declared services),
	/// same as <see cref="UpsertDiscoveredAsync"/>'s NULL-identity branch. No
	/// <c>discovered_fact</c>/<c>configured_fact</c> is ever written here: a declared
	/// child's version facts are the parent appliance's, inherited at match time
	/// (<see cref="ComponentFactInheritance"/>), never a stored third copy.
	/// </summary>
	private static async Task<CatalogDeclaredChildSyncOutcome> SyncCatalogDeclaredChildrenCoreAsync(
		NpgsqlConnection connection,
		NpgsqlTransaction transaction,
		Guid targetId,
		Guid parentComponentId,
		IReadOnlyList<CatalogDeclaredChild> declared,
		CancellationToken cancellationToken)
	{
		int upserted = 0;
		int reconnected = 0;
		foreach (CatalogDeclaredChild child in declared)
		{
			await using NpgsqlCommand upsert = new(
				"""
				WITH prior AS (
				    SELECT lifecycle FROM components
				    WHERE parent_target_id = $1
				      AND COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid) = $2
				      AND catalog_component_key = $3
				      AND vendor_identity IS NULL
				)
				INSERT INTO components (parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
				                         vendor_identity, display_name, lifecycle, last_seen_at)
				VALUES ($1, $2, $4, $3, NULL, $5, 'active', now())
				ON CONFLICT (parent_target_id, COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid), catalog_component_key)
				    WHERE vendor_identity IS NULL
				    DO UPDATE SET
				        catalog_component_id = EXCLUDED.catalog_component_id,
				        display_name = EXCLUDED.display_name,
				        lifecycle = 'active',
				        last_seen_at = now(),
				        continuous_absence_since = NULL,
				        retired_at = NULL
				RETURNING id, (SELECT lifecycle FROM prior) AS prior_lifecycle
				""", connection, transaction);
			upsert.Parameters.AddWithValue(targetId);
			upsert.Parameters.AddWithValue(parentComponentId);
			upsert.Parameters.AddWithValue(child.CatalogComponentKey);
			upsert.Parameters.AddWithValue(child.CatalogComponentId);
			upsert.Parameters.AddWithValue(child.DisplayName);

			await using NpgsqlDataReader reader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			string? priorLifecycle = reader.IsDBNull(1) ? null : reader.GetString(1);
			if (priorLifecycle is not null && priorLifecycle != Waypoint.Core.Components.ComponentLifecycleStates.Active)
			{
				reconnected++;
			}
			else if (priorLifecycle is null)
			{
				upserted++;
			}
		}

		// A child the catalog no longer declares (or every child, when the parent has
		// lost its catalog link and the caller passed an empty declared set) becomes
		// absent -- retained identity, never deleted, honest omission on the next scope
		// resolution (ADR-0023 lifecycle). Unlike the discovery sweep, retired rows are
		// also left retired here; only the declared upsert above reconnects one.
		int markedAbsent;
		{
			string[] declaredKeys = [.. declared.Select(c => c.CatalogComponentKey)];
			await using NpgsqlCommand markAbsent = new(
				"""
				UPDATE components
				SET lifecycle = 'absent',
				    continuous_absence_since = COALESCE(continuous_absence_since, now())
				WHERE parent_target_id = $1
				  AND parent_component_id = $2
				  AND vendor_identity IS NULL
				  AND lifecycle = 'active'
				  AND NOT (catalog_component_key = ANY($3))
				""", connection, transaction);
			markAbsent.Parameters.AddWithValue(targetId);
			markAbsent.Parameters.AddWithValue(parentComponentId);
			markAbsent.Parameters.AddWithValue(declaredKeys);
			markedAbsent = await markAbsent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		return new CatalogDeclaredChildSyncOutcome(upserted, reconnected, markedAbsent);
	}

	/// <inheritdoc />
	public async Task<Guid?> CreateDeclaredRootAsync(
		Guid targetId, string catalogComponentKey, string displayName, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(catalogComponentKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Issue #743: an Admin-declared root row -- no parent, no vendor identity, no
		// facts, UNLINKED (catalog_component_id null). Identity binds to migration
		// 0054's idx_components_no_vendor_identity_unique partial index, the same
		// no-vendor-identity case the declared-children sync uses one tier down. ON
		// CONFLICT DO NOTHING (never DO UPDATE): declaration is creation-only -- an
		// existing row (any lifecycle) is never mutated, reconnected, or relinked by a
		// second declaration; the null return lets the caller surface an honest 409.
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO components (parent_target_id, parent_component_id, catalog_component_id, catalog_component_key,
			                         vendor_identity, display_name, lifecycle, last_seen_at)
			VALUES ($1, NULL, NULL, $2, NULL, $3, 'active', now())
			ON CONFLICT (parent_target_id, COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid), catalog_component_key)
			    WHERE vendor_identity IS NULL
			    DO NOTHING
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(targetId);
		insert.Parameters.AddWithValue(catalogComponentKey);
		insert.Parameters.AddWithValue(displayName);

		object? inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return inserted is Guid id ? id : null;
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
			"SELECT catalog_component_key, discovered_fact->>'exact_version', parent_target_id, parent_component_id, vendor_identity FROM components WHERE id = $1 FOR UPDATE",
			connection, transaction);
		select.Parameters.AddWithValue(componentId);
		string? catalogComponentKey = null;
		string? discoveredExactVersion = null;
		Guid parentTargetId = Guid.Empty;
		bool isRootConnectionComponent = false;
		bool found = false;
		await using (NpgsqlDataReader existing = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await existing.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				found = true;
				catalogComponentKey = existing.GetString(0);
				discoveredExactVersion = existing.IsDBNull(1) ? null : existing.GetString(1);
				parentTargetId = existing.GetGuid(2);

				// Issue #741: a target's ROOT connection component (no parent) is the
				// anchor for catalog-declared service expansion below. A null vendor
				// identity still identifies most roots -- issue #743's Admin-declared
				// ssh/target roots (Photon, Aria, ...) never carry one, same as a
				// catalog-declared CHILD (also null, but excluded by the
				// parent_component_id check above).
				//
				// Issue #1081: that null-vendor-identity test alone is no longer
				// SUFFICIENT for the vcenter root specifically -- it now carries the
				// appliance's own authoritative vendor identity once discovery observes
				// it, which would otherwise make this false exactly when a
				// discovery-linked vCenter's Admin PUT should still sync its declared
				// VCSA children. CatalogComponentKey == `vcenter` is always true for
				// that root and never for a discovered esxi/vm sibling (which also has
				// parent_component_id null under the flattened-parentage model -- see
				// DiscoverJobHandler.MapToComponents), so it is added as an explicit
				// OR rather than replacing the null-vendor-identity test outright.
				isRootConnectionComponent = existing.IsDBNull(3) &&
					(existing.IsDBNull(4) ||
						string.Equals(catalogComponentKey, Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter, StringComparison.Ordinal));
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

		// Issue #741: a root connection component's linkage change re-derives its
		// catalog-declared service children in the SAME transaction -- the configured
		// vCenter version PUT is exactly how a VCSA appliance's link is established in
		// practice (discovery never observes the vCenter's own version), so this write
		// is the moment the catalog release's declared VCSA service set becomes (or
		// stops being) this appliance's discoverable component list. Linked: the linked
		// catalog component's product version declares the set; unlinked (cleared/
		// conflicted/no match): an empty declared set marks every previously-derived
		// child absent -- honest, never silently retained as scannable.
		if (isRootConnectionComponent)
		{
			IReadOnlyList<CatalogDeclaredChild> declared = [];
			if (catalogComponentId is { } linkedCatalogComponentId)
			{
				CatalogComponent? linkedComponent = await _catalog.GetComponentAsync(linkedCatalogComponentId, cancellationToken).ConfigureAwait(false);
				if (linkedComponent is not null)
				{
					IReadOnlyList<CatalogComponent> versionComponents = await _catalog
						.ListComponentsAsync(linkedComponent.ProductVersionId, cancellationToken).ConfigureAwait(false);
					declared = CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren(versionComponents, linkedCatalogComponentId);
				}
			}

			await SyncCatalogDeclaredChildrenCoreAsync(
				connection, transaction, parentTargetId, componentId, declared, cancellationToken).ConfigureAwait(false);
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

	/// <summary>
	/// Issue #1202: a declared-root row (<see cref="CreateDeclaredRootAsync"/>) is
	/// created UNLINKED with a version-neutral display name (the catalog component
	/// key); once <c>exact_version</c> links it to one exact catalog product version
	/// (this write path or a later <see cref="SetConfiguredFactAsync"/> re-link), the
	/// name a reader sees must match the ACTUAL linked catalog component -- never the
	/// stale name captured at declaration time, and never re-derived by writing
	/// <c>display_name</c> on every link change (this read-time derivation instead
	/// self-heals on every read, including a re-link the write path never explicitly
	/// visits). Scoped to the closed ssh/target declared-root shape ONLY: a vSphere-
	/// discovered host/VM/vCenter's <see cref="Component.DisplayName"/> is the real
	/// vendor-observed name (a hostname, not a catalog-authored label) and must never
	/// be overwritten by the catalog component's own descriptive name.
	/// </summary>
	private async Task<Component> WithLinkedDisplayNameAsync(Component component, CancellationToken cancellationToken)
	{
		if (component.CatalogComponentId is not { } catalogComponentId)
		{
			return component;
		}

		CatalogComponent? linked = await _catalog.GetComponentAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
		if (linked is null
			|| !string.Equals(linked.Transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			|| !string.Equals(linked.SelectorKind, CatalogSelectorKinds.Target, StringComparison.Ordinal))
		{
			return component;
		}

		return component with { DisplayName = linked.DisplayName };
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
		// Issue #1081: optional -- absent entirely on a configured fact (which never
		// carries a build) or on an older discovered fact recorded before this field
		// existed; both read back as honestly null, never a parse failure.
		string? build = root.TryGetProperty("build", out JsonElement buildElement) && buildElement.ValueKind != JsonValueKind.Null
			? buildElement.GetString()
			: null;
		// Issue #1063: optional -- absent entirely on a configured fact (never derived)
		// or on any fact recorded before this field existed; both read back as honestly
		// false (directly observed), never a parse failure.
		bool derivedFromParent = root.TryGetProperty("derived_from_parent", out JsonElement derivedElement)
			&& derivedElement.ValueKind == JsonValueKind.True;

		return new ComponentFact(exactVersion, observedAt, rawEvidenceReference, build, derivedFromParent);
	}
}
