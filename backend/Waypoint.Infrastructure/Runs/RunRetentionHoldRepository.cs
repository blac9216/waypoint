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
using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <inheritdoc cref="IRunRetentionHoldRepository"/>
public sealed class RunRetentionHoldRepository : IRunRetentionHoldRepository
{
	private const string PlacedEventType = "retention_hold_placed";
	private const string RemovedEventType = "retention_hold_removed";

	private readonly string _connectionString;

	public RunRetentionHoldRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<RunRetentionHold?> GetAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT run_id, reason, placed_by, placed_at FROM run_retention_holds WHERE run_id = $1", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return Read(reader);
	}

	public async Task<IReadOnlyList<Guid>> ListHeldRunIdsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT run_id FROM run_retention_holds", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<Guid> runIds = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			runIds.Add(reader.GetGuid(0));
		}

		return runIds;
	}

	public async Task<bool> TryInsertAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		int inserted;
		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO run_retention_holds (run_id, reason, placed_by)
			VALUES ($1, $2, $3)
			ON CONFLICT (run_id) DO NOTHING
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue(runId);
			insert.Parameters.AddWithValue(reason);
			insert.Parameters.AddWithValue(actor);
			inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (inserted == 0)
		{
			// Already held -- no-op, no audit row. The caller re-reads via GetAsync to
			// surface the existing hold's own actor/time/reason to the requester.
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return false;
		}

		await WriteAuditAsync(connection, transaction, PlacedEventType, runId, actor, reason, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return true;
	}

	public async Task<bool> TryRemoveAsync(Guid runId, string reason, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		int deleted;
		await using (NpgsqlCommand delete = new("DELETE FROM run_retention_holds WHERE run_id = $1", connection, transaction))
		{
			delete.Parameters.AddWithValue(runId);
			deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (deleted == 0)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return false;
		}

		await WriteAuditAsync(connection, transaction, RemovedEventType, runId, actor, reason, cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return true;
	}

	/// <summary>
	/// Same inline <c>audit_log</c> write idiom <c>TrustRepository</c> already
	/// established for a reasoned Admin action -- <c>audit_log</c> has no dedicated
	/// repository of its own on the write side (see <c>IAuditRepository</c>'s doc
	/// comment: "every writer stays exactly where it already lives").
	/// </summary>
	private static async Task WriteAuditAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, Guid runId, string actor, string reason, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand audit = new(
			"INSERT INTO audit_log (event_type, actor, run_id, detail) VALUES ($1, $2, $3, $4::jsonb)", connection, transaction);
		audit.Parameters.AddWithValue(eventType);
		audit.Parameters.AddWithValue(actor);
		audit.Parameters.AddWithValue(runId);
		audit.Parameters.AddWithValue(JsonSerializer.Serialize(new { reason }));
		await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static RunRetentionHold Read(NpgsqlDataReader reader) => new(
		RunId: reader.GetGuid(0),
		Reason: reader.GetString(1),
		PlacedBy: reader.GetString(2),
		PlacedAt: reader.GetFieldValue<DateTimeOffset>(3));
}
