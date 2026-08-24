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

/// <inheritdoc cref="IDepotEnrollmentRepository"/>
public sealed class DepotEnrollmentRepository : IDepotEnrollmentRepository
{
	private const string SelectSql =
		"""
		SELECT state, depot_id, depot_id_generated_at, paired_asset_id, paired_at,
		       last_validation_failure, reset_at, updated_at
		FROM depot_enrollment
		WHERE id = 1
		""";

	private readonly string _connectionString;

	public DepotEnrollmentRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(SelectSql, connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(depotId);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET depot_id = $1,
			    depot_id_generated_at = now(),
			    state = CASE
			        WHEN state IN ('tool_unavailable', 'depot_id_unavailable') THEN 'awaiting_portal_registration'
			        ELSE state
			    END
			WHERE id = 1
			""", connection);
		command.Parameters.AddWithValue(depotId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetPairedAsync(string assetId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET paired_asset_id = $1,
			    paired_at = now(),
			    state = 'activation_code_stored',
			    last_validation_failure = NULL
			WHERE id = 1
			""", connection);
		command.Parameters.AddWithValue(assetId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = $1,
			    last_validation_failure = $2
			WHERE id = 1
			""", connection);
		command.Parameters.AddWithValue(succeeded ? DepotEnrollmentStates.Validated : DepotEnrollmentStates.AuthFailing);
		command.Parameters.AddWithValue((object?)failureNote ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task ResetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'depot_id_unavailable',
			    depot_id = NULL,
			    depot_id_generated_at = NULL,
			    paired_asset_id = NULL,
			    paired_at = NULL,
			    last_validation_failure = NULL,
			    reset_at = now()
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static DepotEnrollment Map(NpgsqlDataReader reader) => new(
		State: reader.GetString(0),
		DepotId: reader.IsDBNull(1) ? null : reader.GetString(1),
		DepotIdGeneratedAt: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
		PairedAssetId: reader.IsDBNull(3) ? null : reader.GetString(3),
		PairedAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
		LastValidationFailure: reader.IsDBNull(5) ? null : reader.GetString(5),
		ResetAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(7));
}
