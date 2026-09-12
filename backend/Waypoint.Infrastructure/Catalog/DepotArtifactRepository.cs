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
using Waypoint.Core.Pagination;

namespace Waypoint.Infrastructure.Catalog;

/// <inheritdoc cref="IDepotArtifactRepository"/>
public sealed class DepotArtifactRepository : IDepotArtifactRepository
{
	private const string ProjectionSql = """
		SELECT id, relative_path, sha256, status, product, version, metadata::text, indexed_at, updated_at, size_bytes, last_verified_at, bundle_id
		FROM depot_artifacts
		""";

	private readonly string _connectionString;

	public DepotArtifactRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// <c>relative_path</c> is the table's unique idempotency key (migration 0100,
	/// issue #1488 -- renamed in place from <c>external_id</c>; migration 0001
	/// originally established the same unique-key idiom), so re-syncing the same
	/// artifact is one <c>INSERT ... ON CONFLICT DO UPDATE</c> -- the newer payload
	/// always wins, matching issue #193's acceptance criterion. <c>indexed_at</c> is
	/// deliberately left alone on conflict (it records when the artifact first
	/// entered the catalog); <c>updated_at</c> advances via the existing
	/// <c>trg_depot_artifacts_updated_at</c> trigger. <c>last_verified_at</c> is
	/// deliberately NOT written here -- deciding when a row counts as freshly
	/// verified is presence-sweep behavior (#1503/#1512), out of this slice's scope.
	/// <c>sha256</c>/<c>size_bytes</c> use <c>COALESCE(EXCLUDED.x, depot_artifacts.x)</c>
	/// rather than an unconditional overwrite: <c>DownloadJobHandler</c>'s
	/// present/failed upserts (issue #1593 review, finding 1) carry the artifact's
	/// existing <c>Sha256</c> forward but have no size to report, so a plain
	/// <c>EXCLUDED.size_bytes</c> would silently null out the size the catalog
	/// write path (<see cref="VendorProductVersionCatalogParser"/>/
	/// <c>CatalogIndexJobHandler</c>) had just recorded. A caller that does
	/// know a new, smaller size (e.g. a corrected catalog re-index) still wins,
	/// because <c>COALESCE</c> only falls back when the incoming value is null, not
	/// when it is present but different. <c>bundle_id</c> (migration 0132, issue
	/// #1783) uses the identical <c>COALESCE(EXCLUDED.bundle_id, ...)</c> pattern: a
	/// present/failed verification upsert (<c>DownloadJobHandler</c>/
	/// <c>BinariesDownloadJobHandler</c>) never carries a bundle id and must not null
	/// out one a prior connected pull already recorded. Bound on the resulting
	/// staleness: if the vendor reassigns/retires a bundle id between catalog pulls,
	/// this COALESCE keeps the row's PRIOR bundle_id until the next connected pull
	/// re-syncs it (a present/failed verification upsert never supplies a fresher one
	/// to overwrite it with) -- that stale id is never silently accepted as still valid,
	/// though: <c>BinariesDownloadTool.TryDetectEmptySelectionFailure</c> (issue #1783)
	/// reports the real tool's own "0 elements" empty-selection table as an honest,
	/// actionable failure naming the id, so a job queued against a stale bundle_id
	/// fails loudly rather than resolving nothing and reporting silent success.
	/// </summary>
	public async Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifact.RelativePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Status);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO depot_artifacts (relative_path, sha256, status, metadata, size_bytes, bundle_id)
			VALUES ($1, $2, $3, $4::jsonb, $5, $6)
			ON CONFLICT (relative_path) DO UPDATE SET
				sha256 = COALESCE(EXCLUDED.sha256, depot_artifacts.sha256),
				status = EXCLUDED.status,
				metadata = EXCLUDED.metadata,
				size_bytes = COALESCE(EXCLUDED.size_bytes, depot_artifacts.size_bytes),
				bundle_id = COALESCE(EXCLUDED.bundle_id, depot_artifacts.bundle_id)
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(artifact.RelativePath);
		command.Parameters.AddWithValue((object?)artifact.Sha256 ?? DBNull.Value);
		command.Parameters.AddWithValue(artifact.Status);
		command.Parameters.AddWithValue(artifact.MetadataJson ?? "{}");
		command.Parameters.AddWithValue((object?)artifact.SizeBytes ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)artifact.BundleId ?? DBNull.Value);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
	}

	/// <summary>
	/// The <c>NOT EXISTS</c> guards below read a statement-start snapshot under
	/// Postgres's default READ COMMITTED isolation, not a locked/rechecked read: if a
	/// presence sweep commits a row at a TO identity concurrently with the rename
	/// statement, the two can race, and that statement can still attempt (and fail
	/// with a unique-key violation on <c>relative_path</c>) a rename onto an identity
	/// that exists by the time it commits. This is distinct from the collision case
	/// this method now reconciles (the ordinary, non-concurrent case where the sweep
	/// already created the TO row BEFORE this pull started -- verified in
	/// <see cref="DepotArtifactRepositoryTests"/>). The narrower concurrent-commit
	/// race is timing-dependent, unobserved in production, and left unfixed (a
	/// <c>FOR UPDATE</c>/advisory-lock closes it but adds contention to every rekey
	/// for a window that has never been hit) -- recorded here so a future
	/// unhandled-exception report from this call site is not a surprise.
	/// </summary>
	/// <inheritdoc/>
	public async Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(renames);
		if (renames.Count == 0)
		{
			return 0;
		}

		// Issue #1852: the per-artifact RekeyAsync(string, string) this method replaced
		// began with ArgumentException.ThrowIfNullOrWhiteSpace on both arguments; the
		// batched shape validated only the dictionary itself, letting a null/empty/
		// whitespace key or value flow unvalidated into the text[] parameters below.
		// Restore the same per-identity check the single-artifact path always had.
		HashSet<string> seenToIdentities = new(StringComparer.Ordinal);
		foreach (KeyValuePair<string, string> pair in renames)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
			ArgumentException.ThrowIfNullOrWhiteSpace(pair.Value);

			// Repository-boundary invariant, kept deliberately as defence in depth:
			// two different FROM identities renaming onto the same TO identity would
			// violate depot_artifacts_relative_path_key's real UNIQUE constraint at
			// the SQL layer in an unpredictable way (whichever UNNEST row the rename
			// statement processes first wins, silently), so refuse it explicitly.
			//
			// It is NOT where issue #1852's bare-fileName gap is fixed, and it cannot
			// be: the sole caller today (CatalogPullJobHandler.BuildLegacyRenames)
			// DERIVES each key from its value as the value's trailing path segment,
			// so two distinct keys can never share a value and this branch is
			// unreachable from that call site by construction. The shape a violated
			// bare-fileName-uniqueness invariant actually produces there is a KEY
			// collision, and BuildLegacyRenames enforces it at that point. This check
			// stays because it costs one HashSet over a dictionary the method already
			// walks for the null/whitespace checks, and because RekeyManyAsync is a
			// public repository API whose next caller may build its map some other
			// way -- but it is a backstop, not the enforcement #1852 asked for.
			if (!seenToIdentities.Add(pair.Value))
			{
				throw new ArgumentException(
					$"RekeyManyAsync's renames must not map two different FROM identities onto the same TO identity ('{pair.Value}').",
					nameof(renames));
			}
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Step 1, the one query "up front": which of the candidate FROM identities
		// actually have a non-superseded row today? Almost always none once a
		// stack's first post-#1784 pull has run -- the per-artifact guard this
		// batching replaced fired on every artifact regardless of whether a legacy
		// row existed (#1818). Zero matches here means zero further round trips.
		string[] fromCandidates = [.. renames.Keys];
		List<string> legacyFrom = [];
		await using (NpgsqlCommand probe = new(
			"SELECT relative_path FROM depot_artifacts WHERE relative_path = ANY($1) AND superseded_at IS NULL", connection))
		{
			probe.Parameters.AddWithValue(fromCandidates);
			await using NpgsqlDataReader reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				legacyFrom.Add(reader.GetString(0));
			}
		}

		if (legacyFrom.Count == 0)
		{
			return 0;
		}

		string[] legacyFromArray = [.. legacyFrom];
		string[] legacyToArray = [.. legacyFrom.Select(from => renames[from])];

		// Step 2: rename every legacy row onto its TO identity in one statement,
		// except where the TO identity already has a row (issue #1804's collision
		// case -- step 3 below reconciles those). Deliberately unfiltered by
		// superseded_at (issue #1851): unlike step 1 and step 3's legacy join, this
		// guard exists to avoid a real depot_artifacts_relative_path_key UNIQUE
		// violation, and that constraint applies to EVERY row regardless of
		// superseded_at -- a superseded row still permanently occupies its
		// relative_path (migration 0134: never cleared, never deleted), so ANY row
		// at the TO identity, superseded or not, must block the rename here or the
		// UPDATE below would fail outright. Step 3's target filter below is the
		// half of this pair that issue #1851 actually needed: without it, this
		// case fell through to a merge into an already-superseded target.
		int renamed;
		await using (NpgsqlCommand rename = new(
			"""
			WITH pairs (from_path, to_path) AS (SELECT * FROM UNNEST($1::text[], $2::text[]))
			UPDATE depot_artifacts d
			SET relative_path = p.to_path
			FROM pairs p
			WHERE d.relative_path = p.from_path
			  AND NOT EXISTS (SELECT 1 FROM depot_artifacts t WHERE t.relative_path = p.to_path)
			""", connection))
		{
			rename.Parameters.AddWithValue(legacyFromArray);
			rename.Parameters.AddWithValue(legacyToArray);
			renamed = await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (renamed == legacyFrom.Count)
		{
			return renamed;
		}

		// Step 3, issue #1804's collision case: for whichever pairs step 2 could not
		// rename (their FROM row is still present, unchanged), fold the legacy row's
		// still-valid facts into the surviving TO row -- COALESCE-only, never
		// clobbering a fact the TO row already has, the same convention
		// UpsertAsync's own ON CONFLICT clause uses -- then mark the legacy row
		// superseded (migration 0134) rather than deleting it: IDepotArtifactRepository
		// has no Delete/Remove/Purge-named member (design #16 section 2's
		// never-auto-remove policy, ReviewListServiceTests.
		// Interface_HasNoDeleteOrRemoveOrPurgeMethod).
		//
		// Issue #1851: target.superseded_at IS NULL is the fix. Without it, a TO
		// identity occupied by an already-superseded row (verified unreachable
		// today -- superseded_at is only ever set on a bare-fileName FROM identity,
		// never on a "PROD/COMP/<product>/<fileName>" TO identity -- but not
		// guarded against) would still match here as "target", folding the legacy
		// row's facts into a row nothing lists and then superseding the legacy row
		// too: two superseded rows, zero visible, permanently. With the filter,
		// that pair matches neither step 2 (blocked by the real UNIQUE constraint
		// on relative_path, which does not exempt superseded rows) nor step 3
		// (blocked by this filter) -- the legacy row is left untouched rather than
		// corrupted, and the next pull's step 1 finds it again and retries.
		int superseded;
		await using (NpgsqlCommand merge = new(
			"""
			WITH pairs (from_path, to_path) AS (SELECT * FROM UNNEST($1::text[], $2::text[])),
			merged AS (
				UPDATE depot_artifacts target
				SET sha256 = COALESCE(target.sha256, legacy.sha256),
				    size_bytes = COALESCE(target.size_bytes, legacy.size_bytes),
				    last_verified_at = COALESCE(target.last_verified_at, legacy.last_verified_at)
				FROM pairs p
				JOIN depot_artifacts legacy ON legacy.relative_path = p.from_path AND legacy.superseded_at IS NULL
				WHERE target.relative_path = p.to_path AND target.superseded_at IS NULL
				RETURNING legacy.id AS legacy_id
			)
			UPDATE depot_artifacts d
			SET superseded_at = now()
			FROM merged m
			WHERE d.id = m.legacy_id
			""", connection))
		{
			merge.Parameters.AddWithValue(legacyFromArray);
			merge.Parameters.AddWithValue(legacyToArray);
			superseded = await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		return renamed + superseded;
	}

	/// <inheritdoc/>
	public async Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<DepotArtifact>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(ids);
		if (ids.Count == 0)
		{
			return [];
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = ANY($1)", connection);
		command.Parameters.AddWithValue(ids.ToArray());

		List<DepotArtifact> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}

		return results;
	}

	public async Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(
		DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(filter);
		ArgumentNullException.ThrowIfNull(page);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		string whereClause = BuildWhereClause(filter, out List<object> parameters);

		long total;
		await using (NpgsqlCommand countCommand = new($"SELECT count(*) FROM depot_artifacts{whereClause}", connection))
		{
			AddParameters(countCommand, parameters);
			total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<DepotArtifact> items = [];
		string listSql = $"{ProjectionSql}{whereClause} ORDER BY indexed_at DESC, id LIMIT ${parameters.Count + 1} OFFSET ${parameters.Count + 2}";
		await using (NpgsqlCommand listCommand = new(listSql, connection))
		{
			AddParameters(listCommand, parameters);
			listCommand.Parameters.AddWithValue(page.Limit);
			listCommand.Parameters.AddWithValue(page.Offset);

			await using NpgsqlDataReader reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(Map(reader));
			}
		}

		return (items, total);
	}

	/// <summary>
	/// Builds a <c>WHERE</c> clause (or the empty string) from whichever filters are
	/// set, and returns the matching parameter values in order -- kept separate from
	/// the two callers (count, list) so the same predicate always backs the count
	/// used for <c>X-Total-Count</c> and the rows actually returned.
	/// </summary>
	private static string BuildWhereClause(DepotArtifactFilter filter, out List<object> parameters)
	{
		parameters = [];
		// Issue #1804: a superseded legacy row (RekeyManyAsync's collision path) is
		// never a delete -- but every read surface that lists artifacts must not
		// show it as a duplicate of the row it was folded into. Unconditional, not a
		// DepotArtifactFilter option: no caller has ever needed to list superseded
		// rows, and GetByIdAsync (an existing-FK-reference lookup, not a listing
		// surface) deliberately does not apply this filter.
		List<string> clauses = ["superseded_at IS NULL"];

		if (!string.IsNullOrWhiteSpace(filter.Product))
		{
			parameters.Add(filter.Product);
			clauses.Add($"product = ${parameters.Count}");
		}

		if (!string.IsNullOrWhiteSpace(filter.Version))
		{
			parameters.Add(filter.Version);
			clauses.Add($"version = ${parameters.Count}");
		}

		if (!string.IsNullOrWhiteSpace(filter.Status))
		{
			parameters.Add(filter.Status);
			clauses.Add($"status = ${parameters.Count}");
		}

		return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
	}

	private static void AddParameters(NpgsqlCommand command, List<object> parameters)
	{
		foreach (object value in parameters)
		{
			command.Parameters.AddWithValue(value);
		}
	}

	private static DepotArtifact Map(NpgsqlDataReader reader)
	{
		return new DepotArtifact(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.IsDBNull(2) ? null : reader.GetString(2),
			reader.GetString(3),
			reader.IsDBNull(4) ? null : reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetString(5),
			reader.GetString(6),
			reader.GetFieldValue<DateTimeOffset>(7),
			reader.GetFieldValue<DateTimeOffset>(8),
			reader.IsDBNull(9) ? null : reader.GetInt64(9),
			reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
			reader.IsDBNull(11) ? null : reader.GetString(11));
	}
}
