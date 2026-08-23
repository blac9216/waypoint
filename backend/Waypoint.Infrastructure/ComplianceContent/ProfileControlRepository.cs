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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IProfileControlRepository"/>
public sealed class ProfileControlRepository : IProfileControlRepository
{
	private readonly string _connectionString;

	public ProfileControlRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<ProfileControl>> ListByProfileAsync(Guid profileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, profile_id, control_id, title, severity, updated_at
			FROM profile_controls
			WHERE profile_id = $1
			ORDER BY control_id
			""", connection);
		command.Parameters.AddWithValue(profileId);

		List<ProfileControl> controls = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			controls.Add(new ProfileControl(
				reader.GetGuid(0),
				reader.GetGuid(1),
				reader.GetString(2),
				reader.IsDBNull(3) ? null : reader.GetString(3),
				reader.IsDBNull(4) ? null : reader.GetString(4),
				reader.GetFieldValue<DateTimeOffset>(5)));
		}

		return controls;
	}

	public async Task ReplaceForProfileAsync(Guid profileId, IReadOnlyList<ProfileControlUpsert> controls, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(controls);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string[] controlIds = [.. controls.Select(control => control.ControlId)];

		foreach (ProfileControlUpsert control in controls)
		{
			await using NpgsqlCommand upsert = new(
				"""
				INSERT INTO profile_controls (profile_id, control_id, title, severity)
				VALUES ($1, $2, $3, $4)
				ON CONFLICT (profile_id, control_id) DO UPDATE SET
					title = EXCLUDED.title,
					severity = EXCLUDED.severity
				""", connection, transaction);
			upsert.Parameters.AddWithValue(profileId);
			upsert.Parameters.AddWithValue(control.ControlId);
			upsert.Parameters.AddWithValue((object?)control.Title ?? DBNull.Value);
			upsert.Parameters.AddWithValue((object?)control.Severity ?? DBNull.Value);
			await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand delete = new(
			"DELETE FROM profile_controls WHERE profile_id = $1 AND NOT (control_id = ANY($2))", connection, transaction))
		{
			delete.Parameters.AddWithValue(profileId);
			delete.Parameters.Add(new NpgsqlParameter { Value = controlIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}
}
