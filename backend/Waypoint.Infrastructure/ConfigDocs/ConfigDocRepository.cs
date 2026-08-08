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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Pagination;

namespace Waypoint.Infrastructure.ConfigDocs;

/// <summary>
/// Storage for the three-layer config-doc store (issue #265, epic #13/#22): plain
/// Npgsql, no ORM -- the same convention <see cref="Waypoint.Infrastructure.Sites.SiteRepository"/>
/// establishes for this layer. Versions are append-only: <see cref="SaveVersionAsync"/>
/// always INSERTs a new <c>config_versions</c> row and repoints
/// <c>config_docs.current_version</c>; nothing in this type ever UPDATEs or DELETEs an
/// existing version row. No resolve/merge logic here -- that is #266, the second slice.
/// </summary>
public sealed class ConfigDocRepository
{
	private const string DocProjectionSql = """
		SELECT id, kind, profile, layer_type, layer_ref, current_version, created_at, updated_at
		FROM config_docs
		""";

	private readonly string _connectionString;

	public ConfigDocRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// Lists config-docs, optionally filtered by kind/profile/layer
	/// (docs/api-contract.md `/config-docs`: "Filter by kind, profile, layer").
	/// </summary>
	public async Task<(IReadOnlyList<ConfigDoc> Items, long TotalCount)> ListAsync(
		string? kind, string? profile, string? layerType, Guid? layerRef, PageRequest page, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		List<string> predicates = [];
		List<object> parameters = [];
		AddPredicate(predicates, parameters, "kind", kind);
		AddPredicate(predicates, parameters, "profile", profile);
		AddPredicate(predicates, parameters, "layer_type", layerType);
		if (layerRef.HasValue)
		{
			predicates.Add($"layer_ref = ${parameters.Count + 1}");
			parameters.Add(layerRef.Value);
		}

		string whereClause = predicates.Count > 0 ? $" WHERE {string.Join(" AND ", predicates)}" : string.Empty;

		long total;
		await using (NpgsqlCommand countCommand = new($"SELECT count(*) FROM config_docs{whereClause}", connection))
		{
			foreach (object parameter in parameters)
			{
				countCommand.Parameters.AddWithValue(parameter);
			}

			total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<ConfigDoc> items = [];
		int limitIndex = parameters.Count + 1;
		string listSql = $"{DocProjectionSql}{whereClause} ORDER BY profile, kind LIMIT ${limitIndex} OFFSET ${limitIndex + 1}";
		await using (NpgsqlCommand listCommand = new(listSql, connection))
		{
			foreach (object parameter in parameters)
			{
				listCommand.Parameters.AddWithValue(parameter);
			}

			listCommand.Parameters.AddWithValue(page.Limit);
			listCommand.Parameters.AddWithValue(page.Offset);
			await using NpgsqlDataReader reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(MapDoc(reader));
			}
		}

		return (items, total);
	}

	/// <summary>
	/// The doc-with-latest-version at a single (kind, profile, layer) slot, or null when no
	/// doc exists there -- the building block <c>GET /config-docs/resolve</c> (issue #266)
	/// uses up to three times per kind (global, the target's site, the target) to gather the
	/// candidates <see cref="Waypoint.Core.ConfigDocs.ConfigDocResolver"/> picks from.
	/// Degrades to null rather than throwing when a matching doc row has no versions yet
	/// (the #270 orphan-row edge case) -- resolution simply treats that slot as absent
	/// instead of surfacing the orphan as a 500.
	/// </summary>
	public async Task<ConfigDocWithLatestVersion?> FindWithLatestVersionAsync(
		string kind, string profile, string layerType, Guid? layerRef, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		Guid? docId = await FindDocIdAsync(connection, kind, profile, layerType, layerRef, cancellationToken).ConfigureAwait(false);
		if (!docId.HasValue)
		{
			return null;
		}

		ConfigDoc? doc = await GetAsync(docId.Value, cancellationToken).ConfigureAwait(false);
		ConfigDocVersion? latest = await GetLatestVersionAsync(docId.Value, cancellationToken).ConfigureAwait(false);
		return doc is null || latest is null ? null : new ConfigDocWithLatestVersion(doc, latest);
	}

	public async Task<ConfigDoc?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{DocProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapDoc(reader) : null;
	}

	/// <summary>The version at <paramref name="version"/>, or null if the doc or that version does not exist.</summary>
	public async Task<ConfigDocVersion?> GetVersionAsync(Guid docId, int version, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT id, doc_id, version, author, body_yaml, created_at FROM config_versions WHERE doc_id = $1 AND version = $2", connection);
		command.Parameters.AddWithValue(docId);
		command.Parameters.AddWithValue(version);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapVersion(reader) : null;
	}

	/// <summary>The latest version's body -- what backs GET /config-docs/{id}.</summary>
	public async Task<ConfigDocVersion?> GetLatestVersionAsync(Guid docId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, doc_id, version, author, body_yaml, created_at
			FROM config_versions
			WHERE doc_id = $1
			ORDER BY version DESC
			LIMIT 1
			""", connection);
		command.Parameters.AddWithValue(docId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapVersion(reader) : null;
	}

	/// <summary>Full version history, newest first -- the auditor answer (docs/api-contract.md `/config-docs/{id}/versions`).</summary>
	public async Task<IReadOnlyList<ConfigDocVersion>> ListVersionsAsync(Guid docId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, doc_id, version, author, body_yaml, created_at
			FROM config_versions
			WHERE doc_id = $1
			ORDER BY version DESC
			""", connection);
		command.Parameters.AddWithValue(docId);

		List<ConfigDocVersion> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(MapVersion(reader));
		}

		return items;
	}

	/// <summary>
	/// Writes a new version at the (kind, profile, layer) slot identified by
	/// <paramref name="docId"/> (an existing doc id) -- see the two-argument overload for
	/// the "identify by slot, create the doc row if new" entry point PUT /config-docs/{id}
	/// and POST /config-docs both need. Never mutates an existing version row: this always
	/// INSERTs <c>current_version + 1</c> and advances the pointer in the same transaction.
	/// </summary>
	public async Task<ConfigDocVersion?> SaveVersionAsync(Guid docId, string author, string bodyYaml, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(author);
		ArgumentException.ThrowIfNullOrWhiteSpace(bodyYaml);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Locks the doc row for the duration of the transaction so two concurrent saves
		// against the same slot cannot both read the same current_version and race to
		// insert the same next version number (config_versions_doc_id_version_key would
		// catch it, but FOR UPDATE avoids the wasted round trip and retry).
		int? nextVersion;
		await using (NpgsqlCommand lockCommand = new("SELECT current_version FROM config_docs WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockCommand.Parameters.AddWithValue(docId);
			object? current = await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (current is null)
			{
				return null;
			}

			nextVersion = (int)current + 1;
		}

		Guid versionId;
		DateTimeOffset createdAt;
		await using (NpgsqlCommand insertCommand = new(
			"""
			INSERT INTO config_versions (doc_id, version, author, body_yaml)
			VALUES ($1, $2, $3, $4)
			RETURNING id, created_at
			""", connection, transaction))
		{
			insertCommand.Parameters.AddWithValue(docId);
			insertCommand.Parameters.AddWithValue(nextVersion.Value);
			insertCommand.Parameters.AddWithValue(author);
			insertCommand.Parameters.AddWithValue(bodyYaml);
			await using NpgsqlDataReader reader = await insertCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			versionId = reader.GetGuid(0);
			createdAt = reader.GetFieldValue<DateTimeOffset>(1);
		}

		await using (NpgsqlCommand updatePointer = new("UPDATE config_docs SET current_version = $2 WHERE id = $1", connection, transaction))
		{
			updatePointer.Parameters.AddWithValue(docId);
			updatePointer.Parameters.AddWithValue(nextVersion.Value);
			await updatePointer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

		return new ConfigDocVersion(versionId, docId, nextVersion.Value, author, bodyYaml, createdAt);
	}

	/// <summary>
	/// Writes the first (or next) version at the (kind, profile, layer) slot, creating the
	/// <c>config_docs</c> identity row on first use -- this is what backs
	/// <c>PUT /config-docs/{id}</c> per docs/api-contract.md: "PUT creates a new immutable
	/// version"; there is no separate POST-to-create-the-slot step in the documented
	/// contract, so the first PUT to a not-yet-existing slot both creates the doc and
	/// writes @v1. <paramref name="layerType"/>/<paramref name="layerRef"/> are validated by
	/// the caller (the API layer checks the referenced site/target exists) before this runs.
	/// </summary>
	public async Task<(ConfigDocSaveOutcome Outcome, ConfigDoc? Doc, ConfigDocVersion? Version)> SaveAsync(
		Guid id, string kind, string profile, string layerType, Guid? layerRef, string author, string bodyYaml, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(kind);
		ArgumentException.ThrowIfNullOrWhiteSpace(profile);
		ArgumentException.ThrowIfNullOrWhiteSpace(layerType);
		ArgumentException.ThrowIfNullOrWhiteSpace(author);
		ArgumentException.ThrowIfNullOrWhiteSpace(bodyYaml);

		if (!ConfigDocKinds.All.Contains(kind))
		{
			return (ConfigDocSaveOutcome.InvalidKind, null, null);
		}

		if (!ConfigDocLayers.All.Contains(layerType) || (layerType == ConfigDocLayers.Global) != (layerRef is null))
		{
			return (ConfigDocSaveOutcome.InvalidLayer, null, null);
		}

		Guid docId = await FindOrCreateDocAsync(id, kind, profile, layerType, layerRef, cancellationToken).ConfigureAwait(false);

		ConfigDocVersion version = (await SaveVersionAsync(docId, author, bodyYaml, cancellationToken).ConfigureAwait(false))!;
		ConfigDoc doc = (await GetAsync(docId, cancellationToken).ConfigureAwait(false))!;
		return (ConfigDocSaveOutcome.Ok, doc, version);
	}

	/// <summary>
	/// Finds the existing (kind, profile, layer) doc row or creates it at
	/// <paramref name="id"/> -- the client-supplied id from <c>PUT /config-docs/{id}</c>,
	/// so the resource a first-time PUT creates is the one the caller addressed, not a
	/// server-generated id the caller would have no way to discover. The find-then-insert
	/// is not fully race-free by itself (two concurrent first-saves at the same brand-new
	/// slot could both miss the SELECT), so a losing INSERT's unique-violation is caught and
	/// resolved by re-reading -- the loser ends up pointed at whichever row won the race
	/// (its own id if it inserted first, otherwise the concurrent caller's), which the
	/// controller's "slot already exists at a different id" check surfaces as a 409.
	/// </summary>
	private async Task<Guid> FindOrCreateDocAsync(Guid id, string kind, string profile, string layerType, Guid? layerRef, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		Guid? existing = await FindDocIdAsync(connection, kind, profile, layerType, layerRef, cancellationToken).ConfigureAwait(false);
		if (existing.HasValue)
		{
			return existing.Value;
		}

		await using NpgsqlCommand insert = new(
			"INSERT INTO config_docs (id, kind, profile, layer_type, layer_ref) VALUES ($1, $2, $3, $4, $5) RETURNING id", connection);
		insert.Parameters.AddWithValue(id);
		insert.Parameters.AddWithValue(kind);
		insert.Parameters.AddWithValue(profile);
		insert.Parameters.AddWithValue(layerType);
		insert.Parameters.AddWithValue((object?)layerRef ?? DBNull.Value);

		try
		{
			return (Guid)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return (await FindDocIdAsync(connection, kind, profile, layerType, layerRef, cancellationToken).ConfigureAwait(false))!.Value;
		}
	}

	private static async Task<Guid?> FindDocIdAsync(
		NpgsqlConnection connection, string kind, string profile, string layerType, Guid? layerRef, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand find = new(
			layerType == ConfigDocLayers.Global
				? "SELECT id FROM config_docs WHERE kind = $1 AND profile = $2 AND layer_type = 'global'"
				: "SELECT id FROM config_docs WHERE kind = $1 AND profile = $2 AND layer_type = $3 AND layer_ref = $4",
			connection);
		find.Parameters.AddWithValue(kind);
		find.Parameters.AddWithValue(profile);
		if (layerType != ConfigDocLayers.Global)
		{
			find.Parameters.AddWithValue(layerType);
			find.Parameters.AddWithValue(layerRef!.Value);
		}

		object? result = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid id ? id : null;
	}

	private static void AddPredicate(List<string> predicates, List<object> parameters, string column, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}

		predicates.Add($"{column} = ${parameters.Count + 1}");
		parameters.Add(value);
	}

	private static ConfigDoc MapDoc(NpgsqlDataReader reader)
	{
		return new ConfigDoc(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.IsDBNull(4) ? null : reader.GetGuid(4),
			reader.GetInt32(5),
			reader.GetFieldValue<DateTimeOffset>(6),
			reader.GetFieldValue<DateTimeOffset>(7));
	}

	private static ConfigDocVersion MapVersion(NpgsqlDataReader reader)
	{
		return new ConfigDocVersion(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.GetInt32(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetFieldValue<DateTimeOffset>(5));
	}
}
