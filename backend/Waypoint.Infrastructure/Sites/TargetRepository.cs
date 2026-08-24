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
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;

namespace Waypoint.Infrastructure.Sites;

/// <summary>
/// Storage for targets (issue #19, epic #13): plain Npgsql, no ORM -- same convention
/// as <see cref="SiteRepository"/>. <c>connection</c> is stored and returned verbatim
/// as JSONB/JSON text; this layer never inspects it for secret-shaped keys -- that
/// validation is the API layer's job (<c>TargetsController</c>), so the repository
/// stays a pure storage seam.
/// </summary>
public sealed class TargetRepository
{
	private const string ProjectionSql = """
		SELECT id, site_id, kind, name, connection::text, credential_id, discovery_status, last_refreshed, created_at, updated_at
		FROM targets
		""";

	private readonly string _connectionString;

	public TargetRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	/// <summary>Lists targets under one site (the `/sites/{id}/targets` route). Null <paramref name="siteId"/> lists across all sites (the `/targets/{id}` sibling routes need no such call, but the flexibility keeps this one method the single list path).</summary>
	public async Task<(IReadOnlyList<Target> Items, long TotalCount)> ListAsync(Guid? siteId, PageRequest page, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		string whereClause = siteId.HasValue ? " WHERE site_id = $1" : string.Empty;

		long total;
		await using (NpgsqlCommand countCommand = new($"SELECT count(*) FROM targets{whereClause}", connection))
		{
			if (siteId.HasValue)
			{
				countCommand.Parameters.AddWithValue(siteId.Value);
			}

			total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<Target> items = [];
		int limitIndex = siteId.HasValue ? 2 : 1;
		string listSql = $"{ProjectionSql}{whereClause} ORDER BY name LIMIT ${limitIndex} OFFSET ${limitIndex + 1}";
		await using (NpgsqlCommand listCommand = new(listSql, connection))
		{
			if (siteId.HasValue)
			{
				listCommand.Parameters.AddWithValue(siteId.Value);
			}

			listCommand.Parameters.AddWithValue(page.Limit);
			listCommand.Parameters.AddWithValue(page.Offset);
			await using NpgsqlDataReader reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(Map(reader));
			}
		}

		return (items, total);
	}

	/// <summary>
	/// All targets under one site, unpaginated (issue #279): the scan fan-out needs
	/// every target to build one job per row -- <see cref="ListAsync"/>'s <see cref="PageRequest"/>
	/// clamps to <c>MaxLimit</c> (200), which silently truncated a full-site scan on
	/// any site past that count. A site's target count is bounded by what an operator
	/// can realistically stand up (M1 fixtures/lab: low hundreds), so one unpaginated
	/// query is the right shape here rather than a paged fetch loop -- this method
	/// exists precisely because the fan-out is one of the few callers that genuinely
	/// needs "all rows", unlike the paginated `/sites/{id}/targets` listing endpoint.
	/// </summary>
	public async Task<IReadOnlyList<Target>> ListAllForSiteAsync(Guid siteId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		List<Target> items = [];
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE site_id = $1 ORDER BY name", connection);
		command.Parameters.AddWithValue(siteId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return items;
	}

	public async Task<Target?> GetAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>
	/// Issue #245: the first (oldest) target that references <paramref name="credentialId"/>,
	/// used to resolve a host to dial for <c>POST /credentials/{id}/test</c> -- a
	/// credential has no host of its own (domain-model.md: "One global service
	/// account" is the degenerate case of many targets sharing one credential), so
	/// the connectivity job borrows the host from whichever target the credential is
	/// actually wired to today. Null when no target references it -- the caller
	/// reports that as a clear failure rather than guessing a host.
	/// </summary>
	public async Task<Target?> FindFirstByCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"{ProjectionSql} WHERE credential_id = $1 ORDER BY created_at ASC LIMIT 1", connection);
		command.Parameters.AddWithValue(credentialId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	/// <summary>
	/// Creates a target under a site. Returns the outcome discriminated union rather
	/// than throwing for the two known conflict shapes (site missing, name taken within
	/// the site) so the controller maps each to its documented status code.
	///
	/// Issue #584/migration 0043 dual-write: a non-null <paramref name="credentialId"/>
	/// also upserts a binding for the target kind's DEFAULT purpose
	/// (<c>CredentialPurposeMatrix.DefaultPurposeByTargetKind</c>) to the same
	/// credential, in the same transaction as the insert -- the legacy
	/// <c>credential_ref</c> surface and the new binding CRUD stay consistent for that
	/// one purpose regardless of which surface an operator used (migration 0043's
	/// documented dual-write contract).
	/// </summary>
	public async Task<(TargetWriteOutcome Outcome, Guid? Id)> CreateAsync(
		Guid siteId, string kind, string name, string connectionJson, Guid? credentialId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand siteCheck = new("SELECT 1 FROM sites WHERE id = $1", connection, transaction))
		{
			siteCheck.Parameters.AddWithValue(siteId);
			if (await siteCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return (TargetWriteOutcome.SiteNotFound, null);
			}
		}

		Guid id;
		await using (NpgsqlCommand command = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, credential_id)
			VALUES ($1, $2, $3, $4::jsonb, $5)
			ON CONFLICT (site_id, name) DO NOTHING
			RETURNING id
			""", connection, transaction))
		{
			command.Parameters.AddWithValue(siteId);
			command.Parameters.AddWithValue(kind);
			command.Parameters.AddWithValue(name);
			command.Parameters.AddWithValue(connectionJson);
			command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);

			try
			{
				object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
				if (result is not Guid createdId)
				{
					await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
					return (TargetWriteOutcome.NameTaken, null);
				}

				id = createdId;
			}
			catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return (TargetWriteOutcome.CredentialNotFound, null);
			}
		}

		if (credentialId.HasValue && CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(kind, out string? defaultPurpose))
		{
			await MirrorIfCompatibleAsync(connection, transaction, id, defaultPurpose, credentialId.Value, cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return (TargetWriteOutcome.Ok, id);
	}

	/// <summary>
	/// Full replacement update: a null argument leaves the corresponding column
	/// unchanged. Same dual-write contract as <see cref="CreateAsync"/>: a supplied
	/// <paramref name="credentialId"/> or <paramref name="clearCredential"/> mirrors
	/// into the (possibly-updated) kind's default-purpose binding.
	/// </summary>
	public async Task<TargetWriteOutcome> UpdateAsync(
		Guid id, string? kind, string? name, string? connectionJson, Guid? credentialId, bool clearCredential, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string resolvedKind;
		await using (NpgsqlCommand command = new(
			"""
			UPDATE targets SET
				kind = COALESCE($2, kind),
				name = COALESCE($3, name),
				connection = COALESCE($4::jsonb, connection),
				credential_id = CASE WHEN $6 THEN NULL ELSE COALESCE($5, credential_id) END
			WHERE id = $1
			RETURNING kind
			""", connection, transaction))
		{
			command.Parameters.AddWithValue(id);
			command.Parameters.AddWithValue((object?)kind ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)connectionJson ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)credentialId ?? DBNull.Value);
			command.Parameters.AddWithValue(clearCredential);

			try
			{
				object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
				if (result is not string kindResult)
				{
					await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
					return TargetWriteOutcome.NotFound;
				}

				resolvedKind = kindResult;
			}
			catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return TargetWriteOutcome.NameTaken;
			}
			catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				return TargetWriteOutcome.CredentialNotFound;
			}
		}

		if (CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(resolvedKind, out string? defaultPurpose))
		{
			if (clearCredential)
			{
				await using NpgsqlCommand deleteBinding = new(
					"DELETE FROM target_credential_bindings WHERE target_id = $1 AND purpose = $2", connection, transaction);
				deleteBinding.Parameters.AddWithValue(id);
				deleteBinding.Parameters.AddWithValue(defaultPurpose);
				await deleteBinding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
			else if (credentialId.HasValue)
			{
				await MirrorIfCompatibleAsync(connection, transaction, id, defaultPurpose, credentialId.Value, cancellationToken).ConfigureAwait(false);
			}
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return TargetWriteOutcome.Ok;
	}

	/// <summary>
	/// Mirrors <paramref name="credentialId"/> into the target kind's default-purpose
	/// binding ONLY when the credential's type actually satisfies that purpose
	/// (<c>CredentialPurposeMatrix.SatisfyingCredentialTypes</c>). The legacy
	/// <c>credential_ref</c> write path (this class's whole reason to still exist,
	/// pending #585) has never validated credential type -- an operator could always
	/// point a target at a <c>token</c>-type credential, and that must keep working
	/// unchanged (issue #584 scope: "the run/job path must be BEHAVIOR-UNCHANGED").
	/// Silently skipping the mirror on a type mismatch (rather than rejecting the
	/// legacy write) preserves that: the binding table simply has no default-purpose
	/// row for a target whose legacy credential is not purpose-compatible, which is a
	/// true statement -- that credential could never satisfy the purpose regardless of
	/// which surface named it.
	/// </summary>
	private static async Task MirrorIfCompatibleAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, string purpose, Guid credentialId, CancellationToken cancellationToken)
	{
		string? credentialType;
		await using (NpgsqlCommand lookup = new("SELECT credential_type FROM credentials WHERE id = $1", connection, transaction))
		{
			lookup.Parameters.AddWithValue(credentialId);
			credentialType = (string?)await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		}

		if (credentialType is null
			|| !CredentialPurposeMatrix.SatisfyingCredentialTypes.TryGetValue(purpose, out IReadOnlyCollection<string>? satisfyingTypes)
			|| !satisfyingTypes.Contains(credentialType))
		{
			return;
		}

		await using NpgsqlCommand upsert = new(
			"""
			INSERT INTO target_credential_bindings (target_id, purpose, credential_id)
			VALUES ($1, $2, $3)
			ON CONFLICT (target_id, purpose) DO UPDATE SET credential_id = EXCLUDED.credential_id
			""", connection, transaction);
		upsert.Parameters.AddWithValue(targetId);
		upsert.Parameters.AddWithValue(purpose);
		upsert.Parameters.AddWithValue(credentialId);
		await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<TargetDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("DELETE FROM targets WHERE id = $1 RETURNING id", connection);
		command.Parameters.AddWithValue(id);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is not null ? TargetDeleteOutcome.Deleted : TargetDeleteOutcome.NotFound;
	}

	/// <summary>
	/// Updates a target's <c>discovery_status</c> (issue #21's <c>discover</c> job
	/// handler drives this: discovering -&gt; discovered|failed) and, when
	/// <paramref name="stampLastRefreshed"/> is true, <c>last_refreshed</c> to now --
	/// the staleness clock <see cref="Waypoint.Core.Discovery.DiscoveryOptions"/>
	/// measures against. A transition into <c>discovering</c> does not stamp
	/// <c>last_refreshed</c> (the refresh is not complete yet); the terminal
	/// discovered/failed transition does.
	/// </summary>
	public async Task<bool> SetDiscoveryStatusAsync(Guid id, string discoveryStatus, bool stampLastRefreshed, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(discoveryStatus);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			$"""
			UPDATE targets SET
				discovery_status = $2,
				last_refreshed = {(stampLastRefreshed ? "now()" : "last_refreshed")}
			WHERE id = $1
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(id);
		command.Parameters.AddWithValue(discoveryStatus);
		return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
	}

	private static Target Map(NpgsqlDataReader reader)
	{
		return new Target(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetGuid(5),
			reader.GetString(6),
			reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
			reader.GetFieldValue<DateTimeOffset>(8),
			reader.GetFieldValue<DateTimeOffset>(9));
	}
}
