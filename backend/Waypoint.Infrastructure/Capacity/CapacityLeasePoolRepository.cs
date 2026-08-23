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

/// <summary>
/// Postgres implementation of the shared capacity lease pool (issue #569, ADR-0020).
/// Every claim-shaped mutation is one SQL statement that takes <c>FOR UPDATE</c> on the
/// singleton <c>capacity_pool</c> row first, so concurrent claims from any number of
/// runner processes serialize on that row -- two runners racing for the last slot
/// cannot both observe it free. Expired rows are excluded by predicate
/// (<c>expires_at &gt; now()</c>) everywhere they are summed, so a lost worker's stale
/// lease stops counting the moment it expires regardless of when the reaper next runs
/// -- the same recovery posture as the jobs queue's expired-lease predicate.
/// </summary>
public sealed class CapacityLeasePoolRepository : ICapacityLeasePool, ICapacityPoolStatusReader
{
	private readonly string _connectionString;

	public CapacityLeasePoolRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task RegisterPoolCapacityAsync(string reportedBy, double cpuCores, long memoryBytes, bool operatorSet, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reportedBy);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuCores);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBytes);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Operator writes always win and pin source='operator'. Derived writes never
		// touch an operator row, and converge with other derived reports via GREATEST
		// per axis (see the migration 0036 header for why: replicas on one host all
		// measure the same hardware; a container-capped one must not shrink the pool).
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO capacity_pool (id, cpu_cores, memory_bytes, source, reported_by, updated_at)
			VALUES (1, $1, $2, $3, $4, now())
			ON CONFLICT (id) DO UPDATE SET
				cpu_cores = CASE
					WHEN EXCLUDED.source = 'operator' THEN EXCLUDED.cpu_cores
					WHEN capacity_pool.source = 'operator' THEN capacity_pool.cpu_cores
					ELSE GREATEST(capacity_pool.cpu_cores, EXCLUDED.cpu_cores) END,
				memory_bytes = CASE
					WHEN EXCLUDED.source = 'operator' THEN EXCLUDED.memory_bytes
					WHEN capacity_pool.source = 'operator' THEN capacity_pool.memory_bytes
					ELSE GREATEST(capacity_pool.memory_bytes, EXCLUDED.memory_bytes) END,
				source = CASE
					WHEN EXCLUDED.source = 'operator' OR capacity_pool.source = 'operator' THEN 'operator'
					ELSE 'derived' END,
				reported_by = CASE
					WHEN EXCLUDED.source = 'operator' OR capacity_pool.source <> 'operator' THEN EXCLUDED.reported_by
					ELSE capacity_pool.reported_by END,
				updated_at = now()
			""", connection);
		command.Parameters.AddWithValue(cpuCores);
		command.Parameters.AddWithValue(memoryBytes);
		command.Parameters.AddWithValue(operatorSet ? "operator" : "derived");
		command.Parameters.AddWithValue(reportedBy);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<bool> TryClaimAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Two statements in one transaction, deliberately NOT one statement: under
		// READ COMMITTED, a single statement's snapshot is taken BEFORE any FOR UPDATE
		// lock wait, so two racing claimants would each sum the leases as of before the
		// other's commit and both admit themselves -- the exact overcommit race this
		// lock exists to prevent (caught by TryClaim_ConcurrentRunnersRacingForLastSlots).
		// Locking the pool row in its own statement first means the second statement's
		// snapshot is taken after the lock is granted, and therefore sees every lease
		// committed by whoever held the lock before us.
		// No pool row -> nothing to lock -> denied (fail safe: no pool, no admission).
		await using (NpgsqlCommand lockCommand = new(
			"SELECT cpu_cores, memory_bytes FROM capacity_pool WHERE id = 1 FOR UPDATE", connection, transaction))
		{
			await using NpgsqlDataReader reader = await lockCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return false;
			}
		}

		// The admission predicate has two tiers (ADR-0020 fairness):
		//   - active tier: the weights must fit alongside every unexpired ACTIVE lease
		//     belonging to other jobs. Always required.
		//   - reservation tier: a job WITHOUT its own reservation must additionally fit
		//     alongside other jobs' unexpired RESERVATIONS, so capacity freed while a
		//     starved job waits accumulates for it instead of being consumed by an
		//     endless stream of smaller claims. A job converting its OWN reservation
		//     skips this tier (its reservation is the one being converted, and two
		//     mutually-blocking large reservations must not deadlock the pool --
		//     first-fit conversion wins, the other keeps waiting with its reservation
		//     intact).
		// The ON CONFLICT arm converts this job's own reservation row -- or takes over
		// a stale active row for the same job after worker loss (the jobs-queue lease
		// already guarantees single job ownership, so the capacity row follows the job).
		await using NpgsqlCommand command = new(
			"""
			WITH pool AS (
				SELECT cpu_cores, memory_bytes FROM capacity_pool WHERE id = 1
			),
			active AS (
				SELECT COALESCE(SUM(cpu_cores), 0)::double precision AS cpu, COALESCE(SUM(memory_bytes), 0)::bigint AS mem
				FROM capacity_leases
				WHERE NOT reserved AND expires_at > now() AND job_id <> $1
			),
			reservations AS (
				SELECT COALESCE(SUM(cpu_cores), 0)::double precision AS cpu, COALESCE(SUM(memory_bytes), 0)::bigint AS mem
				FROM capacity_leases
				WHERE reserved AND expires_at > now() AND job_id <> $1
			),
			own_reservation AS (
				SELECT 1 FROM capacity_leases WHERE job_id = $1 AND reserved AND expires_at > now()
			)
			INSERT INTO capacity_leases (job_id, runner_id, job_type, cpu_cores, memory_bytes, reserved, heartbeat_at, expires_at)
			SELECT $1, $2, $3, $4, $5, FALSE, now(), now() + $6
			FROM pool, active, reservations
			WHERE active.cpu + $4 <= pool.cpu_cores
			  AND active.mem + $5 <= pool.memory_bytes
			  AND (EXISTS (SELECT 1 FROM own_reservation)
			       OR (active.cpu + reservations.cpu + $4 <= pool.cpu_cores
			           AND active.mem + reservations.mem + $5 <= pool.memory_bytes))
			ON CONFLICT (job_id) DO UPDATE SET
				runner_id = EXCLUDED.runner_id,
				job_type = EXCLUDED.job_type,
				cpu_cores = EXCLUDED.cpu_cores,
				memory_bytes = EXCLUDED.memory_bytes,
				reserved = FALSE,
				heartbeat_at = now(),
				expires_at = EXCLUDED.expires_at
			RETURNING job_id
			""", connection, transaction);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(runnerId);
		command.Parameters.AddWithValue(jobType);
		command.Parameters.AddWithValue(cpuCores);
		command.Parameters.AddWithValue(memoryBytes);
		command.Parameters.AddWithValue(leaseDuration);
		bool claimed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return claimed;
	}

	public async Task<bool> TryReserveAsync(Guid jobId, string runnerId, string jobType, double cpuCores, long memoryBytes, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// Refreshing an existing reservation keeps its original created_at (that is the
		// "waiting since" surfaced by GET /system). The WHERE on the conflict arm keeps
		// a reservation write from ever downgrading an ACTIVE lease the same job
		// already converted -- release/re-reserve is the only path that could race that
		// way, and losing the race must not un-admit a running job.
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO capacity_leases (job_id, runner_id, job_type, cpu_cores, memory_bytes, reserved, heartbeat_at, expires_at)
			VALUES ($1, $2, $3, $4, $5, TRUE, now(), now() + $6)
			ON CONFLICT (job_id) DO UPDATE SET
				runner_id = EXCLUDED.runner_id,
				heartbeat_at = now(),
				expires_at = EXCLUDED.expires_at
			WHERE capacity_leases.reserved
			RETURNING job_id
			""", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(runnerId);
		command.Parameters.AddWithValue(jobType);
		command.Parameters.AddWithValue(cpuCores);
		command.Parameters.AddWithValue(memoryBytes);
		command.Parameters.AddWithValue(leaseDuration);
		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
	}

	public async Task<bool> RenewAsync(Guid jobId, string runnerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE capacity_leases SET heartbeat_at = now(), expires_at = now() + $3 WHERE job_id = $1 AND runner_id = $2",
			connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(runnerId);
		command.Parameters.AddWithValue(leaseDuration);
		return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
	}

	public async Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM capacity_leases WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<int> ReapExpiredAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM capacity_leases WHERE expires_at <= now()", connection);
		return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<CapacityPoolStatus?> GetStatusAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand poolCommand = new(
			"""
			SELECT p.cpu_cores, p.memory_bytes, p.source, p.updated_at,
			       COALESCE(l.cpu, 0)::double precision, COALESCE(l.mem, 0)::bigint, COALESCE(l.count, 0)::int
			FROM capacity_pool p
			LEFT JOIN (
				SELECT SUM(cpu_cores) AS cpu, SUM(memory_bytes) AS mem, COUNT(*) AS count
				FROM capacity_leases WHERE NOT reserved AND expires_at > now()
			) l ON TRUE
			WHERE p.id = 1
			""", connection);

		double cpuCores;
		long memoryBytes;
		string source;
		DateTimeOffset updatedAt;
		double leasedCpu;
		long leasedMemory;
		int activeCount;
		await using (NpgsqlDataReader reader = await poolCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return null;
			}

			cpuCores = reader.GetDouble(0);
			memoryBytes = reader.GetInt64(1);
			source = reader.GetString(2);
			updatedAt = reader.GetFieldValue<DateTimeOffset>(3);
			leasedCpu = reader.GetDouble(4);
			leasedMemory = reader.GetInt64(5);
			activeCount = reader.GetInt32(6);
		}

		List<CapacityReservation> reservations = [];
		await using NpgsqlCommand reservationCommand = new(
			"""
			SELECT job_id, job_type, runner_id, cpu_cores, memory_bytes, created_at
			FROM capacity_leases
			WHERE reserved AND expires_at > now()
			ORDER BY created_at
			""", connection);
		await using (NpgsqlDataReader reader = await reservationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				reservations.Add(new CapacityReservation(
					reader.GetGuid(0),
					reader.GetString(1),
					reader.GetString(2),
					reader.GetDouble(3),
					reader.GetInt64(4),
					reader.GetFieldValue<DateTimeOffset>(5)));
			}
		}

		return new CapacityPoolStatus(cpuCores, memoryBytes, source, updatedAt, leasedCpu, leasedMemory, activeCount, reservations);
	}
}
