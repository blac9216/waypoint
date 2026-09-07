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
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IVksLibraryIndexRepository"/>
public sealed class VksLibraryIndexRepository : IVksLibraryIndexRepository
{
	private const string UpsertItemSql =
		"""
		INSERT INTO vks_library_items (
		    id, name, source, item_uuid, distro, distro_version, arch, k8s_version,
		    vmware_build, fips, release_line, line_build, ob_build_id, naming_era,
		    parse_status, etag, sha256, size_bytes, created_upstream, discovered_at, last_seen_at)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
		ON CONFLICT (source, name) DO UPDATE SET
		    item_uuid = EXCLUDED.item_uuid,
		    distro = EXCLUDED.distro,
		    distro_version = EXCLUDED.distro_version,
		    arch = EXCLUDED.arch,
		    k8s_version = EXCLUDED.k8s_version,
		    vmware_build = EXCLUDED.vmware_build,
		    fips = EXCLUDED.fips,
		    release_line = EXCLUDED.release_line,
		    line_build = EXCLUDED.line_build,
		    ob_build_id = EXCLUDED.ob_build_id,
		    naming_era = EXCLUDED.naming_era,
		    parse_status = EXCLUDED.parse_status,
		    etag = EXCLUDED.etag,
		    sha256 = EXCLUDED.sha256,
		    size_bytes = EXCLUDED.size_bytes,
		    created_upstream = EXCLUDED.created_upstream,
		    last_seen_at = EXCLUDED.last_seen_at
		""";

	private readonly string _connectionString;

	public VksLibraryIndexRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task UpsertItemsAsync(IReadOnlyCollection<VksLibraryItem> items, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(items);
		if (items.Count == 0)
		{
			return;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		foreach (VksLibraryItem item in items)
		{
			await using NpgsqlCommand command = new(UpsertItemSql, connection, transaction);
			command.Parameters.AddWithValue(item.Id);
			command.Parameters.AddWithValue(item.Name);
			command.Parameters.AddWithValue(item.Source);
			command.Parameters.AddWithValue((object?)item.ItemUuid ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.Distro ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.DistroVersion ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.Arch ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.K8sVersion ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.VmwareBuild ?? DBNull.Value);
			command.Parameters.AddWithValue(item.Dimensions.Fips);
			command.Parameters.AddWithValue((object?)item.Dimensions.ReleaseLine ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.LineBuild ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Dimensions.ObBuildId ?? DBNull.Value);
			command.Parameters.AddWithValue(item.NamingEra);
			command.Parameters.AddWithValue(item.ParseStatus);
			command.Parameters.AddWithValue((object?)item.Etag?.Value ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.Sha256 ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.SizeBytes ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)item.CreatedUpstream ?? DBNull.Value);
			command.Parameters.AddWithValue(item.DiscoveredAt);
			command.Parameters.AddWithValue(item.LastSeenAt);
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<VksLibraryItem>> GetItemsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, name, source, item_uuid, distro, distro_version, arch, k8s_version,
			       vmware_build, fips, release_line, line_build, ob_build_id, naming_era,
			       parse_status, etag, sha256, size_bytes, created_upstream, discovered_at, last_seen_at
			FROM vks_library_items
			ORDER BY name
			""", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<VksLibraryItem> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			VksItemDimensions dimensions = new(
				Distro: reader.IsDBNull(4) ? null : reader.GetString(4),
				DistroVersion: reader.IsDBNull(5) ? null : reader.GetString(5),
				Arch: reader.IsDBNull(6) ? null : reader.GetString(6),
				K8sVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
				VmwareBuild: reader.IsDBNull(8) ? null : reader.GetString(8),
				Fips: reader.GetBoolean(9),
				ReleaseLine: reader.IsDBNull(10) ? null : reader.GetString(10),
				LineBuild: reader.IsDBNull(11) ? null : reader.GetString(11),
				ObBuildId: reader.IsDBNull(12) ? null : reader.GetString(12));

			results.Add(new VksLibraryItem(
				Id: reader.GetGuid(0),
				Name: reader.GetString(1),
				Source: reader.GetString(2),
				ItemUuid: reader.IsDBNull(3) ? null : reader.GetString(3),
				Dimensions: dimensions,
				NamingEra: reader.GetString(13),
				ParseStatus: reader.GetString(14),
				Etag: reader.IsDBNull(15) ? null : new VksChangeToken(reader.GetString(15)),
				Sha256: reader.IsDBNull(16) ? null : reader.GetString(16),
				SizeBytes: reader.IsDBNull(17) ? null : reader.GetInt64(17),
				CreatedUpstream: reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
				DiscoveredAt: reader.GetFieldValue<DateTimeOffset>(19),
				LastSeenAt: reader.GetFieldValue<DateTimeOffset>(20)));
		}

		return results;
	}
}
