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
		SELECT id, external_id, sha256, status, product, version, metadata::text, indexed_at, updated_at
		FROM depot_artifacts
		""";

	private readonly string _connectionString;

	public DepotArtifactRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// <c>external_id</c> is the table's unique idempotency key (migration 0001), so
	/// re-syncing the same artifact is one <c>INSERT ... ON CONFLICT DO UPDATE</c> --
	/// the newer payload always wins, matching issue #193's acceptance criterion.
	/// <c>indexed_at</c> is deliberately left alone on conflict (it records when the
	/// artifact first entered the catalog); <c>updated_at</c> advances via the
	/// existing <c>trg_depot_artifacts_updated_at</c> trigger.
	/// </summary>
	public async Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ExternalId);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Status);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO depot_artifacts (external_id, sha256, status, metadata)
			VALUES ($1, $2, $3, $4::jsonb)
			ON CONFLICT (external_id) DO UPDATE SET
				sha256 = EXCLUDED.sha256,
				status = EXCLUDED.status,
				metadata = EXCLUDED.metadata
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(artifact.ExternalId);
		command.Parameters.AddWithValue((object?)artifact.Sha256 ?? DBNull.Value);
		command.Parameters.AddWithValue(artifact.Status);
		command.Parameters.AddWithValue(artifact.MetadataJson ?? "{}");

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
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
		List<string> clauses = [];

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
			reader.GetFieldValue<DateTimeOffset>(8));
	}
}
