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
using Waypoint.Core.Downloads.Photon;

namespace Waypoint.Infrastructure.Downloads.Photon;

/// <inheritdoc cref="IPhotonIndexRepository"/>
public sealed class PhotonIndexRepository : IPhotonIndexRepository
{
	private const string ProjectionSql = """
		SELECT id, version, variant, arch, base_url, has_repodata, repomd_revision,
		       package_count, discovered_at, last_seen_at
		FROM photon_repo_index
		""";

	private readonly string _connectionString;

	public PhotonIndexRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// <c>INSERT ... ON CONFLICT (version, variant, arch) DO UPDATE</c> -- re-discovery
	/// of an unchanged upstream repo touches only <c>last_seen_at</c> and the mutable
	/// fields, never inserting a duplicate row and never disturbing the original
	/// <c>discovered_at</c> (only set by Postgres's column DEFAULT on the first insert).
	/// </summary>
	public async Task UpsertRepoIndexEntryAsync(PhotonRepoIndexEntry entry, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentException.ThrowIfNullOrWhiteSpace(entry.Version);
		ArgumentException.ThrowIfNullOrWhiteSpace(entry.Variant);
		ArgumentException.ThrowIfNullOrWhiteSpace(entry.Arch);
		ArgumentException.ThrowIfNullOrWhiteSpace(entry.BaseUrl);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO photon_repo_index (version, variant, arch, base_url, has_repodata, repomd_revision, package_count)
			VALUES ($1, $2, $3, $4, $5, $6, $7)
			ON CONFLICT (version, variant, arch) DO UPDATE SET
				base_url = EXCLUDED.base_url,
				has_repodata = EXCLUDED.has_repodata,
				repomd_revision = EXCLUDED.repomd_revision,
				package_count = EXCLUDED.package_count,
				last_seen_at = now()
			""", connection);
		command.Parameters.AddWithValue(entry.Version);
		command.Parameters.AddWithValue(entry.Variant);
		command.Parameters.AddWithValue(entry.Arch);
		command.Parameters.AddWithValue(entry.BaseUrl);
		command.Parameters.AddWithValue(entry.HasRepodata);
		command.Parameters.AddWithValue((object?)entry.RepomdRevision ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)entry.PackageCount ?? DBNull.Value);

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<PhotonRepoIndexEntry>> ListRepoIndexEntriesAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY version, variant, arch", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<PhotonRepoIndexEntry> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}
		return items;
	}

	private static PhotonRepoIndexEntry Map(NpgsqlDataReader reader) => new(
		reader.GetString(1),
		reader.GetString(2),
		reader.GetString(3),
		reader.GetString(4),
		reader.GetBoolean(5),
		reader.IsDBNull(6) ? null : reader.GetString(6),
		reader.IsDBNull(7) ? null : reader.GetInt32(7))
	{
		Id = reader.GetGuid(0),
		DiscoveredAt = reader.GetFieldValue<DateTimeOffset>(8),
		LastSeenAt = reader.GetFieldValue<DateTimeOffset>(9),
	};
}
