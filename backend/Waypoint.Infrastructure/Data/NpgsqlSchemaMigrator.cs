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

using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Waypoint.Infrastructure.Data;

/// <summary>
/// A small, hand-rolled migration runner over the embedded <c>Data/Migrations/*.sql</c>
/// files — not EF Core's generated-migration model. The job queue's claim query
/// (ADR-0008) is raw SQL by nature, so a raw-SQL migrations pipeline keeps one
/// consistent data-access story rather than mixing an ORM in for schema only.
///
/// Tracking: a <c>schema_migrations(version, applied_at)</c> table records which
/// embedded files have already run; <see cref="ApplyAsync"/> skips anything already
/// recorded, which is what makes re-running it against an already-migrated database a
/// no-op (the acceptance-criteria "idempotent on an existing database" requirement).
/// Each embedded file is additionally written to be idempotent on its own (IF NOT
/// EXISTS / OR REPLACE / ON CONFLICT throughout) as defense in depth beyond the
/// tracking table.
///
/// A Postgres session-level advisory lock serializes concurrent callers (e.g. two
/// backend replicas starting at once) so they do not race to apply the same pending
/// migration twice.
/// </summary>
public sealed partial class NpgsqlSchemaMigrator : ISchemaMigrator
{
	// Arbitrary constant identifying this application's migration advisory lock.
	// Any int64 works; it only has to be stable and distinct from other advisory-lock
	// users of the same database.
	private const long AdvisoryLockKey = 875_190_001L;

	private readonly string _connectionString;
	private readonly ILogger<NpgsqlSchemaMigrator> _logger;
	private readonly Assembly _migrationsAssembly;

	public NpgsqlSchemaMigrator(string connectionString, ILogger<NpgsqlSchemaMigrator> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_logger = logger;
		_migrationsAssembly = typeof(NpgsqlSchemaMigrator).Assembly;
	}

	public async Task ApplyAsync(CancellationToken cancellationToken = default)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_lock($1)", connection))
		{
			// Unbounded, like the migration command below: a second instance blocked
			// behind another instance's in-progress migration must wait for the lock
			// to be released, not time out at Npgsql's default 30s CommandTimeout and
			// crash-loop (issue #108).
			lockCommand.CommandTimeout = 0;
			lockCommand.Parameters.AddWithValue(AdvisoryLockKey);
			await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		try
		{
			await EnsureMigrationsTableAsync(connection, cancellationToken).ConfigureAwait(false);

			HashSet<string> appliedVersions = await GetAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);

			foreach ((string version, string resourceName) in GetOrderedMigrations())
			{
				if (appliedVersions.Contains(version))
				{
					LogMigrationAlreadyApplied(version);
					continue;
				}

				LogApplyingMigration(version);
				string sql = await ReadMigrationSqlAsync(resourceName, cancellationToken).ConfigureAwait(false);

				await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

				await using (NpgsqlCommand migrationCommand = new(sql, connection, transaction))
				{
					migrationCommand.CommandTimeout = 0;
					await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				}

				await using (NpgsqlCommand recordCommand = new(
					"INSERT INTO schema_migrations (version) VALUES ($1)", connection, transaction))
				{
					recordCommand.Parameters.AddWithValue(version);
					await recordCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				}

				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				LogAppliedMigration(version);
			}
		}
		finally
		{
			await using NpgsqlCommand unlockCommand = new("SELECT pg_advisory_unlock($1)", connection);
			unlockCommand.Parameters.AddWithValue(AdvisoryLockKey);
			await unlockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task EnsureMigrationsTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
	{
		const string sql = """
			CREATE TABLE IF NOT EXISTS schema_migrations (
				version TEXT PRIMARY KEY,
				applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
			);
			""";

		await using NpgsqlCommand command = new(sql, connection);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<HashSet<string>> GetAppliedVersionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
	{
		HashSet<string> versions = [];

		await using NpgsqlCommand command = new("SELECT version FROM schema_migrations", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			versions.Add(reader.GetString(0));
		}

		return versions;
	}

	/// <summary>
	/// Enumerates the embedded <c>Data/Migrations/*.sql</c> resources as
	/// (version, resourceName) pairs, ordered by version. The zero-padded numeric
	/// filename prefix (<c>0001_...</c>) makes ordinal string ordering the same as
	/// migration order.
	/// </summary>
	private IEnumerable<(string Version, string ResourceName)> GetOrderedMigrations()
	{
		const string suffix = ".sql";

		return _migrationsAssembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
			.Select(name => (Version: ExtractVersion(name), ResourceName: name))
			.OrderBy(migration => migration.Version, StringComparer.Ordinal);
	}

	private static string ExtractVersion(string resourceName)
	{
		int lastDot = resourceName.LastIndexOf('.', resourceName.Length - ".sql".Length - 1);
		string withoutExtension = resourceName[..^".sql".Length];
		return lastDot < 0 ? withoutExtension : withoutExtension[(lastDot + 1)..];
	}

	private async Task<string> ReadMigrationSqlAsync(string resourceName, CancellationToken cancellationToken)
	{
		await using Stream? stream = _migrationsAssembly.GetManifestResourceStream(resourceName);
		if (stream is null)
		{
			throw new InvalidOperationException(
				string.Format(CultureInfo.InvariantCulture, "Embedded migration resource '{0}' could not be loaded.", resourceName));
		}

		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Migration {Version} already applied, skipping")]
	private partial void LogMigrationAlreadyApplied(string version);

	[LoggerMessage(Level = LogLevel.Information, Message = "Applying migration {Version}")]
	private partial void LogApplyingMigration(string version);

	[LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Version}")]
	private partial void LogAppliedMigration(string version);
}
