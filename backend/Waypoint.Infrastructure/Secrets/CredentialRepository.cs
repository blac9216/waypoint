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
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// Credential METADATA only (epic #8 slice 3): every projection here is buildable
/// from columns that carry no secret material -- the blob lives in
/// <c>credential_secrets</c> behind <see cref="ICredentialSecretStore"/> and has no
/// read path in this class. <c>rotated_at</c> is stamped by the caller when a secret
/// write succeeds.
/// </summary>
public enum CredentialWriteOutcome
{
	Ok,
	NotFound,
	NameTaken,
}

public enum CredentialDeleteOutcome
{
	Deleted,
	NotFound,

	/// <summary>
	/// Issue #593: at least one BLOCKING reference remains -- a target, a
	/// schedule/config binding, or a non-terminal job/run. Terminal-only
	/// runs/jobs are no longer a blocker (see <see cref="DeleteAsync"/>); this
	/// outcome now always carries a non-empty <see cref="CredentialDeleteResult.Blockers"/>
	/// list the caller maps to the documented 409 category breakdown.
	/// </summary>
	InUse,
}

/// <summary>Issue #593: the closed set of machine-readable <c>credential_in_use</c> categories <see cref="CredentialRepository.DeleteAsync"/> can report. Stable wire strings -- the frontend branches on these, not on <see cref="ErrorDetail.Message"/> prose.</summary>
public static class CredentialBlockingCategories
{
	/// <summary>A <c>targets.credential_id</c> row -- live connection configuration, not history.</summary>
	public const string Targets = "targets";

	/// <summary>A <c>schedules.credential_id</c> row -- a scheduled job definition, not history.</summary>
	public const string Schedules = "schedules";

	/// <summary>The singleton <c>stigman_connections.credential_id</c> row -- global config, not history.</summary>
	public const string Configuration = "configuration";

	/// <summary>A <c>jobs.credential_id</c> row whose job has not reached a <see cref="JobTerminalStates"/> state -- active work, not history.</summary>
	public const string ActiveJobs = "active_jobs";

	/// <summary>
	/// A <c>runs.credential_id</c> row whose run is still <c>pending</c> or
	/// <c>running</c> -- an in-flight scan/remediate run actively using (or about to
	/// use) the secret. Issue #593 round 2: a run row's own credential_id is
	/// independent of its jobs' -- a live run can reference a credential with NO
	/// co-referencing non-terminal job (the create-&gt;fan-out window; a run stuck
	/// pending because fan-out never ran; a run-secret "my credentials" scan whose
	/// jobs deliberately carry no credential_id). Counting it here re-adds the guard
	/// the old RESTRICT FK gave for free, so a live run blocks deletion instead of
	/// having its credential nulled out from under it with no snapshot.
	/// </summary>
	public const string ActiveRuns = "active_runs";

	/// <summary>
	/// Issue #584 (epic #582): a <c>target_credential_bindings</c> row naming the
	/// credential -- purpose-specific target binding configuration (migration 0043),
	/// distinct from <see cref="Targets"/> (which counts only the legacy
	/// <c>targets.credential_id</c> column). A binding can name a credential the
	/// target's legacy column does not, e.g. a non-default purpose like
	/// <c>vcsa-ssh</c> on a <c>vsphere</c> target -- omitting this category would let
	/// that binding be silently orphaned (its row would remain, pointing at a deleted
	/// credential id via the RESTRICT FK, which migration 0043 deliberately does NOT
	/// relax to SET NULL, so in practice the DELETE would instead fail with a bare FK
	/// violation the caller cannot render as a 409 breakdown). Counting it here
	/// upfront keeps every blocker surfaced through the same machine-readable
	/// category/count shape the rest of this enum already provides.
	/// </summary>
	public const string TargetCredentialBindings = "target_credential_bindings";
}

/// <summary>Result of <see cref="CredentialRepository.DeleteAsync"/>: <see cref="Outcome"/> plus, only for <see cref="CredentialDeleteOutcome.InUse"/>, the category/count breakdown driving the 409 body.</summary>
public sealed record CredentialDeleteResult(CredentialDeleteOutcome Outcome, IReadOnlyList<BlockingCategory>? Blockers = null);

public sealed class CredentialRepository
{
	private const string ProjectionSql = """
		SELECT c.id, c.name, c.credential_type, c.owner, c.health, c.sudo_enabled,
			EXISTS (SELECT 1 FROM credential_secrets s WHERE s.credential_id = c.id) AS has_secret,
			(SELECT count(*) FROM jobs j WHERE j.credential_id = c.id) AS used_by_job_count,
			c.rotated_at, c.created_at, c.updated_at, c.username, c.last_tested_at, c.expires_at
		FROM credentials c
		""";

	private readonly string _connectionString;

	public CredentialRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<CredentialResponse>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ProjectionSql + " ORDER BY c.name", connection);
		List<CredentialResponse> credentials = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			credentials.Add(Map(reader));
		}

		return credentials;
	}

	public async Task<CredentialResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ProjectionSql + " WHERE c.id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>
	/// The single credential of a well-known type (e.g. the depot token --
	/// <see cref="Waypoint.Core.Catalog.CatalogOptions.DepotTokenCredentialType"/>) that
	/// a job type resolves by type rather than by an id carried on the job row. Returns
	/// null when none is configured; ambiguous when more than one row shares the type
	/// (oldest wins, deterministic rather than arbitrary -- an operator who created a
	/// second one by mistake should see the first keep working, not a random choice).
	/// </summary>
	public async Task<CredentialResponse?> FindByTypeAsync(string credentialType, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(credentialType);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			ProjectionSql + " WHERE c.credential_type = $1 ORDER BY c.created_at LIMIT 1", connection);
		command.Parameters.AddWithValue(credentialType);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>Creates the metadata row. Returns null when the name is already taken (the caller maps that to a 409). <paramref name="sudoEnabled"/> is only meaningful for <see cref="CredentialTypes.Ssh"/>; the controller validates that before this is called. <paramref name="username"/> (migration 0012) is the protocol-level login, distinct from <paramref name="name"/>'s human-facing label -- optional at creation, same as it is thereafter.</summary>
	public async Task<Guid?> CreateAsync(string name, string credentialType, string owner, bool sudoEnabled, CancellationToken cancellationToken, string? username = null)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		Guid? id = await CreateAsync(connection, transaction, name, credentialType, owner, sudoEnabled, username, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return id;
	}

	/// <summary>
	/// Connection-scoped core of <see cref="CreateAsync(string,string,string,bool,CancellationToken,string?)"/>
	/// -- does not open a connection/transaction or commit; the caller owns both so
	/// this can be composed with a secret store (issue #188, via
	/// <see cref="ICredentialCreationCoordinator"/>).
	/// </summary>
	internal static async Task<Guid?> CreateAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string name, string credentialType, string owner, bool sudoEnabled,
		string? username, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO credentials (name, credential_type, owner, sudo_enabled, username)
			VALUES ($1, $2, $3, $4, $5)
			ON CONFLICT (name) DO NOTHING
			RETURNING id
			""", connection, transaction);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(credentialType);
		command.Parameters.AddWithValue(owner);
		command.Parameters.AddWithValue(sudoEnabled);
		command.Parameters.AddWithValue((object?)username ?? DBNull.Value);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid id ? id : null;
	}

	public async Task<CredentialWriteOutcome> RenameAsync(Guid id, string name, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE credentials SET name = $2 WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(name);
		try
		{
			return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
				? CredentialWriteOutcome.Ok
				: CredentialWriteOutcome.NotFound;
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
		{
			// PR #187 round 1, finding 2: a rename onto a taken name is the same
			// conflict Create already maps to 409 -- surface it as data, not a 500.
			return CredentialWriteOutcome.NameTaken;
		}
	}

	/// <summary>Issue #20: flips the SSH-only sudo flag. Not gated on credential_type here -- the controller rejects a sudo flip on a non-ssh credential before this is reached, same split as the rest of this class's validation.</summary>
	public async Task<CredentialWriteOutcome> UpdateSudoAsync(Guid id, bool sudoEnabled, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE credentials SET sudo_enabled = $2 WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(sudoEnabled);
		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
			? CredentialWriteOutcome.Ok
			: CredentialWriteOutcome.NotFound;
	}

	/// <summary>Issue #262: sets or clears (via null) the dedicated username column. Unlike <see cref="UpdateSudoAsync"/> this is not gated to one credential_type here -- a username is meaningful for any connection-type credential (vcenter/nsx/ssh), and the controller/handlers decide which types require one.</summary>
	public async Task<CredentialWriteOutcome> UpdateUsernameAsync(Guid id, string? username, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE credentials SET username = $2 WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue((object?)username ?? DBNull.Value);
		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
			? CredentialWriteOutcome.Ok
			: CredentialWriteOutcome.NotFound;
	}

	/// <summary>Stamped only after a successful secret write -- rotation is a fact about the blob, not the metadata.</summary>
	public async Task StampRotatedAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE credentials SET rotated_at = now() WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Connection-scoped core of <see cref="StampRotatedAsync"/>, for composition into
	/// the atomic create-with-secret transaction (issue #188, via
	/// <see cref="ICredentialCreationCoordinator"/>).
	/// </summary>
	internal static async Task StampRotatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("UPDATE credentials SET rotated_at = now() WHERE id = $1", connection, transaction);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #20: records a <c>/credentials/{id}/test</c> outcome as the credential's
	/// new health. This is the ONLY path (besides the queue halt, which only ever sets
	/// <see cref="CredentialHealthStates.AuthFailing"/>) that sets
	/// <see cref="CredentialHealthStates.Valid"/> -- a bare queue-halt unblock does
	/// not, because unblocking proves nothing about whether the credential actually
	/// works (see <c>JobQueueRepository.UnblockCredentialAsync</c>'s doc comment).
	/// A failed test sets <see cref="CredentialHealthStates.AuthFailing"/> too, so a
	/// manual test can surface a bad credential without waiting for the halt's
	/// consecutive-failure threshold.
	/// </summary>
	public async Task<CredentialWriteOutcome> MarkTestOutcomeAsync(Guid id, bool succeeded, CancellationToken cancellationToken)
	{
		string health = succeeded ? CredentialHealthStates.Valid : CredentialHealthStates.AuthFailing;
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		// Issue #560 (migration 0035): last_tested_at is stamped on every outcome,
		// success or failure -- "was this ever tested, and when" is meaningful even
		// for a failing credential, unlike rotated_at which only moves on a secret
		// write.
		await using NpgsqlCommand command = new(
			"UPDATE credentials SET health = $2, last_tested_at = now() WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(health);
		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null
			? CredentialWriteOutcome.Ok
			: CredentialWriteOutcome.NotFound;
	}

	/// <summary>
	/// Issue #593 (epic #577): deletes the credential IF every reference to it is
	/// either absent or terminal history. Order of operations, all in one
	/// transaction:
	///
	///   1. Count each blocking category (<see cref="CredentialBlockingCategories"/>):
	///      targets, schedules, the stigman_connections singleton, non-terminal
	///      jobs (<see cref="JobTerminalStates"/> defines terminal), and non-terminal
	///      (<c>pending</c>/<c>running</c>) runs. Any non-zero category rolls the
	///      transaction back and returns <see cref="CredentialDeleteOutcome.InUse"/>
	///      with the full breakdown. A live run is its own blocker (issue #593 round 2):
	///      its credential_id is independent of its jobs', so a run can be live while
	///      referencing a credential with no non-terminal job (the create-&gt;fan-out
	///      window, a stuck-pending run, or a run-secret scan whose jobs carry no
	///      credential_id) -- counting only jobs would let the delete null a live run's
	///      credential with no snapshot. The initial <c>SELECT ... FOR UPDATE</c> on
	///      the credentials row also serializes this against run/job creation: an
	///      INSERT referencing this credential takes a <c>FOR KEY SHARE</c> FK lock on
	///      the same row, which blocks behind (or before) this transaction's
	///      <c>FOR UPDATE</c>, so a run created concurrently cannot slip its reference
	///      in between the count and the DELETE.
	///   2. Otherwise, every terminal run/job row still naming this credential gets
	///      its non-secret attribution (name, credential_type, username --
	///      deliberately NOT the credential's purpose/binding, per epic #577's
	///      trajectory note that #582 will redesign bindings) snapshotted onto the
	///      new 0041 columns, then credential_id is nulled on those same rows. This
	///      is an explicit UPDATE, not reliance on the FK's ON DELETE SET NULL action
	///      -- the snapshot must land before the row loses its only link to the
	///      credential's display fields.
	///   3. The deletion audit row is written (as before), then the credential row
	///      itself is deleted; <c>credential_secrets</c> follows via ON DELETE
	///      CASCADE. audit_log's own credential_id survives nulled (migration 0006).
	/// </summary>
	public async Task<CredentialDeleteResult> DeleteAsync(Guid id, string actor, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string? name;
		string? credentialType;
		string? username;
		await using (NpgsqlCommand read = new(
			"SELECT name, credential_type, username FROM credentials WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			read.Parameters.AddWithValue(id);
			await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return new CredentialDeleteResult(CredentialDeleteOutcome.NotFound);
			}

			name = reader.GetString(0);
			credentialType = reader.GetString(1);
			username = reader.IsDBNull(2) ? null : reader.GetString(2);
		}

		List<BlockingCategory> blockers = await CountBlockersAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
		if (blockers.Count > 0)
		{
			// No writes have happened yet (the read above is FOR UPDATE only) --
			// rolling back here is a formality, but stays consistent with every
			// other early-return path in this class using an owned transaction.
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return new CredentialDeleteResult(CredentialDeleteOutcome.InUse, blockers);
		}

		await DetachTerminalHistoryAsync(connection, transaction, id, name, credentialType, username, cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand audit = new(
			"INSERT INTO audit_log (event_type, actor, credential_id, detail) VALUES ('credential.deleted', $1, $2, $3::jsonb)",
			connection, transaction))
		{
			audit.Parameters.AddWithValue(actor);
			audit.Parameters.AddWithValue(id);
			audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { credential_id = id, name }));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand delete = new("DELETE FROM credentials WHERE id = $1", connection, transaction))
		{
			delete.Parameters.AddWithValue(id);
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return new CredentialDeleteResult(CredentialDeleteOutcome.Deleted);
	}

	/// <summary>
	/// One row per non-empty <see cref="CredentialBlockingCategories"/> bucket, in a
	/// fixed, deterministic order (targets, schedules, configuration, active_jobs,
	/// active_runs) so the 409 body's category list is stable across calls. A run's
	/// own credential_id is a first-class blocker (issue #593 round 2): a
	/// <c>pending</c>/<c>running</c> run can reference a credential with no
	/// co-referencing non-terminal job (the create-&gt;fan-out window; a run stuck
	/// pending; a run-secret scan whose jobs carry no credential_id), so counting only
	/// jobs would let a live run's credential be deleted+nulled with no snapshot -- the
	/// exact hole the old RESTRICT FK closed for free.
	/// </summary>
	private static async Task<List<BlockingCategory>> CountBlockersAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
	{
		List<BlockingCategory> blockers = [];

		int targets = await CountAsync(connection, transaction, "SELECT count(*) FROM targets WHERE credential_id = $1", id, cancellationToken).ConfigureAwait(false);
		if (targets > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.Targets, targets));
		}

		// Issue #584: target_credential_bindings is a distinct category from `targets`
		// above -- a binding can name this credential for a non-default purpose (e.g.
		// vcsa-ssh) that targets.credential_id never carried, so it must be counted
		// separately rather than folded into the Targets bucket.
		int targetCredentialBindings = await CountAsync(
			connection, transaction, "SELECT count(*) FROM target_credential_bindings WHERE credential_id = $1", id, cancellationToken).ConfigureAwait(false);
		if (targetCredentialBindings > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.TargetCredentialBindings, targetCredentialBindings));
		}

		int schedules = await CountAsync(connection, transaction, "SELECT count(*) FROM schedules WHERE credential_id = $1", id, cancellationToken).ConfigureAwait(false);
		if (schedules > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.Schedules, schedules));
		}

		int configuration = await CountAsync(connection, transaction, "SELECT count(*) FROM stigman_connections WHERE credential_id = $1", id, cancellationToken).ConfigureAwait(false);
		if (configuration > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.Configuration, configuration));
		}

		int activeJobs = await CountAsync(
			connection, transaction,
			"SELECT count(*) FROM jobs WHERE credential_id = $1 AND state NOT IN ('uploaded', 'done', 'failed', 'auth-failed', 'cancelled')",
			id, cancellationToken).ConfigureAwait(false);
		if (activeJobs > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.ActiveJobs, activeJobs));
		}

		int activeRuns = await CountAsync(
			connection, transaction,
			"SELECT count(*) FROM runs WHERE credential_id = $1 AND state IN ('pending', 'running')",
			id, cancellationToken).ConfigureAwait(false);
		if (activeRuns > 0)
		{
			blockers.Add(new BlockingCategory(CredentialBlockingCategories.ActiveRuns, activeRuns));
		}

		return blockers;
	}

	private static async Task<int> CountAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(sql, connection, transaction);
		command.Parameters.AddWithValue(id);
		return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Snapshots non-secret attribution onto every terminal run/job still naming
	/// <paramref name="id"/>, then nulls their credential_id. Only reachable after
	/// <see cref="CountBlockersAsync"/> has already proven zero non-terminal jobs
	/// reference the credential, so every jobs row this touches is terminal by
	/// construction; the WHERE clause repeats the terminal check anyway rather than
	/// trusting that invariant blindly (defense in depth against the two drifting
	/// out of sync).
	/// </summary>
	private static async Task DetachTerminalHistoryAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string name, string credentialType, string? username,
		CancellationToken cancellationToken)
	{
		await using (NpgsqlCommand jobs = new(
			"""
			UPDATE jobs
			SET credential_name = $2, credential_type = $3, credential_username = $4, credential_id = NULL
			WHERE credential_id = $1 AND state IN ('uploaded', 'done', 'failed', 'auth-failed', 'cancelled')
			""", connection, transaction))
		{
			jobs.Parameters.AddWithValue(id);
			jobs.Parameters.AddWithValue(name);
			jobs.Parameters.AddWithValue(credentialType);
			jobs.Parameters.AddWithValue((object?)username ?? DBNull.Value);
			await jobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		// A run's own credential_id (docs/api-contract.md: the credential a scan/
		// remediate run was initiated with) is independent of its jobs' -- detach it
		// whenever the run itself is no longer 'running'/'pending' (its own terminal
		// states, RunStates in api-contract.md), regardless of the jobs UPDATE above.
		await using (NpgsqlCommand runs = new(
			"""
			UPDATE runs
			SET credential_name = $2, credential_type = $3, credential_username = $4, credential_id = NULL
			WHERE credential_id = $1 AND state NOT IN ('pending', 'running')
			""", connection, transaction))
		{
			runs.Parameters.AddWithValue(id);
			runs.Parameters.AddWithValue(name);
			runs.Parameters.AddWithValue(credentialType);
			runs.Parameters.AddWithValue((object?)username ?? DBNull.Value);
			await runs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private static CredentialResponse Map(NpgsqlDataReader reader)
	{
		return new CredentialResponse(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetBoolean(5),
			reader.GetBoolean(6),
			reader.GetInt64(7),
			reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
			reader.GetFieldValue<DateTimeOffset>(9),
			reader.GetFieldValue<DateTimeOffset>(10),
			reader.IsDBNull(11) ? null : reader.GetString(11),
			reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
			reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13));
	}
}
