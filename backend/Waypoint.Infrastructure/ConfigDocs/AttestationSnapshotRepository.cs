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
using Waypoint.Core.ConfigDocs;

namespace Waypoint.Infrastructure.ConfigDocs;

/// <summary>
/// Storage for <c>attestation_snapshots</c> (issue #306, migration 0021): the persisted
/// at-scan-time attestation ledger that replaces
/// <c>RunsController.GetAttestationsApplied</c>'s old live re-resolution. Plain Npgsql,
/// no ORM -- the same convention <see cref="ConfigDocRepository"/> establishes for this
/// layer.
///
/// This type only ever INSERTs: the table is append-only by DB trigger (0021), and
/// nothing here issues UPDATE/DELETE, matching the discipline
/// <see cref="ConfigDocRepository.SaveVersionAsync"/>'s doc comment describes for
/// <c>config_versions</c>.
/// </summary>
public sealed class AttestationSnapshotRepository
{
	private const string Projection = """
		SELECT id, run_id, job_id, target_id, profile, scope, doc_id, doc_version,
		       doc_author, doc_version_created_at, applied, expired, applied_at
		FROM attestation_snapshots
		""";

	private readonly string _connectionString;

	public AttestationSnapshotRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>
	/// Records the attest stage's resolution for one (job, target) at the moment it
	/// happened. <c>ON CONFLICT (job_id, target_id) DO NOTHING</c> against the 0021
	/// unique constraint makes this safe to call again if a lease-recovered/resumed
	/// attest stage re-runs its whole body (ADR-0012, <c>ExecuteAttestStageAsync</c> has
	/// no other idempotency guard) -- first genuine resolution wins, so a resumed stage
	/// can never overwrite or duplicate the original at-scan-time fact.
	/// </summary>
	public async Task RecordAsync(
		Guid runId,
		Guid jobId,
		Guid targetId,
		string profile,
		string scope,
		Guid? docId,
		int? docVersion,
		string? docAuthor,
		DateTimeOffset? docVersionCreatedAt,
		bool applied,
		bool expired,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profile);
		ArgumentException.ThrowIfNullOrWhiteSpace(scope);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO attestation_snapshots
			    (run_id, job_id, target_id, profile, scope, doc_id, doc_version, doc_author, doc_version_created_at, applied, expired)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
			ON CONFLICT (job_id, target_id) DO NOTHING
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue(profile);
		command.Parameters.AddWithValue(scope);
		command.Parameters.AddWithValue((object?)docId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)docVersion ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)docAuthor ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)docVersionCreatedAt ?? DBNull.Value);
		command.Parameters.AddWithValue(applied);
		command.Parameters.AddWithValue(expired);

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>The full recorded ledger for a run, oldest first -- what backs <c>GET /runs/{id}/attestations-applied</c>.</summary>
	public async Task<IReadOnlyList<AttestationSnapshot>> ListForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{Projection} WHERE run_id = $1 ORDER BY applied_at", connection);
		command.Parameters.AddWithValue(runId);

		List<AttestationSnapshot> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	private static AttestationSnapshot Map(NpgsqlDataReader reader)
	{
		return new AttestationSnapshot(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.GetGuid(2),
			reader.GetGuid(3),
			reader.GetString(4),
			reader.GetString(5),
			reader.IsDBNull(6) ? null : reader.GetGuid(6),
			reader.IsDBNull(7) ? null : reader.GetInt32(7),
			reader.IsDBNull(8) ? null : reader.GetString(8),
			reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
			reader.GetBoolean(10),
			reader.GetBoolean(11),
			reader.GetFieldValue<DateTimeOffset>(12));
	}
}
