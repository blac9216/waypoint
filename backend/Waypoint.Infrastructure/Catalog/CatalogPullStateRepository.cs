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

namespace Waypoint.Infrastructure.Catalog;

/// <inheritdoc cref="ICatalogPullStateRepository"/>
public sealed class CatalogPullStateRepository : ICatalogPullStateRepository
{
	private const string SelectSql =
		"""
		SELECT last_attempt_at, last_outcome, last_failure_reason, last_success_at,
		       last_success_item_count, updated_at
		FROM catalog_pull_state
		WHERE id = 1
		""";

	private readonly string _connectionString;

	public CatalogPullStateRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<CatalogPullState?> GetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(SelectSql, connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task RecordSuccessAsync(int itemCount, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE catalog_pull_state
			SET last_attempt_at = now(),
			    last_outcome = $1,
			    last_failure_reason = NULL,
			    last_success_at = now(),
			    last_success_item_count = $2
			WHERE id = 1
			""", connection);
		command.Parameters.AddWithValue(CatalogPullOutcomes.Succeeded);
		command.Parameters.AddWithValue(itemCount);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task RecordFailureAsync(bool isAuthFailure, string failureReason, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE catalog_pull_state
			SET last_attempt_at = now(),
			    last_outcome = $1,
			    last_failure_reason = $2
			WHERE id = 1
			""", connection);
		command.Parameters.AddWithValue(isAuthFailure ? CatalogPullOutcomes.AuthFailed : CatalogPullOutcomes.Failed);
		command.Parameters.AddWithValue(failureReason);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static CatalogPullState Map(NpgsqlDataReader reader) => new(
		LastAttemptAt: reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
		LastOutcome: reader.IsDBNull(1) ? null : reader.GetString(1),
		LastFailureReason: reader.IsDBNull(2) ? null : reader.GetString(2),
		LastSuccessAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
		LastSuccessItemCount: reader.IsDBNull(4) ? null : reader.GetInt32(4),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(5));
}
