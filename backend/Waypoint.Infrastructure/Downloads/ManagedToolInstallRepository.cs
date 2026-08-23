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

/// <inheritdoc cref="IManagedToolInstallRepository"/>
public sealed class ManagedToolInstallRepository : IManagedToolInstallRepository
{
	private const string ProjectionSql = """
		SELECT id, source, source_path, version, sha256, outcome, rejected_reason, initiated_by, job_id, created_at
		FROM managed_tool_installs
		""";

	private readonly string _connectionString;

	public ManagedToolInstallRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> RecordAsync(ManagedToolInstallAttempt attempt, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(attempt);
		ArgumentException.ThrowIfNullOrWhiteSpace(attempt.Source);
		ArgumentException.ThrowIfNullOrWhiteSpace(attempt.SourcePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(attempt.Outcome);
		ArgumentException.ThrowIfNullOrWhiteSpace(attempt.InitiatedBy);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO managed_tool_installs (source, source_path, version, sha256, outcome, rejected_reason, initiated_by, job_id)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(attempt.Source);
		command.Parameters.AddWithValue(attempt.SourcePath);
		command.Parameters.AddWithValue((object?)attempt.Version ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)attempt.Sha256 ?? DBNull.Value);
		command.Parameters.AddWithValue(attempt.Outcome);
		command.Parameters.AddWithValue((object?)attempt.RejectedReason ?? DBNull.Value);
		command.Parameters.AddWithValue(attempt.InitiatedBy);
		command.Parameters.AddWithValue((object?)attempt.JobId ?? DBNull.Value);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
	}

	public async Task<IReadOnlyList<ManagedToolInstall>> ListAsync(int limit, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY created_at DESC LIMIT $1", connection);
		command.Parameters.AddWithValue(limit);

		List<ManagedToolInstall> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<ManagedToolInstall?> GetCurrentAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"{ProjectionSql} WHERE outcome = 'installed' ORDER BY created_at DESC LIMIT 1", connection);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	private static ManagedToolInstall Map(NpgsqlDataReader reader)
	{
		return new ManagedToolInstall(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.IsDBNull(3) ? null : reader.GetString(3),
			reader.IsDBNull(4) ? null : reader.GetString(4),
			reader.GetString(5),
			reader.IsDBNull(6) ? null : reader.GetString(6),
			reader.GetString(7),
			reader.IsDBNull(8) ? null : reader.GetGuid(8),
			reader.GetFieldValue<DateTimeOffset>(9));
	}
}
