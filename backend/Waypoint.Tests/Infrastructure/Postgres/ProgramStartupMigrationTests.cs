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
using Npgsql;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// End-to-end proof of the ADR-0009 claim in <c>Program.cs</c>: launches the real
/// <c>Waypoint.Api</c> binary as a child process (see <see cref="ApiProcess"/>) — not
/// <c>WebApplicationFactory</c>'s in-process <c>TestServer</c> — pointed at a real,
/// empty Postgres database, and confirms the schema exists *before* the health probe
/// (and therefore any traffic) succeeds. <see cref="SchemaMigrationTests"/> proves the
/// migrator component works in isolation; this proves <c>Program.cs</c> actually wires
/// it into startup the way the doc comment there claims.
/// </summary>
[Collection("Postgres")]
public sealed class ProgramStartupMigrationTests
{
	private readonly PostgresFixture _fixture;

	public ProgramStartupMigrationTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact]
	public async Task Startup_WithMigrationsEnabled_AppliesSchemaBeforeTheHealthProbeSucceeds()
	{
		int port = ApiProcess.GetFreePort();

		using Process process = ApiProcess.Start(environment: new Dictionary<string, string>
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			// Deliberately not "Testing" — that environment's appsettings.Testing.json
			// turns RunMigrationsOnStartup off. This test exercises the default (on).
			["ASPNETCORE_ENVIRONMENT"] = "Production",
			["ConnectionStrings__Waypoint"] = _fixture.ConnectionString
		});

		try
		{
			await WaitForHealthyAsync(port);
			Assert.False(process.HasExited, "The API process exited instead of serving traffic.");

			await using NpgsqlConnection connection = new(_fixture.ConnectionString);
			await connection.OpenAsync();

			await using NpgsqlCommand command = new(
				"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'jobs')",
				connection);
			bool jobsTableExists = (bool)(await command.ExecuteScalarAsync())!;

			Assert.True(jobsTableExists, "jobs table was not created — Program.cs did not apply migrations on startup.");
		}
		finally
		{
			ApiProcess.Kill(process);
		}
	}

	private static async Task WaitForHealthyAsync(int port)
	{
		using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
		Exception? lastError = null;

		for (int attempt = 0; attempt < 60; attempt++)
		{
			try
			{
				HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/api/v1/health");
				if (response.IsSuccessStatusCode)
				{
					return;
				}
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				lastError = exception;
			}

			await Task.Delay(TimeSpan.FromSeconds(1));
		}

		throw new TimeoutException("Waypoint.Api did not become healthy in time.", lastError);
	}
}
