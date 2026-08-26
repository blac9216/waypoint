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
using Waypoint.Core.Serialization;
using Waypoint.Core.Trust;

namespace Waypoint.Infrastructure.Trust;

/// <inheritdoc cref="ITrustRepository"/>
public sealed class TrustRepository : ITrustRepository
{
	private const string BundleProjectionSql =
		"SELECT id, label, pem_chain, subject, issuer, fingerprint_sha256, not_before, not_after, status, superseded_by_id, superseded_at, uploaded_by, created_at FROM trust_bundles";

	private const string PolicyProjectionSql =
		"SELECT id, scope_type, scope_id, mode, trust_bundle_id, bypass_reason, status, superseded_at, actor, created_at FROM trust_policies";

	private readonly string _connectionString;

	public TrustRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<TrustBundle?> FindActiveByFingerprintAsync(string fingerprintSha256, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintSha256);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			BundleProjectionSql + " WHERE fingerprint_sha256 = $1 AND status = 'active'", connection);
		command.Parameters.AddWithValue(fingerprintSha256);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBundle(reader) : null;
	}

	public async Task<TrustBundle> CreateAsync(
		string label, string pemChain, string subject, string issuer, string fingerprintSha256,
		DateTimeOffset notBefore, DateTimeOffset notAfter, string uploadedBy, Guid? supersedesId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(label);
		ArgumentException.ThrowIfNullOrWhiteSpace(pemChain);
		ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintSha256);
		ArgumentException.ThrowIfNullOrWhiteSpace(uploadedBy);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		TrustBundle created;
		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO trust_bundles (label, pem_chain, subject, issuer, fingerprint_sha256, not_before, not_after, uploaded_by)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
			RETURNING id, label, pem_chain, subject, issuer, fingerprint_sha256, not_before, not_after, status, superseded_by_id, superseded_at, uploaded_by, created_at
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue(label);
			insert.Parameters.AddWithValue(pemChain);
			insert.Parameters.AddWithValue(subject);
			insert.Parameters.AddWithValue(issuer);
			insert.Parameters.AddWithValue(fingerprintSha256);
			insert.Parameters.AddWithValue(notBefore);
			insert.Parameters.AddWithValue(notAfter);
			insert.Parameters.AddWithValue(uploadedBy);

			await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			created = ReadBundle(reader);
		}

		if (supersedesId is Guid oldId)
		{
			await using NpgsqlCommand supersede = new(
				"UPDATE trust_bundles SET status = 'superseded', superseded_by_id = $2, superseded_at = now() WHERE id = $1 AND status = 'active'",
				connection, transaction);
			supersede.Parameters.AddWithValue(oldId);
			supersede.Parameters.AddWithValue(created.Id);
			await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await InsertAuditAsync(
			connection, transaction, "trust_bundle.uploaded", uploadedBy,
			new { bundle_id = created.Id, fingerprint_sha256 = fingerprintSha256, subject, supersedes = supersedesId },
			cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return created;
	}

	public async Task<TrustBundle?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(BundleProjectionSql + " WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBundle(reader) : null;
	}

	public async Task<IReadOnlyList<TrustBundle>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(BundleProjectionSql + " ORDER BY created_at DESC", connection);

		List<TrustBundle> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(ReadBundle(reader));
		}

		return results;
	}

	public async Task<TrustBundleDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand existsCheck = new("SELECT 1 FROM trust_bundles WHERE id = $1", connection, transaction))
		{
			existsCheck.Parameters.AddWithValue(id);
			object? exists = await existsCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (exists is null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return TrustBundleDeleteOutcome.NotFound;
			}
		}

		await using (NpgsqlCommand referencedCheck = new("SELECT 1 FROM trust_policies WHERE trust_bundle_id = $1 LIMIT 1", connection, transaction))
		{
			referencedCheck.Parameters.AddWithValue(id);
			object? referenced = await referencedCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (referenced is not null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return TrustBundleDeleteOutcome.Referenced;
			}
		}

		await using (NpgsqlCommand delete = new("DELETE FROM trust_bundles WHERE id = $1", connection, transaction))
		{
			delete.Parameters.AddWithValue(id);
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return TrustBundleDeleteOutcome.Deleted;
	}

	public async Task<TrustPolicy?> GetCurrentPolicyAsync(string scopeType, string scopeId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeType);
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			PolicyProjectionSql + " WHERE scope_type = $1 AND scope_id = $2 AND status = 'current'", connection);
		command.Parameters.AddWithValue(scopeType);
		command.Parameters.AddWithValue(scopeId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader) : null;
	}

	public async Task<TrustPolicy?> GetPolicyAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(PolicyProjectionSql + " WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader) : null;
	}

	public async Task<IReadOnlyList<TrustPolicy>> ListPoliciesAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(PolicyProjectionSql + " ORDER BY created_at DESC", connection);

		List<TrustPolicy> results = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			results.Add(ReadPolicy(reader));
		}

		return results;
	}

	public async Task<(TrustPolicyWriteOutcome Outcome, TrustPolicy? Policy)> SetPolicyAsync(
		string scopeType, string scopeId, string mode, Guid? trustBundleId, string? bypassReason, string actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeType);
		ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
		ArgumentException.ThrowIfNullOrWhiteSpace(mode);
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		if (mode == TrustPolicyModes.Bundle)
		{
			await using NpgsqlCommand bundleCheck = new("SELECT status FROM trust_bundles WHERE id = $1 FOR UPDATE", connection, transaction);
			bundleCheck.Parameters.AddWithValue(trustBundleId!.Value);
			object? status = await bundleCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (status is not string statusText)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return (TrustPolicyWriteOutcome.TrustBundleNotFound, null);
			}

			if (statusText != TrustBundleStatuses.Active)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return (TrustPolicyWriteOutcome.TrustBundleSuperseded, null);
			}
		}

		await using (NpgsqlCommand supersede = new(
			"UPDATE trust_policies SET status = 'superseded', superseded_at = now() WHERE scope_type = $1 AND scope_id = $2 AND status = 'current'",
			connection, transaction))
		{
			supersede.Parameters.AddWithValue(scopeType);
			supersede.Parameters.AddWithValue(scopeId);
			await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		TrustPolicy created;
		await using (NpgsqlCommand insert = new(
			"""
			INSERT INTO trust_policies (scope_type, scope_id, mode, trust_bundle_id, bypass_reason, actor)
			VALUES ($1, $2, $3, $4, $5, $6)
			RETURNING id, scope_type, scope_id, mode, trust_bundle_id, bypass_reason, status, superseded_at, actor, created_at
			""", connection, transaction))
		{
			insert.Parameters.AddWithValue(scopeType);
			insert.Parameters.AddWithValue(scopeId);
			insert.Parameters.AddWithValue(mode);
			insert.Parameters.AddWithValue((object?)trustBundleId ?? DBNull.Value);
			insert.Parameters.AddWithValue((object?)bypassReason ?? DBNull.Value);
			insert.Parameters.AddWithValue(actor);

			await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			created = ReadPolicy(reader);
		}

		string eventType = mode == TrustPolicyModes.Bypass ? "trust_policy.bypass_authorized" : "trust_policy.bundle_bound";
		await InsertAuditAsync(
			connection, transaction, eventType, actor,
			new
			{
				policy_id = created.Id,
				scope_type = scopeType,
				scope_id = scopeId,
				mode,
				trust_bundle_id = trustBundleId,
				bypass_reason = bypassReason,
			},
			cancellationToken).ConfigureAwait(false);

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return (TrustPolicyWriteOutcome.Written, created);
	}

	/// <summary>
	/// Shared audit-insert helper mirroring <c>CredentialRepository</c>/<c>JobQueueRepository</c>'s
	/// inline <c>audit_log</c> write idiom -- <c>audit_log</c> has no
	/// <c>trust_bundle_id</c>/<c>trust_policy_id</c> column (migration 0001 fixed its FK
	/// set to credential/job/run), so every non-secret fact this slice's ADR-0025 audit
	/// table (docs/security.md) requires (bundle id, fingerprint, scope, mode, reason,
	/// actor, time) travels in <c>detail</c> JSONB instead, exactly like every other
	/// audited decision this codebase has that predates a dedicated FK column.
	/// </summary>
	private static async Task InsertAuditAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, string actor, object detail,
		CancellationToken cancellationToken)
	{
		await using NpgsqlCommand audit = new(
			"INSERT INTO audit_log (event_type, actor, detail) VALUES ($1, $2, $3::jsonb)", connection, transaction);
		audit.Parameters.AddWithValue(eventType);
		audit.Parameters.AddWithValue(actor);
		audit.Parameters.AddWithValue(JsonSerializer.Serialize(detail, WaypointJsonOptions.Default));
		await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static TrustBundle ReadBundle(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.GetString(3),
		reader.GetString(4),
		reader.GetString(5),
		reader.GetFieldValue<DateTimeOffset>(6),
		reader.GetFieldValue<DateTimeOffset>(7),
		reader.GetString(8),
		reader.IsDBNull(9) ? null : reader.GetGuid(9),
		reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
		reader.GetString(11),
		reader.GetFieldValue<DateTimeOffset>(12));

	private static TrustPolicy ReadPolicy(NpgsqlDataReader reader) => new(
		reader.GetGuid(0),
		reader.GetString(1),
		reader.GetString(2),
		reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetGuid(4),
		reader.IsDBNull(5) ? null : reader.GetString(5),
		reader.GetString(6),
		reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
		reader.GetString(8),
		reader.GetFieldValue<DateTimeOffset>(9));

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return connection;
	}
}
