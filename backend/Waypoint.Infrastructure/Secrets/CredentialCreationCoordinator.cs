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

using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// See <see cref="ICredentialCreationCoordinator"/> for the contract issue #188 fixes.
/// This is the only place that opens the connection/transaction shared by
/// <see cref="CredentialRepository"/>'s metadata insert and
/// <see cref="CredentialSecretStore"/>'s secret store -- both classes expose
/// connection/transaction-scoped internal cores for exactly this composition,
/// mirroring how <c>ConfigDocRepository.SaveAsync</c> composes slot-creation and
/// the first version insert (issue #270), just across two classes instead of one.
/// </summary>
public sealed partial class CredentialCreationCoordinator : ICredentialCreationCoordinator
{
	private readonly string _connectionString;
	private readonly IEnvelopeCipher _cipher;
	private readonly ILogger<CredentialCreationCoordinator> _logger;

	public CredentialCreationCoordinator(string connectionString, IEnvelopeCipher cipher, ILogger<CredentialCreationCoordinator> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(cipher);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_cipher = cipher;
		_logger = logger;
	}

	public async Task<Guid?> CreateAsync(
		string name, string credentialType, string owner, bool sudoEnabled, string? username,
		byte[]? secretValue, string actor, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		Guid? id = await CredentialRepository.CreateAsync(
			connection, transaction, name, credentialType, owner, sudoEnabled, username, cancellationToken).ConfigureAwait(false);
		if (id is not Guid createdId)
		{
			// Name already taken -- nothing to roll back beyond the no-op transaction;
			// disposing without committing is a harmless rollback of an empty transaction.
			return null;
		}

		if (secretValue is { Length: > 0 })
		{
			// A failure here (bad master key, DB blip) throws out of this method before
			// CommitAsync runs -- disposing the transaction without committing rolls back
			// the metadata INSERT above along with anything the secret store already
			// staged, so no orphan row (has_secret=false) survives for a retry to collide
			// with (issue #188).
			await CredentialSecretStore.StoreAsync(
				connection, transaction, createdId, secretValue, actor, _cipher, cancellationToken).ConfigureAwait(false);
			await CredentialRepository.StampRotatedAsync(connection, transaction, createdId, cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		LogCredentialCreated(createdId, actor, secretValue is { Length: > 0 });
		return createdId;
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Credential {CredentialId} created by {Actor} (with secret: {WithSecret})")]
	private partial void LogCredentialCreated(Guid credentialId, string actor, bool withSecret);
}
