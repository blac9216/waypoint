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

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// Storage for repo credential bindings (issue #1517, migration 0103): plain Npgsql,
/// no ORM -- same convention as
/// <see cref="Waypoint.Infrastructure.Sites.TargetCredentialBindingRepository"/>, which
/// this class deliberately mirrors closely (per issue #1517's own "a reviewer familiar
/// with that code recognizes the pattern immediately" note). Every write validates the
/// store (against <see cref="RepoStores"/>) and the credential's type (against
/// <see cref="CredentialTypes.RepoBasicAuth"/>) before touching the row -- the same
/// "closed-set + type-compatibility" shape, simplified because this bounded context has
/// exactly one purpose (Basic-auth repo serving), not a matrix of them.
/// </summary>
public sealed class RepoCredentialBindingRepository
{
	private const string ProjectionSql = """
		SELECT id, store, credential_id, created_at, updated_at
		FROM repo_credential_bindings
		""";

	private readonly string _connectionString;

	public RepoCredentialBindingRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>Every store's current binding, in a stable order (store, ascending). Stores with no binding yet are simply absent -- there is no placeholder row.</summary>
	public async Task<IReadOnlyList<RepoCredentialBinding>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} ORDER BY store", connection);
		List<RepoCredentialBinding> items = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	/// <summary>The binding for one store, or null when that store has none.</summary>
	public async Task<RepoCredentialBinding?> GetAsync(string store, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE store = $1", connection);
		command.Parameters.AddWithValue(store);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>
	/// Creates or replaces the binding for <paramref name="store"/> -- an UPSERT, not an
	/// insert-only create (the same "override" semantics
	/// <see cref="Waypoint.Infrastructure.Sites.TargetCredentialBindingRepository.SetAsync"/>
	/// documents). Rejects an invalid store, an unknown credential, or a credential
	/// whose type is not <see cref="CredentialTypes.RepoBasicAuth"/>.
	/// </summary>
	public async Task<RepoCredentialBindingWriteOutcome> SetAsync(string store, Guid credentialId, CancellationToken cancellationToken)
	{
		if (!RepoStores.IsValid(store))
		{
			return RepoCredentialBindingWriteOutcome.InvalidStore;
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string? credentialType;
		await using (NpgsqlCommand lookup = new("SELECT credential_type FROM credentials WHERE id = $1", connection, transaction))
		{
			lookup.Parameters.AddWithValue(credentialId);
			object? result = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return RepoCredentialBindingWriteOutcome.CredentialNotFound;
			}

			credentialType = (string)result;
		}

		if (!string.Equals(credentialType, CredentialTypes.RepoBasicAuth, StringComparison.Ordinal))
		{
			await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
			return RepoCredentialBindingWriteOutcome.IncompatibleCredentialType;
		}

		await using (NpgsqlCommand upsert = new(
			"""
			INSERT INTO repo_credential_bindings (store, credential_id)
			VALUES ($1, $2)
			ON CONFLICT (store) DO UPDATE SET credential_id = EXCLUDED.credential_id
			""", connection, transaction))
		{
			upsert.Parameters.AddWithValue(store);
			upsert.Parameters.AddWithValue(credentialId);
			await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return RepoCredentialBindingWriteOutcome.Ok;
	}

	/// <summary>Removes the binding for <paramref name="store"/>, if present.</summary>
	public async Task<RepoCredentialBindingDeleteOutcome> ClearAsync(string store, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand delete = new("DELETE FROM repo_credential_bindings WHERE store = $1 RETURNING id", connection);
		delete.Parameters.AddWithValue(store);
		object? deletedId = await delete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return deletedId is null ? RepoCredentialBindingDeleteOutcome.NotFound : RepoCredentialBindingDeleteOutcome.Deleted;
	}

	private static RepoCredentialBinding Map(NpgsqlDataReader reader)
	{
		return new RepoCredentialBinding(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetGuid(2),
			reader.GetFieldValue<DateTimeOffset>(3),
			reader.GetFieldValue<DateTimeOffset>(4));
	}
}
