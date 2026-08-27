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

using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Starts a real, disposable PostgreSQL 16 container for the schema/queue-concurrency
/// tests (issue #4) — no in-memory or fake provider, per docs/testing.md's "don't
/// substitute away a tool that is actually available" rule (docker is available in
/// this sandbox). Isolated per docs/testing.md's recipe: a container name unique per
/// test run and a dynamically reserved host port, so this never collides with the
/// shared <c>deploy/compose.yaml</c> stack (fixed container names,
/// <c>waypoint-postgres</c>) or another agent's ad hoc container. Removed with
/// <c>docker rm -f</c> in <see cref="DisposeAsync"/> regardless of test outcome.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	private readonly string _containerName = $"wp-test-pg-{Guid.NewGuid():N}";

	/// <summary>Connection string for the running container's <c>waypoint_test</c> database.</summary>
	public string ConnectionString { get; private set; } = string.Empty;

	public async Task InitializeAsync()
	{
		string? network = Environment.GetEnvironmentVariable("WAYPOINT_TEST_PG_NETWORK");

		if (string.IsNullOrEmpty(network))
		{
			int port = ApiProcess.GetFreePort();
			string host = Environment.GetEnvironmentVariable("WAYPOINT_TEST_PG_HOST") ?? "127.0.0.1";
			ConnectionString =
				$"Host={host};Port={port};Database=waypoint_test;Username=waypoint_test;Password=waypoint_test";

			await RunDockerAsync(
				"run", "-d", "--name", _containerName,
				"-e", "POSTGRES_USER=waypoint_test",
				"-e", "POSTGRES_PASSWORD=waypoint_test",
				"-e", "POSTGRES_DB=waypoint_test",
				"-p", $"{port}:5432",
				"postgres:16-alpine").ConfigureAwait(false);
		}
		else
		{
			// Bridge-networked test process (e.g. a devcontainer): published host ports
			// can be unreachable in both directions (inter-bridge isolation + host DNAT),
			// so join the given network and dial the container's own address on 5432 —
			// a unique IP per container, so concurrent test runs on one host cannot
			// collide. See docs/testing.md.
			await RunDockerAsync(
				"run", "-d", "--name", _containerName,
				"-e", "POSTGRES_USER=waypoint_test",
				"-e", "POSTGRES_PASSWORD=waypoint_test",
				"-e", "POSTGRES_DB=waypoint_test",
				"--network", network,
				"postgres:16-alpine").ConfigureAwait(false);

			string address = (await RunDockerAsync(
				"inspect", "--format",
				$"{{{{(index .NetworkSettings.Networks \"{network}\").IPAddress}}}}",
				_containerName).ConfigureAwait(false)).Trim();

			if (address.Length == 0)
			{
				throw new InvalidOperationException(
					$"Postgres test container '{_containerName}' has no address on network '{network}'.");
			}

			ConnectionString =
				$"Host={address};Port=5432;Database=waypoint_test;Username=waypoint_test;Password=waypoint_test";
		}

		await WaitUntilReadyAsync().ConfigureAwait(false);
		await CreateRunnerRolesAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// Creates the two runner login roles migration 0025 grants to
	/// (<c>backend/Waypoint.Infrastructure/Data/Migrations/0025_runner_db_roles.sql</c>),
	/// mirroring what <c>deploy/postgres/initdb/01-runner-roles.sh</c> does for a real
	/// stack's fresh <c>pgdata</c> volume. This fixture starts a bare
	/// <c>postgres:16-alpine</c> container with no init scripts, so without this step
	/// every test that runs migrations against it would fail the moment 0025 tries to
	/// <c>GRANT</c> to a role that does not exist — the same "role must already exist"
	/// failure 0025's own header comment describes as the correct behavior for a real
	/// deployment missing the init script, just surfacing here instead against a test
	/// container that intentionally never runs one.
	/// </summary>
	private async Task CreateRunnerRolesAsync()
	{
		await using NpgsqlConnection connection = new(ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			DO $$
			BEGIN
				IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
					CREATE ROLE waypoint_compliance_runner LOGIN PASSWORD 'waypoint_test' NOSUPERUSER NOCREATEDB NOCREATEROLE;
				END IF;
				IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
					CREATE ROLE waypoint_download_runner LOGIN PASSWORD 'waypoint_test' NOSUPERUSER NOCREATEDB NOCREATEROLE;
				END IF;
			END
			$$;
			""",
			connection);
		await command.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	public async Task DisposeAsync()
	{
		await RunDockerAsync("rm", "-f", _containerName).ConfigureAwait(false);
	}

	/// <summary>
	/// Clears every row the job engine's genuinely *global*, unscoped queries
	/// (<c>ClaimJobAsync</c>, <c>RecoverExpiredLeasesAsync</c>) would otherwise see
	/// across test methods and test classes sharing this one container. Every other
	/// test class in this collection scopes its own queries (by <c>run_id</c>, a unique
	/// marker, or a specific <c>credential_id</c>) and is unaffected by leftover rows,
	/// so it never needed this; a test proving a truly global claim/recovery query's
	/// concurrency guarantee cannot make that same assumption -- an unrelated leftover
	/// 'queued' or lease-expired 'running' row from an earlier test is exactly as
	/// claimable/recoverable as the row the test is trying to observe. Call this first
	/// thing in <c>InitializeAsync</c> from any test class that calls those two methods.
	/// </summary>
	public async Task ResetJobEngineDataAsync()
	{
		await using NpgsqlConnection connection = new(ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);

		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE job_events, downloads, audit_log, jobs, runs, credential_secrets, credentials RESTART IDENTITY CASCADE",
			connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	private async Task WaitUntilReadyAsync()
	{
		Exception? lastError = null;

		for (int attempt = 0; attempt < 60; attempt++)
		{
			try
			{
				await using NpgsqlConnection connection = new(ConnectionString);
				await connection.OpenAsync().ConfigureAwait(false);
				return;
			}
			catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
			{
				lastError = exception;
				await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
			}
		}

		throw new TimeoutException(
			$"Postgres test container '{_containerName}' did not become ready in time.", lastError);
	}

	private static async Task<string> RunDockerAsync(params string[] arguments)
	{
		ProcessStartInfo startInfo = new("docker")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start the docker CLI process.");

		Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
		string standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
		await process.WaitForExitAsync().ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"docker {string.Join(' ', arguments)} exited {process.ExitCode}: {standardError}");
		}

		return await standardOutput.ConfigureAwait(false);
	}
}

/// <summary>
/// Shares one <see cref="PostgresFixture"/> container across every test class in the
/// "Postgres" collection. xUnit runs test classes within the same collection
/// sequentially by default (parallelism is still enabled *across* collections), which
/// is exactly what a shared, stateful Postgres instance needs — no extra
/// configuration required for "full suite green with parallelism enabled".
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
}
