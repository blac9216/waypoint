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
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;

namespace Waypoint.Infrastructure.Sites;

/// <summary>
/// Storage for target credential bindings (issue #584, migration 0043, ADR-0021):
/// plain Npgsql, no ORM -- same convention as <see cref="TargetRepository"/>. Every
/// write validates purpose applicability (against the target's kind) and credential
/// type compatibility (against the purpose) using the shared, inert
/// <see cref="CredentialPurposeMatrix"/> from #583 -- this class never re-derives or
/// forks that matrix.
///
/// Also owns the dual-write mirror into the legacy <c>targets.credential_id</c> column
/// (migration 0043's documented contract): setting/clearing a target kind's DEFAULT
/// purpose binding (<see cref="CredentialPurposeMatrix.DefaultPurposeByTargetKind"/>)
/// keeps <c>targets.credential_id</c> in lockstep, in the same transaction, so #585's
/// execution resolution lands on consistent data regardless of which surface (legacy
/// <c>credential_ref</c> or the new binding CRUD) an operator used.
/// </summary>
public sealed class TargetCredentialBindingRepository
{
	private const string ProjectionSql = """
		SELECT id, target_id, purpose, credential_id, created_at, updated_at
		FROM target_credential_bindings
		""";

	private readonly string _connectionString;

	public TargetCredentialBindingRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>Every binding for one target, in a stable order (purpose, ascending) -- the shape a target detail response mirrors purpose -&gt; credential ref from.</summary>
	public async Task<IReadOnlyList<TargetCredentialBinding>> ListForTargetAsync(Guid targetId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return await ListForTargetAsync(connection, null, targetId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Every binding for a set of targets in one round trip (list endpoints that would otherwise N+1 per target row).</summary>
	public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TargetCredentialBinding>>> ListForTargetsAsync(
		IReadOnlyCollection<Guid> targetIds, CancellationToken cancellationToken)
	{
		Dictionary<Guid, IReadOnlyList<TargetCredentialBinding>> result = [];
		if (targetIds.Count == 0)
		{
			return result;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE target_id = ANY($1) ORDER BY purpose", connection);
		command.Parameters.AddWithValue(targetIds.ToArray());
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			TargetCredentialBinding binding = Map(reader);
			if (!result.TryGetValue(binding.TargetId, out IReadOnlyList<TargetCredentialBinding>? existing))
			{
				existing = new List<TargetCredentialBinding>();
				result[binding.TargetId] = existing;
			}

			((List<TargetCredentialBinding>)existing).Add(binding);
		}

		return result;
	}

	/// <summary>
	/// Creates or replaces the binding for <c>(targetId, purpose)</c> -- an UPSERT, not
	/// an insert-only create, matching ADR-0021 §4's "override" semantics (a caller
	/// substituting a different credential for a purpose that already has one). Rejects:
	/// unknown target, unknown credential, an invalid/not-applicable purpose for the
	/// target's kind, or a credential type that does not satisfy the purpose.
	/// </summary>
	public async Task<TargetCredentialBindingWriteOutcome> SetAsync(
		Guid targetId, string purpose, Guid credentialId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		(TargetCredentialBindingWriteOutcome validation, string? targetKind, string? credentialType) =
			await ValidateAsync(connection, transaction, targetId, purpose, credentialId, cancellationToken).ConfigureAwait(false);
		if (validation != TargetCredentialBindingWriteOutcome.Ok)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return validation;
		}

		await using (NpgsqlCommand upsert = new(
			"""
			INSERT INTO target_credential_bindings (target_id, purpose, credential_id)
			VALUES ($1, $2, $3)
			ON CONFLICT (target_id, purpose) DO UPDATE SET credential_id = EXCLUDED.credential_id
			""", connection, transaction))
		{
			upsert.Parameters.AddWithValue(targetId);
			upsert.Parameters.AddWithValue(purpose);
			upsert.Parameters.AddWithValue(credentialId);
			await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		if (CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(targetKind!, out string? defaultPurpose)
			&& string.Equals(defaultPurpose, purpose, StringComparison.Ordinal))
		{
			await MirrorLegacyCredentialIdAsync(connection, transaction, targetId, credentialId, cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return TargetCredentialBindingWriteOutcome.Ok;
	}

	/// <summary>Removes the binding for <c>(targetId, purpose)</c>, if present. Mirrors into <c>targets.credential_id</c> (clears it) when <paramref name="purpose"/> is that target kind's default purpose -- the same dual-write contract <see cref="SetAsync"/> documents.</summary>
	public async Task<TargetCredentialBindingDeleteOutcome> ClearAsync(Guid targetId, string purpose, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		Guid? deletedId;
		await using (NpgsqlCommand delete = new(
			"DELETE FROM target_credential_bindings WHERE target_id = $1 AND purpose = $2 RETURNING id", connection, transaction))
		{
			delete.Parameters.AddWithValue(targetId);
			delete.Parameters.AddWithValue(purpose);
			deletedId = (Guid?)await delete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		if (deletedId is null)
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return TargetCredentialBindingDeleteOutcome.NotFound;
		}

		string? targetKind = await GetTargetKindAsync(connection, transaction, targetId, cancellationToken).ConfigureAwait(false);
		if (targetKind is not null
			&& CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(targetKind, out string? defaultPurpose)
			&& string.Equals(defaultPurpose, purpose, StringComparison.Ordinal))
		{
			await MirrorLegacyCredentialIdAsync(connection, transaction, targetId, null, cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return TargetCredentialBindingDeleteOutcome.Deleted;
	}

	private static async Task<(TargetCredentialBindingWriteOutcome Outcome, string? TargetKind, string? CredentialType)> ValidateAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, string purpose, Guid credentialId, CancellationToken cancellationToken)
	{
		if (!CredentialPurposes.IsValid(purpose))
		{
			return (TargetCredentialBindingWriteOutcome.InvalidPurpose, null, null);
		}

		string? targetKind = await GetTargetKindAsync(connection, transaction, targetId, cancellationToken).ConfigureAwait(false);
		if (targetKind is null)
		{
			return (TargetCredentialBindingWriteOutcome.TargetNotFound, null, null);
		}

		if (!CredentialPurposeMatrix.ApplicablePurposes(targetKind).Contains(purpose))
		{
			return (TargetCredentialBindingWriteOutcome.PurposeNotApplicable, targetKind, null);
		}

		string? credentialType;
		await using (NpgsqlCommand credentialLookup = new("SELECT credential_type FROM credentials WHERE id = $1", connection, transaction))
		{
			credentialLookup.Parameters.AddWithValue(credentialId);
			object? result = await credentialLookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				return (TargetCredentialBindingWriteOutcome.CredentialNotFound, targetKind, null);
			}

			credentialType = (string)result;
		}

		if (!CredentialPurposeMatrix.SatisfyingCredentialTypes.TryGetValue(purpose, out IReadOnlyCollection<string>? satisfyingTypes)
			|| !satisfyingTypes.Contains(credentialType))
		{
			return (TargetCredentialBindingWriteOutcome.IncompatibleCredentialType, targetKind, credentialType);
		}

		return (TargetCredentialBindingWriteOutcome.Ok, targetKind, credentialType);
	}

	private static async Task<string?> GetTargetKindAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("SELECT kind FROM targets WHERE id = $1", connection, transaction);
		command.Parameters.AddWithValue(targetId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result as string;
	}

	private static async Task MirrorLegacyCredentialIdAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, Guid? credentialId, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("UPDATE targets SET credential_id = $2 WHERE id = $1", connection, transaction);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<List<TargetCredentialBinding>> ListForTargetAsync(
		NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid targetId, CancellationToken cancellationToken)
	{
		List<TargetCredentialBinding> items = [];
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE target_id = $1 ORDER BY purpose", connection, transaction);
		command.Parameters.AddWithValue(targetId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	private static TargetCredentialBinding Map(NpgsqlDataReader reader)
	{
		return new TargetCredentialBinding(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.GetString(2),
			reader.GetGuid(3),
			reader.GetFieldValue<DateTimeOffset>(4),
			reader.GetFieldValue<DateTimeOffset>(5));
	}
}
