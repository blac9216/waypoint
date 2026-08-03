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
/// shared <c>deploy/docker-compose.yml</c> stack (fixed container names,
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
		int port = ApiProcess.GetFreePort();
		ConnectionString =
			$"Host=127.0.0.1;Port={port};Database=waypoint_test;Username=waypoint_test;Password=waypoint_test";

		await RunDockerAsync(
			"run", "-d", "--name", _containerName,
			"-e", "POSTGRES_USER=waypoint_test",
			"-e", "POSTGRES_PASSWORD=waypoint_test",
			"-e", "POSTGRES_DB=waypoint_test",
			"-p", $"{port}:5432",
			"postgres:16-alpine").ConfigureAwait(false);

		await WaitUntilReadyAsync().ConfigureAwait(false);
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

	private static async Task RunDockerAsync(params string[] arguments)
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

		string standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
		await process.WaitForExitAsync().ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"docker {string.Join(' ', arguments)} exited {process.ExitCode}: {standardError}");
		}
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
