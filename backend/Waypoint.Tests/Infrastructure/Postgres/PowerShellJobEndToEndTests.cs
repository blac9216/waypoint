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
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Epic #6's end-to-end acceptance, through the REAL loop: dispatcher claims a
/// fanned-out job, the PowerShell handler executes the stub module in-process,
/// streams land in <c>job_events</c> via the batched redacting buffer, and outcomes
/// drive the state machine -- including the security.md control-5 canary (a seeded
/// fake secret emitted on every stream never reaches the table) and the
/// auth-failure path tripping migration 0005's durable credential halt.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: IAsyncLifetime.DisposeAsync stops the buffer and disposes the pool after every test.
public sealed class PowerShellJobEndToEndTests : IAsyncLifetime
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointStubModule", "WaypointStubModule.psm1");

	private readonly PostgresFixture _fixture;
	private JobQueueRepository _repository = null!;
	private InPlaySecretRedactor _redactor = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private PowerShellJobHandler _handler = null!;

	public PowerShellJobEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_redactor = new InPlaySecretRedactor();

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrappedOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedOptions, NullLogger<PowerShellExecutor>.Instance);
		_handler = new PowerShellJobHandler("discover", executor, _redactor, wrappedOptions);
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>security.md control 5: the canary secret is emitted on the info,
	/// warning and error streams AND the output; zero occurrences may reach
	/// <c>job_events</c> or <c>jobs.note</c>.</summary>
	[Fact]
	public async Task TheCanarySecret_NeverReachesJobEventsOrTheNote()
	{
		const string canary = "invented-e2e-canary-a1b2c3";
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds;
		using (_redactor.Track(canary))
		{
			jobIds = await _repository.FanOutJobsAsync(
				runId,
				[new JobSpec("discover", 1, CredentialId: credentialId,
					Payload: $$$"""{"command":"Write-StubSecretLeak","parameters":{"Secret":"{{{canary}}}"}}""")],
				"tester", CancellationToken.None);

			JobDispatcherHostedService dispatcher = CreateDispatcher();
			await dispatcher.StartAsync(CancellationToken.None);
			try
			{
				await PollUntilTerminalAsync(jobIds[0]);
			}
			finally
			{
				await dispatcher.StopAsync(CancellationToken.None);
			}
		}

		// Drain the buffer, then hunt the canary anywhere in job_events and jobs.
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand leaked = new(
			"SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand redactedPresent = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND payload::text LIKE '%[REDACTED]%'", connection))
		{
			redactedPresent.Parameters.AddWithValue(jobIds[0]);
			Assert.True((long)(await redactedPresent.ExecuteScalarAsync())! >= 3); // info + warning + error streams
		}

		await using NpgsqlCommand note = new("SELECT state, COALESCE(note, '') FROM jobs WHERE id = $1", connection);
		note.Parameters.AddWithValue(jobIds[0]);
		await using NpgsqlDataReader reader = await note.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("done", reader.GetString(0)); // Write-StubSecretLeak errors are non-terminating
		Assert.DoesNotContain(canary, reader.GetString(1), StringComparison.Ordinal);
	}

	/// <summary>#156: a secret that transits two JSON serialization layers before the
	/// sink -- here, the stub simulates an upstream HTTP error body (ConvertTo-Json,
	/// layer 1) quoted into an information line, which Emit() then serializes into the
	/// job_events payload (layer 2) -- must still be fully redacted, not just the
	/// unescaped prefix a level-1-only needle set would catch.</summary>
	[Fact]
	public async Task TheDoublyEscapedCanarySecret_NeverReachesJobEvents()
	{
		const string canary = "pa\"ss\\wo<rd-invented-e2e";
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds;
		using (_redactor.Track(canary))
		{
			string payload = JsonSerializer.Serialize(new
			{
				command = "Write-StubDoublyEscapedSecretLeak",
				parameters = new { Secret = canary },
			});
			jobIds = await _repository.FanOutJobsAsync(
				runId,
				[new JobSpec("discover", 1, CredentialId: credentialId, Payload: payload)],
				"tester", CancellationToken.None);

			JobDispatcherHostedService dispatcher = CreateDispatcher();
			await dispatcher.StartAsync(CancellationToken.None);
			try
			{
				await PollUntilTerminalAsync(jobIds[0]);
			}
			finally
			{
				await dispatcher.StopAsync(CancellationToken.None);
			}
		}

		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Neither the raw canary nor its once-escaped spelling (what a level-1-only
		// needle set would leave behind after only partially redacting the doubly
		// -escaped occurrence) may survive.
		await using (NpgsqlCommand leaked = new(
			"SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue("rd-invented-e2e");
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand redactedPresent = new(
			"SELECT count(*) FROM job_events WHERE job_id = $1 AND payload::text LIKE '%[REDACTED]%'", connection))
		{
			redactedPresent.Parameters.AddWithValue(jobIds[0]);
			Assert.True((long)(await redactedPresent.ExecuteScalarAsync())! >= 1);
		}
	}

	/// <summary>The full #6-meets-#5 wire: three consecutive PowerShell auth failures
	/// against one credential end as <c>auth-failed</c> jobs AND durably halt the
	/// credential (migration 0005), so a later fan-out is born blocked.</summary>
	[Fact]
	public async Task ThreeAuthFailures_ThroughTheRealHandler_TripTheDurableHalt()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await _repository.CreateRunAsync("discover", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[.. Enumerable.Range(0, 3).Select(_ => new JobSpec("discover", 1, CredentialId: credentialId,
				Payload: """{"command":"Invoke-StubFailure","parameters":{"Message":"401 Unauthorized (invented depot reply)"}}"""))],
			"tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			foreach (Guid jobId in jobIds)
			{
				await PollUntilTerminalAsync(jobId);
			}

			await PollAsync(async () => await IsCredentialHaltedAsync(credentialId));
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		foreach (Guid jobId in jobIds)
		{
			Assert.Equal("auth-failed", await GetJobFieldAsync(jobId, "state"));
		}

		Assert.True(await IsCredentialHaltedAsync(credentialId));

		// Post-halt fan-out is born blocked -- the #144 guarantee, now reached through
		// the real handler path end to end.
		Guid laterRunId = await _repository.CreateRunAsync("discover", "{}", credentialId, "tester", CancellationToken.None);
		IReadOnlyList<Guid> laterJobIds = await _repository.FanOutJobsAsync(
			laterRunId,
			[new JobSpec("discover", 1, CredentialId: credentialId, Payload: """{"command":"Get-StubEcho"}""")],
			"tester", CancellationToken.None);
		Assert.Equal("blocked", await GetJobFieldAsync(laterJobIds[0], "state"));
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
		insert.Parameters.AddWithValue($"cred-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task<bool> IsCredentialHaltedAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT queue_halted FROM credentials WHERE id = $1", connection);
		query.Parameters.AddWithValue(credentialId);
		return (bool)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		await PollAsync(async () =>
		{
			string state = await GetJobFieldAsync(jobId, "state");
			return state is "done" or "failed" or "auth-failed" or "cancelled";
		});
	}

	private static async Task PollAsync(Func<Task<bool>> condition)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			if (await condition())
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}
}
