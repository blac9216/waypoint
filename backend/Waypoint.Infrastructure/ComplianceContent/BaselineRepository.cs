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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IBaselineRepository"/>
public sealed class BaselineRepository : IBaselineRepository
{
	private const string RevisionProjectionSql =
		"SELECT id, source_commit, content_digest, staged_relative_path, status, gc_eligible, staged_at FROM content_revisions";

	private const string BaselineProjectionSql =
		"SELECT id, content_revision_id, catalog_execution_profile_id, benchmark_revision_id, status, activated_at, activated_by, superseded_at, created_at FROM baselines";

	private readonly string _connectionString;

	public BaselineRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<ContentRevision> RecordStagedRevisionAsync(
		string sourceCommit, string contentDigest, string stagedRelativePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentDigest);
		ArgumentException.ThrowIfNullOrWhiteSpace(stagedRelativePath);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO content_revisions (source_commit, content_digest, staged_relative_path)
			VALUES ($1, $2, $3)
			ON CONFLICT (source_commit, content_digest) DO UPDATE SET source_commit = EXCLUDED.source_commit
			RETURNING id, source_commit, content_digest, staged_relative_path, status, gc_eligible, staged_at
			""", connection);
		command.Parameters.AddWithValue(sourceCommit);
		command.Parameters.AddWithValue(contentDigest);
		command.Parameters.AddWithValue(stagedRelativePath);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return ReadRevision(reader);
	}

	public async Task<ContentRevision?> GetRevisionAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(RevisionProjectionSql + " WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRevision(reader) : null;
	}

	public async Task<IReadOnlyList<ContentRevision>> ListRevisionsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(RevisionProjectionSql + " ORDER BY staged_at DESC", connection);

		List<ContentRevision> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(ReadRevision(reader));
		}

		return results;
	}

	public async Task<Baseline> CreateStagedBaselineAsync(
		Guid contentRevisionId, Guid catalogExecutionProfileId, Guid? benchmarkRevisionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO baselines (content_revision_id, catalog_execution_profile_id, benchmark_revision_id, status)
			VALUES ($1, $2, $3, 'staged')
			RETURNING id, content_revision_id, catalog_execution_profile_id, benchmark_revision_id, status, activated_at, activated_by, superseded_at, created_at
			""", connection);
		command.Parameters.AddWithValue(contentRevisionId);
		command.Parameters.AddWithValue(catalogExecutionProfileId);
		command.Parameters.AddWithValue((object?)benchmarkRevisionId ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return ReadBaseline(reader);
	}

	public async Task<Baseline?> GetBaselineAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(BaselineProjectionSql + " WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBaseline(reader) : null;
	}

	public async Task<IReadOnlyList<Baseline>> ListBaselinesForExecutionProfileAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(BaselineProjectionSql + " WHERE catalog_execution_profile_id = $1 ORDER BY created_at DESC", connection);
		command.Parameters.AddWithValue(catalogExecutionProfileId);

		List<Baseline> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(ReadBaseline(reader));
		}

		return results;
	}

	public async Task<IReadOnlyList<Baseline>> ListAllBaselinesAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(BaselineProjectionSql + " ORDER BY created_at DESC", connection);

		List<Baseline> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(ReadBaseline(reader));
		}

		return results;
	}

	public async Task<Baseline?> GetActiveBaselineAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			BaselineProjectionSql + " WHERE catalog_execution_profile_id = $1 AND status = 'active'", connection);
		command.Parameters.AddWithValue(catalogExecutionProfileId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBaseline(reader) : null;
	}

	public Task<BaselineActivationOutcome> ActivateAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) =>
		ActivateOrRollbackAsync(baselineId, activatedBy, cancellationToken);

	public Task<BaselineActivationOutcome> RollbackAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) =>
		ActivateOrRollbackAsync(baselineId, activatedBy, cancellationToken);

	/// <summary>
	/// Shared atomic pointer-swap: activation and rollback are the same operation
	/// (ADR-0022 "rollback ... creates a new activation event pointing at the old
	/// artifact set") -- both flip exactly one target row to 'active' and supersede
	/// any prior active row for the SAME execution profile, all inside one
	/// transaction. Concurrency serialization is per EXECUTION PROFILE, not per
	/// target baseline row: two racing activations of DIFFERENT staged baselines for
	/// the same execution profile must serialize too (this class's own test suite
	/// caught exactly that gap as a live 23505 against the partial unique index), so
	/// after resolving the target's execution profile the transaction's real lock is
	/// <c>SELECT ... FOR UPDATE</c> on the parent <c>catalog_execution_profiles</c>
	/// row -- every activation/rollback for one execution profile funnels through
	/// that single row lock, and the target's status is re-read only after the lock
	/// is held.
	/// </summary>
	private async Task<BaselineActivationOutcome> ActivateOrRollbackAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(activatedBy);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Un-locked existence read first: the execution profile id is needed before
		// the per-profile serialization lock can be taken. The reader must be fully
		// disposed before any other command (including ROLLBACK) runs on this
		// connection -- Npgsql allows one in-flight command per connection.
		Guid executionProfileId = Guid.Empty;
		Guid contentRevisionId = Guid.Empty;
		bool found = false;
		await using (NpgsqlCommand findTarget = new(
			"SELECT catalog_execution_profile_id, content_revision_id FROM baselines WHERE id = $1", connection, transaction))
		{
			findTarget.Parameters.AddWithValue(baselineId);
			await using NpgsqlDataReader reader = await findTarget.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				found = true;
				executionProfileId = reader.GetGuid(0);
				contentRevisionId = reader.GetGuid(1);
			}
		}

		if (!found)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return BaselineActivationOutcome.NotFound;
		}

		// The per-execution-profile serialization point: every concurrent
		// activation/rollback for this profile blocks here until the winner commits.
		await using (NpgsqlCommand lockProfile = new(
			"SELECT id FROM catalog_execution_profiles WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			lockProfile.Parameters.AddWithValue(executionProfileId);
			await lockProfile.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		// Re-read the target's status now that the profile lock is held -- a
		// concurrent activation that won the race may have activated or superseded
		// this row between the provisional read above and the lock acquisition.
		string currentStatus;
		await using (NpgsqlCommand statusRead = new(
			"SELECT status FROM baselines WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			statusRead.Parameters.AddWithValue(baselineId);
			object? status = await statusRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			currentStatus = status as string ?? BaselineStatuses.Staged;
		}

		if (currentStatus == BaselineStatuses.Active)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return BaselineActivationOutcome.AlreadyActive;
		}

		await using (NpgsqlCommand revisionCheck = new(
			"SELECT status FROM content_revisions WHERE id = $1 FOR UPDATE", connection, transaction))
		{
			revisionCheck.Parameters.AddWithValue(contentRevisionId);
			object? revisionStatus = await revisionCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (revisionStatus is not string status || status == ContentRevisionStatuses.Rejected)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return BaselineActivationOutcome.RevisionNotEligible;
			}
		}

		// Supersede any existing active baseline for this SAME execution profile first
		// (the partial unique index would otherwise reject the activation below) --
		// safe against interleaving because every path into this section holds the
		// catalog_execution_profiles row lock above.
		await using (NpgsqlCommand supersede = new(
			"""
			UPDATE baselines
			SET status = 'superseded', superseded_at = now()
			WHERE catalog_execution_profile_id = $1 AND status = 'active'
			""", connection, transaction))
		{
			supersede.Parameters.AddWithValue(executionProfileId);
			await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand activate = new(
			"""
			UPDATE baselines
			SET status = 'active', activated_at = now(), activated_by = $2, superseded_at = NULL
			WHERE id = $1
			""", connection, transaction))
		{
			activate.Parameters.AddWithValue(baselineId);
			activate.Parameters.AddWithValue(activatedBy);
			await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand markRevisionActivated = new(
			"UPDATE content_revisions SET status = 'activated' WHERE id = $1 AND status <> 'activated'", connection, transaction))
		{
			markRevisionActivated.Parameters.AddWithValue(contentRevisionId);
			await markRevisionActivated.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return BaselineActivationOutcome.Activated;
	}

	private static ContentRevision ReadRevision(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.GetString(3),
		reader.GetString(4),
		reader.GetBoolean(5),
		reader.GetFieldValue<DateTimeOffset>(6));

	private static Baseline ReadBaseline(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetGuid(1),
		reader.GetGuid(2),
		reader.IsDBNull(3) ? null : reader.GetGuid(3),
		reader.GetString(4),
		reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
		reader.IsDBNull(6) ? null : reader.GetString(6),
		reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
		reader.GetFieldValue<DateTimeOffset>(8));

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return connection;
	}
}
