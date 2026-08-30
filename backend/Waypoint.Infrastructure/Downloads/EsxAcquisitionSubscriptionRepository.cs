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
using NpgsqlTypes;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IEsxAcquisitionSubscriptionRepository"/>
public sealed class EsxAcquisitionSubscriptionRepository : IEsxAcquisitionSubscriptionRepository
{
	private const string ProjectionSql = """
		SELECT id, name, selected_platforms, enabled, created_at, updated_at
		FROM esx_acquisition_subscriptions
		""";

	private readonly string _connectionString;

	public EsxAcquisitionSubscriptionRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<EsxAcquisitionSubscription> CreateAsync(
		string name, IReadOnlyList<string> selectedPlatforms, bool enabled, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(selectedPlatforms);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"""
			INSERT INTO esx_acquisition_subscriptions (name, selected_platforms, enabled)
			VALUES ($1, $2, $3)
			RETURNING id, name, selected_platforms, enabled, created_at, updated_at
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(selectedPlatforms.ToArray());
		command.Parameters.AddWithValue(enabled);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return Map(reader);
	}

	public async Task<EsxAcquisitionSubscription?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<EsxAcquisitionSubscription>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY created_at DESC, id", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<EsxAcquisitionSubscription> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<EsxAcquisitionSubscription?> UpdateAsync(
		Guid id,
		string? name,
		IReadOnlyList<string>? selectedPlatforms,
		bool? enabled,
		CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"""
			UPDATE esx_acquisition_subscriptions SET
				name = COALESCE($1, name),
				selected_platforms = COALESCE($2, selected_platforms),
				enabled = COALESCE($3, enabled)
			WHERE id = $4
			RETURNING id, name, selected_platforms, enabled, created_at, updated_at
			""", connection);
		command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = (object?)selectedPlatforms?.ToArray() ?? DBNull.Value,
			NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
		});
		command.Parameters.AddWithValue((object?)enabled ?? DBNull.Value);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	private static EsxAcquisitionSubscription Map(NpgsqlDataReader reader)
	{
		return new EsxAcquisitionSubscription(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetFieldValue<string[]>(2),
			reader.GetBoolean(3),
			reader.GetFieldValue<DateTimeOffset>(4),
			reader.GetFieldValue<DateTimeOffset>(5));
	}
}
