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
using NpgsqlTypes;
using Waypoint.Core.SystemState;

namespace Waypoint.Infrastructure.SystemState;

/// <inheritdoc cref="IWorkerRegistryWriter"/>
public sealed class WorkerRegistryRepository : IWorkerRegistryWriter, IWorkerRegistryReader
{
	private readonly string _connectionString;

	public WorkerRegistryRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task HeartbeatAsync(string workerId, IReadOnlyList<string> jobTypes, bool ready, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentNullException.ThrowIfNull(jobTypes);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO worker_registry (worker_id, job_types, ready, last_seen_at)
			VALUES (@worker_id, @job_types, @ready, now())
			ON CONFLICT (worker_id) DO UPDATE
				SET job_types = EXCLUDED.job_types,
					ready = EXCLUDED.ready,
					last_seen_at = EXCLUDED.last_seen_at
			""", connection);
		command.Parameters.AddWithValue("worker_id", workerId);
		command.Parameters.Add(new NpgsqlParameter("job_types", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(jobTypes) });
		command.Parameters.AddWithValue("ready", ready);

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<WorkerHeartbeat>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT worker_id, job_types, ready, last_seen_at
			FROM worker_registry
			ORDER BY last_seen_at DESC
			""", connection);

		List<WorkerHeartbeat> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			string[] jobTypes = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
			results.Add(new WorkerHeartbeat(
				WorkerId: reader.GetString(0),
				JobTypes: jobTypes,
				Ready: reader.GetBoolean(2),
				LastSeenAt: reader.GetFieldValue<DateTimeOffset>(3)));
		}

		return results;
	}
}
