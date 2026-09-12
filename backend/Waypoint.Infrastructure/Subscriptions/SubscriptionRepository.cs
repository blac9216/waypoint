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
using Waypoint.Core.Subscriptions;

namespace Waypoint.Infrastructure.Subscriptions;

/// <inheritdoc cref="ISubscriptionRepository"/>
public sealed class SubscriptionRepository : ISubscriptionRepository
{
	private const string ProjectionSql = """
		SELECT id, product, lane, line_granularity, anchor_version, preset_id,
		       refresh_window_days, retention_override_days, is_enabled, created_at, updated_at
		FROM subscriptions
		""";

	private readonly string _connectionString;

	public SubscriptionRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> CreateAsync(Subscription subscription, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(subscription);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO subscriptions (
			    id, product, lane, line_granularity, anchor_version, preset_id,
			    refresh_window_days, retention_override_days, is_enabled)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
			RETURNING id
			""", connection);
		Guid id = subscription.Id == Guid.Empty ? Guid.NewGuid() : subscription.Id;
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(subscription.Product);
		command.Parameters.AddWithValue(subscription.Lane);
		command.Parameters.AddWithValue(SubscriptionLineGranularityValues.ToDbValue(subscription.LineGranularity));
		command.Parameters.AddWithValue(subscription.AnchorVersion);
		command.Parameters.AddWithValue((object?)subscription.PresetId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)subscription.RefreshWindowDays ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)subscription.RetentionOverrideDays ?? DBNull.Value);
		command.Parameters.AddWithValue(subscription.IsEnabled);

		return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}

	public async Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(subscription);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE subscriptions
			SET product = $2, lane = $3, line_granularity = $4, anchor_version = $5,
			    refresh_window_days = $6, retention_override_days = $7, is_enabled = $8
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(subscription.Id);
		command.Parameters.AddWithValue(subscription.Product);
		command.Parameters.AddWithValue(subscription.Lane);
		command.Parameters.AddWithValue(SubscriptionLineGranularityValues.ToDbValue(subscription.LineGranularity));
		command.Parameters.AddWithValue(subscription.AnchorVersion);
		command.Parameters.AddWithValue((object?)subscription.RefreshWindowDays ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)subscription.RetentionOverrideDays ?? DBNull.Value);
		command.Parameters.AddWithValue(subscription.IsEnabled);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<Subscription>> ListAllAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY created_at", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Subscription> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}
		return results;
	}

	public async Task<IReadOnlyList<Subscription>> ListEnabledByLaneAsync(string lane, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(lane);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"{ProjectionSql} WHERE lane = $1 AND is_enabled = true ORDER BY created_at", connection);
		command.Parameters.AddWithValue(lane);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Subscription> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}
		return results;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM subscriptions WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static Subscription Map(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		Product: reader.GetString(1),
		Lane: reader.GetString(2),
		LineGranularity: SubscriptionLineGranularityValues.FromDbValue(reader.GetString(3)),
		AnchorVersion: reader.GetString(4),
		PresetId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
		RefreshWindowDays: reader.IsDBNull(6) ? null : reader.GetInt32(6),
		RetentionOverrideDays: reader.IsDBNull(7) ? null : reader.GetInt32(7),
		IsEnabled: reader.GetBoolean(8),
		CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(10));
}
