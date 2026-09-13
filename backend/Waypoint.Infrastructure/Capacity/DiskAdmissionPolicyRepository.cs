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
using Waypoint.Core.Capacity;

namespace Waypoint.Infrastructure.Capacity;

/// <inheritdoc cref="IDiskAdmissionPolicyRepository"/>
public sealed class DiskAdmissionPolicyRepository : IDiskAdmissionPolicyRepository
{
	private readonly string _connectionString;

	public DiskAdmissionPolicyRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<DiskAdmissionPolicy?> GetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT reserve_bytes, updated_by, updated_at FROM disk_admission_policy WHERE id = 1", connection);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return Read(reader);
	}

	public async Task<DiskAdmissionPolicy> SetAsync(long reserveBytes, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		if (reserveBytes < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(reserveBytes), reserveBytes, "Disk-admission reserve must be a non-negative byte count.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand update = new(
			"""
			UPDATE disk_admission_policy
			SET reserve_bytes = $1, updated_by = $2
			WHERE id = 1
			RETURNING reserve_bytes, updated_by, updated_at
			""", connection);
		update.Parameters.AddWithValue(reserveBytes);
		update.Parameters.AddWithValue(actor);

		await using NpgsqlDataReader reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			// The migration seeds id=1 unconditionally and nothing deletes it -- see
			// this method's own contract on IDiskAdmissionPolicyRepository.GetAsync.
			throw new InvalidOperationException("disk_admission_policy singleton row (id=1) is missing; cannot update it.");
		}

		return Read(reader);
	}

	private static DiskAdmissionPolicy Read(NpgsqlDataReader reader) => new(
		ReserveBytes: reader.GetInt64(0),
		UpdatedBy: reader.IsDBNull(1) ? null : reader.GetString(1),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2));
}
