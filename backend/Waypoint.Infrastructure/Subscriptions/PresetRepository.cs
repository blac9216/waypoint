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

/// <inheritdoc cref="IPresetRepository"/>
public sealed class PresetRepository : IPresetRepository
{
	private const string ProjectionSql = """
		SELECT id, stack, generation, name, line_granularity, anchor_version,
		       is_custom, source_preset_id, created_at, updated_at
		FROM presets
		""";

	private readonly string _connectionString;

	public PresetRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> CreateAsync(Preset preset, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preset);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO presets (
			    id, stack, generation, name, line_granularity, anchor_version,
			    is_custom, source_preset_id)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
			RETURNING id
			""", connection);
		Guid id = preset.Id == Guid.Empty ? Guid.NewGuid() : preset.Id;
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(preset.Stack);
		command.Parameters.AddWithValue(preset.Generation);
		command.Parameters.AddWithValue(preset.Name);
		command.Parameters.AddWithValue(SubscriptionLineGranularityValues.ToDbValue(preset.LineGranularity));
		command.Parameters.AddWithValue((object?)preset.AnchorVersion ?? DBNull.Value);
		command.Parameters.AddWithValue(preset.IsCustom);
		command.Parameters.AddWithValue((object?)preset.SourcePresetId ?? DBNull.Value);

		return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}

	public async Task UpdateAsync(Preset preset, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preset);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE presets
			SET name = $2, line_granularity = $3, anchor_version = $4
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(preset.Id);
		command.Parameters.AddWithValue(preset.Name);
		command.Parameters.AddWithValue(SubscriptionLineGranularityValues.ToDbValue(preset.LineGranularity));
		command.Parameters.AddWithValue((object?)preset.AnchorVersion ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<IReadOnlyList<Preset>> ListAllAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY created_at", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Preset> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(Map(reader));
		}
		return results;
	}

	public async Task<IReadOnlyList<Preset>> ListShippedAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE is_custom = false ORDER BY created_at", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Preset> results = [];
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
		await using NpgsqlCommand command = new("DELETE FROM presets WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static Preset Map(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		Stack: reader.GetString(1),
		Generation: reader.GetString(2),
		Name: reader.GetString(3),
		LineGranularity: SubscriptionLineGranularityValues.FromDbValue(reader.GetString(4)),
		AnchorVersion: reader.IsDBNull(5) ? null : reader.GetString(5),
		IsCustom: reader.GetBoolean(6),
		SourcePresetId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
		CreatedAt: reader.GetFieldValue<DateTimeOffset>(8),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(9));
}
