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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.SystemState;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #444 regression test, closing the exact gap that let a real bug ship: every
/// prior <see cref="WorkerRegistryRepository"/> test (<c>WorkerRegistryRepositoryTests</c>)
/// ran against <see cref="PostgresFixture.ConnectionString"/> -- the full-privilege test
/// owner role -- never the actual least-privilege <c>waypoint_compliance_runner</c>/
/// <c>waypoint_download_runner</c> roles migrations 0025/0027/0028 grant to. Migration
/// 0027 shipped granting only INSERT+UPDATE on <c>worker_registry</c>, reasoning "neither
/// runner role ever SELECTs this table" -- true of the application's own SQL text, false
/// about what Postgres requires to execute it: <c>HeartbeatAsync</c>'s
/// <c>INSERT ... ON CONFLICT (worker_id) DO UPDATE ...</c> needs SELECT on the target
/// table to evaluate the conflict and the UPDATE's <c>EXCLUDED</c>-row reference, even
/// with no explicit SELECT statement anywhere in the C# or embedded SQL. This shipped
/// broken on a fresh Compose stack for two full landed PRs (#443, then unnoticed through
/// #444's own early drafting) before a live fresh-stack smoke-test run caught
/// <c>permission denied for table worker_registry</c> against a fully-migrated database
/// -- migration 0028 is the fix; this is the test that keeps it fixed.
/// </summary>
[Collection("Postgres")]
public sealed class WorkerRegistryRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _downloadRunnerConnectionString = string.Empty;

	public WorkerRegistryRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		// PostgresFixture.CreateRunnerRolesAsync provisions both roles with the fixed
		// test password "waypoint_test" against the same host/port/db as the owner
		// connection string -- swap only the username/password, same convention this
		// file's sibling tests use for other role-scoped connections.
		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString);
		builder.Username = "waypoint_compliance_runner";
		builder.Password = "waypoint_test";
		_complianceRunnerConnectionString = builder.ConnectionString;

		builder.Username = "waypoint_download_runner";
		_downloadRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// The exact reproduction: a first heartbeat (plain INSERT, no conflicting row yet)
	/// succeeded even under the broken 0027-only grant -- so this test's first call alone
	/// would NOT have caught the bug. The second call against the SAME worker_id is what
	/// forces the <c>ON CONFLICT ... DO UPDATE</c> path and is what failed
	/// `permission denied for table worker_registry` before migration 0028.
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_CanHeartbeatTwice_TheUpsertPathActuallyWorks()
	{
		WorkerRegistryRepository registry = new(_complianceRunnerConnectionString);

		await registry.HeartbeatAsync("compliance-runner-role-probe", ["scan", "discover"], ready: true, CancellationToken.None);
		await registry.HeartbeatAsync("compliance-runner-role-probe", ["scan", "discover"], ready: false, CancellationToken.None);

		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using NpgsqlCommand count = new(
			"SELECT count(*), bool_and(ready = false) FROM worker_registry WHERE worker_id = 'compliance-runner-role-probe'", verify);
		await using NpgsqlDataReader reader = await count.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(1L, reader.GetInt64(0));
		Assert.True(reader.GetBoolean(1), "expected the second heartbeat's ready=false to have overwritten the row, not appended one.");
	}

	/// <summary>Same proof for the download-runner role -- migration 0028 grants both roles identically.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CanHeartbeatTwice_TheUpsertPathActuallyWorks()
	{
		WorkerRegistryRepository registry = new(_downloadRunnerConnectionString);

		await registry.HeartbeatAsync("download-runner-role-probe", ["download", "catalog-index"], ready: true, CancellationToken.None);
		await registry.HeartbeatAsync("download-runner-role-probe", ["download", "catalog-index"], ready: false, CancellationToken.None);

		await using NpgsqlConnection verify = new(_fixture.ConnectionString);
		await verify.OpenAsync();
		await using NpgsqlCommand count = new(
			"SELECT count(*), bool_and(ready = false) FROM worker_registry WHERE worker_id = 'download-runner-role-probe'", verify);
		await using NpgsqlDataReader reader = await count.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(1L, reader.GetInt64(0));
		Assert.True(reader.GetBoolean(1), "expected the second heartbeat's ready=false to have overwritten the row, not appended one.");
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE worker_registry RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
