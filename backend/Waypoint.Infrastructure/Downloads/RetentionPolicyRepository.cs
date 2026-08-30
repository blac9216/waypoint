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

/// <inheritdoc cref="IRetentionPolicyRepository"/>
public sealed class RetentionPolicyRepository : IRetentionPolicyRepository
{
	private const string ProjectionSql = """
		SELECT id, scope_key, grace_period_days, grace_max_refreshes,
		       manual_download_dial_default, created_at, updated_at
		FROM download_retention_policies
		""";

	private readonly string _connectionString;

	public RetentionPolicyRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> UpsertAsync(
		string scopeKey,
		int gracePeriodDays,
		int graceMaxRefreshes,
		string manualDownloadDialDefault,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(manualDownloadDialDefault);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO download_retention_policies (scope_key, grace_period_days, grace_max_refreshes, manual_download_dial_default)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (scope_key) DO UPDATE SET
				grace_period_days = EXCLUDED.grace_period_days,
				grace_max_refreshes = EXCLUDED.grace_max_refreshes,
				manual_download_dial_default = EXCLUDED.manual_download_dial_default
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(scopeKey);
		command.Parameters.AddWithValue(gracePeriodDays);
		command.Parameters.AddWithValue(graceMaxRefreshes);
		command.Parameters.AddWithValue(manualDownloadDialDefault);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
	}

	public async Task<RetentionPolicy?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<RetentionPolicy?> GetByScopeKeyAsync(string scopeKey, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE scope_key = $1", connection);
		command.Parameters.AddWithValue(scopeKey);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<RetentionPolicy>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY scope_key", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<RetentionPolicy> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}
		return items;
	}

	private static RetentionPolicy Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetInt32(2),
		reader.GetInt32(3),
		reader.GetString(4),
		reader.GetFieldValue<DateTimeOffset>(5),
		reader.GetFieldValue<DateTimeOffset>(6));
}
