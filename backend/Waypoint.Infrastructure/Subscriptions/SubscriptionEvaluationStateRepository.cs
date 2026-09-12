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

using System.Text.Json;
using Npgsql;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Infrastructure.Subscriptions;

/// <inheritdoc cref="ISubscriptionEvaluationStateRepository"/>
public sealed class SubscriptionEvaluationStateRepository : ISubscriptionEvaluationStateRepository
{
	private const string ProjectionSql = """
		SELECT subscription_id, last_seen_lib_version_counter, last_evaluated_at, last_fetch_set_count, last_projected_bytes
		FROM subscription_evaluation_state
		""";

	private readonly string _connectionString;

	public SubscriptionEvaluationStateRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<SubscriptionEvaluationState?> GetAsync(Guid subscriptionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE subscription_id = $1", connection);
		command.Parameters.AddWithValue(subscriptionId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>
	/// Upserts by <see cref="SubscriptionEvaluationState.SubscriptionId"/>. The fetch
	/// set itself (issue #1472's "expose the fetch-set/projected-bytes result") is
	/// written separately, via <see cref="SetFetchSetAsync"/> -- the fetch set is
	/// transient consumer data (<c>SubscriptionEvaluationFanOutService</c>'s read),
	/// while the fields here are the durable "when did we last look, what did the
	/// lib.json counter say" facts every evaluation needs regardless of whether it
	/// produced a fetch set.
	/// </summary>
	public async Task UpsertAsync(SubscriptionEvaluationState state, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state);

		const string sql = """
			INSERT INTO subscription_evaluation_state
				(subscription_id, last_seen_lib_version_counter, last_evaluated_at, last_fetch_set_count, last_projected_bytes)
			VALUES ($1, $2, $3, $4, $5)
			ON CONFLICT (subscription_id) DO UPDATE SET
				last_seen_lib_version_counter = EXCLUDED.last_seen_lib_version_counter,
				last_evaluated_at = EXCLUDED.last_evaluated_at,
				last_fetch_set_count = EXCLUDED.last_fetch_set_count,
				last_projected_bytes = EXCLUDED.last_projected_bytes
			""";

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(sql, connection);
		command.Parameters.AddWithValue(state.SubscriptionId);
		command.Parameters.AddWithValue((object?)state.LastSeenLibVersionCounter ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)state.LastEvaluatedAt ?? DBNull.Value);
		command.Parameters.AddWithValue(state.LastFetchSetCount);
		command.Parameters.AddWithValue((object?)state.LastProjectedBytes ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task SetFetchSetAsync(Guid subscriptionId, IReadOnlyList<SubscriptionFetchItem> fetchSet, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fetchSet);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE subscription_evaluation_state SET fetch_set_json = $2, fanned_out_at = NULL WHERE subscription_id = $1", connection);
		command.Parameters.AddWithValue(subscriptionId);
		command.Parameters.AddWithValue(JsonSerializer.Serialize(fetchSet));
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<SubscriptionFetchItem>?> GetPendingFetchSetAsync(Guid subscriptionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT fetch_set_json FROM subscription_evaluation_state WHERE subscription_id = $1 AND fanned_out_at IS NULL", connection);
		command.Parameters.AddWithValue(subscriptionId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		if (result is not string json)
		{
			return null;
		}

		List<SubscriptionFetchItem>? items = JsonSerializer.Deserialize<List<SubscriptionFetchItem>>(json);
		return items is { Count: > 0 } ? items : null;
	}

	/// <inheritdoc/>
	public async Task MarkFannedOutAsync(Guid subscriptionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE subscription_evaluation_state SET fanned_out_at = now() WHERE subscription_id = $1", connection);
		command.Parameters.AddWithValue(subscriptionId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SubscriptionEvaluationState Map(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.IsDBNull(1) ? null : reader.GetInt64(1),
		reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
		reader.GetInt32(3),
		reader.IsDBNull(4) ? null : reader.GetInt64(4));
}
