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

/// <inheritdoc cref="IVmToolsIndexRepository"/>
public sealed class VmToolsIndexRepository : IVmToolsIndexRepository
{
	private const string UpsertArtifactSql =
		"""
		INSERT INTO vmtools_artifact_index (
		    id, relative_path, tools_version_raw, tools_version_major, tools_version_minor,
		    tools_version_patch, tools_build, platform, file_type, size_bytes, etag,
		    first_seen_at, last_seen_at, is_latest_alias)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
		ON CONFLICT (relative_path, etag) DO UPDATE SET
		    last_seen_at = EXCLUDED.last_seen_at,
		    size_bytes = EXCLUDED.size_bytes,
		    is_latest_alias = EXCLUDED.is_latest_alias
		""";

	private const string UpsertMappingSql =
		"""
		INSERT INTO vmtools_esx_version_mapping (
		    id, sequence_in_file, esxi_version_dir, esxi_build, tools_version_code,
		    tools_version_raw, tools_version_major, tools_version_minor, tools_version_patch,
		    tools_build, raw_row)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
		ON CONFLICT (esxi_version_dir, tools_version_code) DO UPDATE SET
		    sequence_in_file = EXCLUDED.sequence_in_file,
		    esxi_build = EXCLUDED.esxi_build,
		    tools_version_raw = EXCLUDED.tools_version_raw,
		    tools_version_major = EXCLUDED.tools_version_major,
		    tools_version_minor = EXCLUDED.tools_version_minor,
		    tools_version_patch = EXCLUDED.tools_version_patch,
		    tools_build = EXCLUDED.tools_build,
		    raw_row = EXCLUDED.raw_row
		""";

	private readonly string _connectionString;

	public VmToolsIndexRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task UpsertArtifactsAsync(IReadOnlyCollection<VmToolsArtifact> artifacts, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(artifacts);
		if (artifacts.Count == 0)
		{
			return;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		foreach (VmToolsArtifact artifact in artifacts)
		{
			await using NpgsqlCommand command = new(UpsertArtifactSql, connection, transaction);
			command.Parameters.AddWithValue(artifact.Id);
			command.Parameters.AddWithValue(artifact.RelativePath);
			command.Parameters.AddWithValue((object?)artifact.ToolsVersionRaw ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)artifact.ToolsVersionMajor ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)artifact.ToolsVersionMinor ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)artifact.ToolsVersionPatch ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)artifact.ToolsBuild ?? DBNull.Value);
			command.Parameters.AddWithValue(artifact.Platform);
			command.Parameters.AddWithValue(artifact.FileType);
			command.Parameters.AddWithValue((object?)artifact.SizeBytes ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)artifact.ETag ?? DBNull.Value);
			command.Parameters.AddWithValue(artifact.FirstSeenAt);
			command.Parameters.AddWithValue(artifact.LastSeenAt);
			command.Parameters.AddWithValue(artifact.IsLatestAlias);
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<VmToolsArtifact>> GetArtifactsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, relative_path, tools_version_raw, tools_version_major, tools_version_minor,
			       tools_version_patch, tools_build, platform, file_type, size_bytes, etag,
			       self_hash_sha256, signature_available, first_seen_at, last_seen_at, is_latest_alias
			FROM vmtools_artifact_index
			ORDER BY relative_path
			""", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<VmToolsArtifact> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(new VmToolsArtifact(
				Id: reader.GetGuid(0),
				RelativePath: reader.GetString(1),
				ToolsVersionRaw: reader.IsDBNull(2) ? null : reader.GetString(2),
				ToolsVersionMajor: reader.IsDBNull(3) ? null : reader.GetInt32(3),
				ToolsVersionMinor: reader.IsDBNull(4) ? null : reader.GetInt32(4),
				ToolsVersionPatch: reader.IsDBNull(5) ? null : reader.GetInt32(5),
				ToolsBuild: reader.IsDBNull(6) ? null : reader.GetString(6),
				Platform: reader.GetString(7),
				FileType: reader.GetString(8),
				SizeBytes: reader.IsDBNull(9) ? null : reader.GetInt64(9),
				ETag: reader.IsDBNull(10) ? null : reader.GetString(10),
				SelfHashSha256: reader.IsDBNull(11) ? null : reader.GetString(11),
				SignatureAvailable: reader.IsDBNull(12) ? null : reader.GetBoolean(12),
				FirstSeenAt: reader.GetFieldValue<DateTimeOffset>(13),
				LastSeenAt: reader.GetFieldValue<DateTimeOffset>(14),
				IsLatestAlias: reader.GetBoolean(15)));
		}

		return results;
	}

	public async Task UpsertVersionMappingsAsync(IReadOnlyCollection<VmToolsEsxVersionMapping> mappings, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(mappings);
		if (mappings.Count == 0)
		{
			return;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		foreach (VmToolsEsxVersionMapping mapping in mappings)
		{
			await using NpgsqlCommand command = new(UpsertMappingSql, connection, transaction);
			command.Parameters.AddWithValue(Guid.NewGuid());
			command.Parameters.AddWithValue(mapping.SequenceInFile);
			command.Parameters.AddWithValue(mapping.EsxiVersionDir);
			command.Parameters.AddWithValue((object?)mapping.EsxiBuild ?? DBNull.Value);
			command.Parameters.AddWithValue(mapping.ToolsVersionCode);
			command.Parameters.AddWithValue((object?)mapping.ToolsVersionRaw ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)mapping.ToolsVersionMajor ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)mapping.ToolsVersionMinor ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)mapping.ToolsVersionPatch ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)mapping.ToolsBuild ?? DBNull.Value);
			command.Parameters.AddWithValue(mapping.RawRow);
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<VmToolsEsxVersionMapping>> GetVersionMappingsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, sequence_in_file, esxi_version_dir, esxi_build, tools_version_code,
			       tools_version_raw, tools_version_major, tools_version_minor, tools_version_patch,
			       tools_build, raw_row
			FROM vmtools_esx_version_mapping
			ORDER BY sequence_in_file
			""", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<VmToolsEsxVersionMapping> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(new VmToolsEsxVersionMapping(
				Id: reader.GetGuid(0),
				SequenceInFile: reader.GetInt32(1),
				EsxiVersionDir: reader.GetString(2),
				EsxiBuild: reader.IsDBNull(3) ? null : reader.GetString(3),
				ToolsVersionCode: reader.GetString(4),
				ToolsVersionRaw: reader.IsDBNull(5) ? null : reader.GetString(5),
				ToolsVersionMajor: reader.IsDBNull(6) ? null : reader.GetInt32(6),
				ToolsVersionMinor: reader.IsDBNull(7) ? null : reader.GetInt32(7),
				ToolsVersionPatch: reader.IsDBNull(8) ? null : reader.GetInt32(8),
				ToolsBuild: reader.IsDBNull(9) ? null : reader.GetString(9),
				RawRow: reader.GetString(10)));
		}

		return results;
	}
}
