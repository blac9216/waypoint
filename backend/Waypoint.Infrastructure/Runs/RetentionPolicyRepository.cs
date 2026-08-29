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

/// <inheritdoc cref="IRetentionPolicyRepository"/>
public sealed class RetentionPolicyRepository : IRetentionPolicyRepository
{
	private readonly string _connectionString;

	public RetentionPolicyRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<RetentionPolicy?> GetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT evidence_retention_days, updated_by, updated_at FROM retention_policy WHERE id = 1", connection);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return Read(reader);
	}

	public async Task<RetentionPolicy> SetAsync(int evidenceRetentionDays, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);
		if (evidenceRetentionDays <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(evidenceRetentionDays), evidenceRetentionDays, "Retention period must be a positive number of days.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Issue #1109: read the pre-change value inside the same transaction (FOR
		// UPDATE holds the row lock across the audit write below) so the audit_log
		// row records a genuine old->new pair rather than racing a concurrent PUT.
		int previousDays;
		await using (NpgsqlCommand select = new("SELECT evidence_retention_days FROM retention_policy WHERE id = 1 FOR UPDATE", connection, transaction))
		{
			object? existing = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (existing is null)
			{
				// Migration 0078 seeds id=1 unconditionally and nothing deletes it --
				// see this method's own contract on IRetentionPolicyRepository.GetAsync.
				throw new InvalidOperationException("retention_policy singleton row (id=1) is missing; cannot update it.");
			}

			previousDays = (int)existing;
		}

		RetentionPolicy updated;
		await using (NpgsqlCommand update = new(
			"""
			UPDATE retention_policy
			SET evidence_retention_days = $1, updated_by = $2
			WHERE id = 1
			RETURNING evidence_retention_days, updated_by, updated_at
			""", connection, transaction))
		{
			update.Parameters.AddWithValue(evidenceRetentionDays);
			update.Parameters.AddWithValue(actor);

			await using NpgsqlDataReader reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			updated = Read(reader);
		}

		// Issue #1109: every PUT writes an audit_log row -- including a no-op that
		// resubmits the current value, which is recorded honestly via `changed:
		// false` rather than silently dropped or misreported as a real change. Same
		// inline-INSERT idiom TrustRepository/RunRetentionHoldRepository already use;
		// audit_log has no evidence_retention_days-shaped FK column, so both values
		// travel in `detail` JSONB.
		await using (NpgsqlCommand audit = new(
			"INSERT INTO audit_log (event_type, actor, detail) VALUES ('retention_policy.updated', $1, $2::jsonb)", connection, transaction))
		{
			audit.Parameters.AddWithValue(actor);
			audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
			{
				previous_evidence_retention_days = previousDays,
				new_evidence_retention_days = evidenceRetentionDays,
				changed = previousDays != evidenceRetentionDays,
			}));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return updated;
	}

	private static RetentionPolicy Read(NpgsqlDataReader reader) => new(
		EvidenceRetentionDays: reader.GetInt32(0),
		UpdatedBy: reader.IsDBNull(1) ? null : reader.GetString(1),
		UpdatedAt: reader.GetFieldValue<DateTimeOffset>(2));
}
