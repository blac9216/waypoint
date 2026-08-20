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
using Waypoint.Core.Scheduling;

namespace Waypoint.Infrastructure.Scheduling;

/// <inheritdoc cref="IScheduleRepository"/>
public sealed class ScheduleRepository : IScheduleRepository
{
	private const string ProjectionSql = """
		SELECT id, name, job_type, cron_expression, scope::text, credential_id, enabled, paused_reason,
		       next_run_at, last_run_at, last_run_id, last_result, created_by, created_at, updated_at
		FROM schedules
		""";

	private readonly string _connectionString;

	public ScheduleRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY name", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Schedule> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<Schedule?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<Guid?> CreateAsync(
		string name, string jobType, string cronExpression, string scopeJson, Guid? credentialId,
		DateTimeOffset nextRunAt, string createdBy, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO schedules (name, job_type, cron_expression, scope, credential_id, next_run_at, created_by)
			VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7)
			ON CONFLICT (name) DO NOTHING
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(jobType);
		command.Parameters.AddWithValue(cronExpression);
		command.Parameters.AddWithValue(scopeJson);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		command.Parameters.AddWithValue(nextRunAt);
		command.Parameters.AddWithValue(createdBy);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid id ? id : null;
	}

	public async Task<ScheduleWriteOutcome> UpdateAsync(
		Guid id, string? name, string? cronExpression, string? scopeJson, Guid? credentialId, bool clearCredential,
		bool? enabled, DateTimeOffset? nextRunAt, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE schedules SET
				name = COALESCE($2, name),
				cron_expression = COALESCE($3, cron_expression),
				scope = COALESCE($4::jsonb, scope),
				credential_id = CASE WHEN $6 THEN NULL ELSE COALESCE($5, credential_id) END,
				enabled = COALESCE($7, enabled),
				next_run_at = COALESCE($8, next_run_at)
			WHERE id = $1
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)cronExpression ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)scopeJson ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		command.Parameters.AddWithValue(clearCredential);
		command.Parameters.AddWithValue((object?)enabled ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)nextRunAt ?? DBNull.Value);

		try
		{
			return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
				? ScheduleWriteOutcome.Ok
				: ScheduleWriteOutcome.NotFound;
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			return ScheduleWriteOutcome.NameTaken;
		}
	}

	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM schedules WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null;
	}

	public async Task<IReadOnlyList<Schedule>> ListDueAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"""
			{ProjectionSql}
			WHERE enabled AND paused_reason IS NULL AND next_run_at IS NOT NULL AND next_run_at <= $1
			ORDER BY next_run_at
			""", connection);
		command.Parameters.AddWithValue(asOf);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Schedule> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task MarkDispatchedAsync(Guid id, DateTimeOffset nextRunAt, Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE schedules SET next_run_at = $2, last_run_at = now(), last_run_id = $3
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(nextRunAt);
		command.Parameters.AddWithValue(runId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetPausedReasonAsync(Guid id, string? reason, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE schedules SET paused_reason = $2 WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetLastResultAsync(Guid id, string result, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE schedules SET last_result = $2 WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(result);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static Schedule Map(NpgsqlDataReader reader)
	{
		return new Schedule(
			Id: reader.GetGuid(0),
			Name: reader.GetString(1),
			JobType: reader.GetString(2),
			CronExpression: reader.GetString(3),
			ScopeJson: reader.GetString(4),
			CredentialId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
			Enabled: reader.GetBoolean(6),
			PausedReason: reader.IsDBNull(7) ? null : reader.GetString(7),
			NextRunAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
			LastRunAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
			LastRunId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
			LastResult: reader.IsDBNull(11) ? null : reader.GetString(11),
			CreatedBy: reader.GetString(12),
			CreatedAt: reader.GetFieldValue<DateTimeOffset>(13),
			UpdatedAt: reader.GetFieldValue<DateTimeOffset>(14));
	}
}
