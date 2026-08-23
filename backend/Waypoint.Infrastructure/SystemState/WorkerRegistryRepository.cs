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

	public async Task HeartbeatAsync(string workerId, IReadOnlyList<string> jobTypes, bool ready, IReadOnlyList<StarvedWorkerJobType> starvedJobTypes, CancellationToken cancellationToken, bool? toolPresent = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
		ArgumentNullException.ThrowIfNull(jobTypes);
		ArgumentNullException.ThrowIfNull(starvedJobTypes);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO worker_registry (worker_id, job_types, ready, starved_job_types, tool_present, last_seen_at)
			VALUES (@worker_id, @job_types, @ready, @starved_job_types, @tool_present, now())
			ON CONFLICT (worker_id) DO UPDATE
				SET job_types = EXCLUDED.job_types,
					ready = EXCLUDED.ready,
					starved_job_types = EXCLUDED.starved_job_types,
					tool_present = EXCLUDED.tool_present,
					last_seen_at = EXCLUDED.last_seen_at
			""", connection);
		command.Parameters.AddWithValue("worker_id", workerId);
		command.Parameters.Add(new NpgsqlParameter("job_types", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(jobTypes) });
		command.Parameters.AddWithValue("ready", ready);
		command.Parameters.Add(new NpgsqlParameter("starved_job_types", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(starvedJobTypes) });
		command.Parameters.AddWithValue("tool_present", (object?)toolPresent ?? DBNull.Value);

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<WorkerHeartbeat>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT worker_id, job_types, ready, last_seen_at, starved_job_types, tool_present
			FROM worker_registry
			ORDER BY last_seen_at DESC
			""", connection);

		List<WorkerHeartbeat> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			string[] jobTypes = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
			StarvedWorkerJobType[] starvedJobTypes = JsonSerializer.Deserialize<StarvedWorkerJobType[]>(reader.GetString(4)) ?? [];
			results.Add(new WorkerHeartbeat(
				WorkerId: reader.GetString(0),
				JobTypes: jobTypes,
				Ready: reader.GetBoolean(2),
				LastSeenAt: reader.GetFieldValue<DateTimeOffset>(3),
				StarvedJobTypes: starvedJobTypes,
				ToolPresent: reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
		}

		return results;
	}
}
