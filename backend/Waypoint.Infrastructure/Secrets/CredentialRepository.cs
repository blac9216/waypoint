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

	/// <summary>Jobs or runs still reference the credential; deletion would erase execution history's attribution.</summary>
	InUse,
}

public sealed class CredentialRepository
{
	private const string ProjectionSql = """
		SELECT c.id, c.name, c.credential_type, c.owner, c.health,
			EXISTS (SELECT 1 FROM credential_secrets s WHERE s.credential_id = c.id) AS has_secret,
			(SELECT count(*) FROM jobs j WHERE j.credential_id = c.id) AS used_by_job_count,
			c.rotated_at, c.created_at, c.updated_at
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

	/// <summary>Creates the metadata row. Returns null when the name is already taken (the caller maps that to a 409).</summary>
	public async Task<Guid?> CreateAsync(string name, string credentialType, string owner, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO credentials (name, credential_type, owner)
			VALUES ($1, $2, $3)
			ON CONFLICT (name) DO NOTHING
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue(credentialType);
		command.Parameters.AddWithValue(owner);
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
	/// Deletes the credential; <c>credential_secrets</c> follows via ON DELETE CASCADE,
	/// and existing audit rows survive with their <c>credential_id</c> nulled
	/// (migration 0006). The deletion itself is audited first, in the same
	/// transaction, with the identity captured in detail JSON -- after the FK nulls
	/// out, that JSON is the surviving attribution.
	/// </summary>
	public async Task<CredentialDeleteOutcome> DeleteAsync(Guid id, string actor, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string? name;
		await using (NpgsqlCommand read = new("SELECT name FROM credentials WHERE id = $1", connection, transaction))
		{
			read.Parameters.AddWithValue(id);
			name = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
		}

		if (name is null)
		{
			return CredentialDeleteOutcome.NotFound;
		}

		await using (NpgsqlCommand audit = new(
			"INSERT INTO audit_log (event_type, actor, credential_id, detail) VALUES ('credential.deleted', $1, $2, $3::jsonb)",
			connection, transaction))
		{
			audit.Parameters.AddWithValue(actor);
			audit.Parameters.AddWithValue(id);
			audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new { credential_id = id, name }));
			await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		try
		{
			await using NpgsqlCommand delete = new("DELETE FROM credentials WHERE id = $1", connection, transaction);
			delete.Parameters.AddWithValue(id);
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
		{
			// PR #187 round 1, finding 1: jobs/runs keep plain RESTRICT FKs on
			// purpose -- nulling job history would erode the auth-halt window and
			// run attribution. A referenced credential is therefore not deletable;
			// the caller maps this to 409 credential_in_use. The transaction rolls
			// back, taking the pre-written audit row with it.
			return CredentialDeleteOutcome.InUse;
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return CredentialDeleteOutcome.Deleted;
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
			reader.GetInt64(6),
			reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
			reader.GetFieldValue<DateTimeOffset>(8),
			reader.GetFieldValue<DateTimeOffset>(9));
	}
}
