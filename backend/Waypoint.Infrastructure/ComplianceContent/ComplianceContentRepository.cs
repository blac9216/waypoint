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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IComplianceContentRepository"/>
public sealed class ComplianceContentRepository : IComplianceContentRepository
{
	private const string ConfigProjectionSql = """
		SELECT repository_url, ref_type, ref_value, pulled_commit, pulled_by, pulled_at, created_at, updated_at
		FROM compliance_content
		WHERE id = 1
		""";

	private readonly string _connectionString;

	public ComplianceContentRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return await GetConfigAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<ComplianceContentConfig?> GetConfigAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(ConfigProjectionSql, connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return Map(reader);
	}

	public async Task<ComplianceContentConfig> PutConfigAsync(string repositoryUrl, string refType, string refValue, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO compliance_content (id, repository_url, ref_type, ref_value)
			VALUES (1, $1, $2, $3)
			ON CONFLICT (id) DO UPDATE SET
				repository_url = EXCLUDED.repository_url,
				ref_type = EXCLUDED.ref_type,
				ref_value = EXCLUDED.ref_value
			""", connection);
		command.Parameters.AddWithValue(repositoryUrl);
		command.Parameters.AddWithValue(refType);
		command.Parameters.AddWithValue(refValue);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		return (await GetConfigAsync(connection, cancellationToken).ConfigureAwait(false))!;
	}

	public async Task RecordPullAsync(
		Guid? jobId, string refType, string refValue, string? commit, string status, string? note, string? initiatedBy, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO compliance_content_pulls (job_id, ref_type, ref_value, commit, status, note, initiated_by)
			VALUES ($1, $2, $3, $4, $5, $6, $7)
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
			insert.Parameters.AddWithValue(refType);
			insert.Parameters.AddWithValue(refValue);
			insert.Parameters.AddWithValue((object?)commit ?? DBNull.Value);
			insert.Parameters.AddWithValue(status);
			insert.Parameters.AddWithValue((object?)note ?? DBNull.Value);
			insert.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);
			await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (status == ComplianceContentPullStatuses.Succeeded)
		{
			await using NpgsqlCommand update = new(
				"""
				UPDATE compliance_content
				SET pulled_commit = $1, pulled_by = $2, pulled_at = now()
				WHERE id = 1
				""", connection, transaction);
			update.Parameters.AddWithValue((object?)commit ?? DBNull.Value);
			update.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);
			await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<ComplianceContentPull>> ListPullsAsync(int limit, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, job_id, ref_type, ref_value, commit, status, note, initiated_by, created_at
			FROM compliance_content_pulls
			ORDER BY created_at DESC
			LIMIT $1
			""", connection);
		command.Parameters.AddWithValue(limit);

		List<ComplianceContentPull> pulls = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			pulls.Add(new ComplianceContentPull(
				reader.GetGuid(0),
				reader.IsDBNull(1) ? null : reader.GetGuid(1),
				reader.GetString(2),
				reader.GetString(3),
				reader.IsDBNull(4) ? null : reader.GetString(4),
				reader.GetString(5),
				reader.IsDBNull(6) ? null : reader.GetString(6),
				reader.IsDBNull(7) ? null : reader.GetString(7),
				reader.GetFieldValue<DateTimeOffset>(8)));
		}

		return pulls;
	}

	private static ComplianceContentConfig Map(NpgsqlDataReader reader) => new(
		reader.GetString(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.IsDBNull(3) ? null : reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4),
		reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
		reader.GetFieldValue<DateTimeOffset>(6),
		reader.GetFieldValue<DateTimeOffset>(7));
}
