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

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Runs the real migrations pipeline against a real, disposable PostgreSQL 16
/// container (see <see cref="PostgresFixture"/>) — the acceptance criteria this
/// covers only mean something proven against the real engine (partial indexes,
/// <c>GENERATED ALWAYS AS IDENTITY</c>, <c>CREATE OR REPLACE TRIGGER</c> are all
/// Postgres-specific and have no meaningful fake).
/// </summary>
[Collection("Postgres")]
public sealed class SchemaMigrationTests
{
	private static readonly string[] ExpectedTables =
	[
		"credentials",
		"credential_secrets",
		"runs",
		"jobs",
		"job_events",
		"depot_artifacts",
		"downloads",
		"audit_log",
		"appliance_state",
		"schema_migrations"
	];

	private readonly PostgresFixture _fixture;

	public SchemaMigrationTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	/// <summary>
	/// The core acceptance criterion, proven in one deterministic sequence rather than
	/// as separately-ordered [Fact]s (xUnit does not guarantee inter-test ordering):
	/// (1) applying to a genuinely fresh database creates the full M1 schema; (2)
	/// re-running the migrator against that now-migrated database is a no-op via the
	/// schema_migrations tracking table; (3) re-running the embedded migration SQL
	/// directly — bypassing the tracking table entirely — is *also* a no-op, proving
	/// the SQL itself is idempotent (IF NOT EXISTS / OR REPLACE / ON CONFLICT), not
	/// just the runner around it.
	/// </summary>
	[Fact]
	public async Task Migrations_ApplyFreshThenReapplyBothViaRunnerAndRawSql_AreAllIdempotent()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);

		// (1) Fresh apply.
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		foreach (string table in ExpectedTables)
		{
			Assert.True(await TableExistsAsync(connection, table), $"Expected table '{table}' to exist after a fresh migration.");
		}

		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));

		// (2) Re-apply via the runner: schema_migrations already has this version, so
		// this must be a pure no-op — not an error, not a second tracking row.
		await migrator.ApplyAsync();
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));

		foreach (string table in ExpectedTables)
		{
			Assert.True(await TableExistsAsync(connection, table));
		}

		// (3) Re-run the raw embedded SQL directly, bypassing the tracking table.
		// If any statement lacked IF NOT EXISTS/OR REPLACE/ON CONFLICT this throws.
		string sql = await ReadEmbeddedMigrationSqlAsync();
		await using NpgsqlCommand rawReapply = new(sql, connection);
		await rawReapply.ExecuteNonQueryAsync();

		Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM appliance_state"));
	}

	[Fact]
	public async Task Migrations_QueueClaimIndex_ExistsAndIsPartial()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// The claim query is `WHERE state = 'queued' ORDER BY priority, created_at`;
		// the index must both exist and carry that partial predicate, or the queue
		// scans the whole table under load.
		await using NpgsqlCommand command = new(
			"SELECT indexdef FROM pg_indexes WHERE indexname = 'idx_jobs_queue_claim'", connection);
		object? indexDefinition = await command.ExecuteScalarAsync();

		Assert.NotNull(indexDefinition);
		Assert.Contains("WHERE (state = 'queued'::text)", (string)indexDefinition!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Migrations_JobEventsSeqPrimaryKey_IsIdentityColumn()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"""
			SELECT is_identity, identity_generation
			FROM information_schema.columns
			WHERE table_name = 'job_events' AND column_name = 'seq'
			""", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync(), "job_events.seq column not found.");
		Assert.Equal("YES", reader.GetString(0));
		Assert.Equal("ALWAYS", reader.GetString(1));
	}

	private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
	{
		await using NpgsqlCommand command = new(
			"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1)",
			connection);
		command.Parameters.AddWithValue(tableName);
		return (bool)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
	{
		await using NpgsqlCommand command = new(sql, connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static async Task<string> ReadEmbeddedMigrationSqlAsync()
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = assembly.GetManifestResourceNames()
			.Single(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal));

		await using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync();
	}
}
