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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #757, end to end against real Postgres:
/// <c>POST /runs/{id}/jobs/bulk-cancel</c> and <c>bulk-retry</c> --
/// <see cref="IJobControlRepository.BulkCancelJobsAsync"/>/<see cref="IJobControlRepository.BulkRetryJobsAsync"/>
/// via <see cref="JobQueueRepository"/>. Proves what the fake-repository controller
/// tests (<c>Waypoint.Tests.Api.RunsEndpointTests</c>) cannot: real per-job state
/// transitions, honest partial-conflict reporting across a mixed job set, cross-run
/// scoping, filter-to-id resolution against real rows, and the one-summary-row audit
/// contract.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class BulkJobActionApiTests : IAsyncLifetime
{
	private sealed class BulkApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public BulkApiFactory(string connectionString)
		{
			_connectionString = connectionString;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureTestServices(services =>
			{
				services
					.AddAuthentication(TestAuthHandler.SchemeName)
					.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

				services.PostConfigure<AuthenticationOptions>(options =>
				{
					options.DefaultScheme = TestAuthHandler.SchemeName;
					options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
					options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
					options.DefaultForbidScheme = TestAuthHandler.SchemeName;
				});

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository), typeof(IComponentJobRepository) })
				{
					var descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (descriptor != null)
					{
						services.Remove(descriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IComponentJobRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private BulkApiFactory _factory = null!;
	private HttpClient _client = null!;

	public BulkJobActionApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new BulkApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	[Fact]
	public async Task BulkCancel_MixedStates_CancelsEligibleAndReportsConflictsHonestly()
	{
		Guid runId = await SeedRunAsyncWithInitiator("test-user");
		Guid queuedJob = await SeedQueuedJobAsync(runId);
		Guid runningJob = await SeedRunningJobAsync(runId);
		Guid doneJob = await SeedTerminalJobAsync(runId, "done");

		HttpResponseMessage response = await PostBulkAsync(
			"cancel", runId, new { job_ids = new[] { queuedJob.ToString(), runningJob.ToString(), doneJob.ToString() } }, "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(3, body.RootElement.GetProperty("resolved_count").GetInt32());
		Dictionary<string, string> outcomesByJobId = ReadOutcomes(body);
		Assert.Equal("cancelled", outcomesByJobId[queuedJob.ToString()]);
		Assert.Equal("cancel_requested", outcomesByJobId[runningJob.ToString()]);
		Assert.Equal("not_cancellable", outcomesByJobId[doneJob.ToString()]);

		// The database effects match the reported outcomes -- one conflict does not
		// block or roll back the others.
		Assert.Equal("cancelled", await ReadJobStateAsync(queuedJob));
		Assert.Equal("running", await ReadJobStateAsync(runningJob)); // cooperative flag only
		Assert.Equal("done", await ReadJobStateAsync(doneJob));

		// Exactly one summary audit row, not one per job.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand auditQuery = new(
			"SELECT actor, detail FROM audit_log WHERE event_type = 'job.bulk_cancelled' AND run_id = $1", connection);
		auditQuery.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await auditQuery.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("test-user", reader.GetString(0));
		using JsonDocument detail = JsonDocument.Parse(reader.GetString(1));
		Assert.Equal(3, detail.RootElement.GetProperty("resolved_count").GetInt32());
		Assert.False(await reader.ReadAsync(), "exactly one summary row, not one per job");
	}

	[Fact]
	public async Task BulkRetry_MixedStates_RetriesFailedAndReportsConflictsHonestly()
	{
		Guid runId = await SeedRunAsyncWithInitiator("test-user");
		Guid failedJob = await SeedFailedJobAsync(runId);
		Guid queuedJob = await SeedQueuedJobAsync(runId);

		HttpResponseMessage response = await PostBulkAsync(
			"retry", runId, new { job_ids = new[] { failedJob.ToString(), queuedJob.ToString() } }, "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Dictionary<string, string> outcomesByJobId = ReadOutcomes(body);
		Assert.Equal("queued", outcomesByJobId[failedJob.ToString()]);
		Assert.Equal("not_retryable", outcomesByJobId[queuedJob.ToString()]);

		Assert.Equal("queued", await ReadJobStateAsync(failedJob));
		Assert.Equal("queued", await ReadJobStateAsync(queuedJob)); // untouched, was already queued

		// One bulk-level summary row -- RetryJobAsync's own per-job 'job.retried' audit
		// row for the successfully retried job is a SEPARATE row, not a duplicate.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand summaryQuery = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'job.bulk_retried' AND run_id = $1", connection);
		summaryQuery.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await summaryQuery.ExecuteScalarAsync())!);

		await using NpgsqlCommand perJobQuery = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'job.retried' AND run_id = $1", connection);
		perJobQuery.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await perJobQuery.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task BulkCancel_JobIdFromDifferentRun_ReportsNotFoundAndDoesNotTouchIt()
	{
		Guid runId = await SeedRunAsyncWithInitiator("test-user");
		Guid otherRunId = await SeedRunAsyncWithInitiator("test-user");
		Guid ownJob = await SeedQueuedJobAsync(runId);
		Guid foreignJob = await SeedQueuedJobAsync(otherRunId);

		HttpResponseMessage response = await PostBulkAsync(
			"cancel", runId, new { job_ids = new[] { ownJob.ToString(), foreignJob.ToString() } }, "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Dictionary<string, string> outcomesByJobId = ReadOutcomes(body);
		Assert.Equal("cancelled", outcomesByJobId[ownJob.ToString()]);
		Assert.Equal("not_found", outcomesByJobId[foreignJob.ToString()]);

		Assert.Equal("cancelled", await ReadJobStateAsync(ownJob));
		Assert.Equal("queued", await ReadJobStateAsync(foreignJob));
	}

	[Fact]
	public async Task BulkCancel_FilterResolvesOnlyMatchingJobsInThisRun()
	{
		Guid runId = await SeedRunAsyncWithInitiator("test-user");
		Guid queuedA = await SeedQueuedJobAsync(runId);
		Guid queuedB = await SeedQueuedJobAsync(runId);
		Guid doneJob = await SeedTerminalJobAsync(runId, "done");

		string[] queuedFilterStates = ["queued"];
		HttpResponseMessage response = await PostBulkAsync(
			"cancel", runId, new { filter = new { state = queuedFilterStates } }, "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(2, body.RootElement.GetProperty("resolved_count").GetInt32());
		Dictionary<string, string> outcomesByJobId = ReadOutcomes(body);
		Assert.Equal("cancelled", outcomesByJobId[queuedA.ToString()]);
		Assert.Equal("cancelled", outcomesByJobId[queuedB.ToString()]);
		Assert.False(outcomesByJobId.ContainsKey(doneJob.ToString()));
		Assert.Equal("done", await ReadJobStateAsync(doneJob)); // never touched -- filter excluded it
	}

	[Fact]
	public async Task BulkCancel_NonOwnerCyber_Returns403AndTouchesNothing()
	{
		Guid runId = await SeedRunAsyncWithInitiator("another-user");
		Guid jobId = await SeedQueuedJobAsync(runId);

		HttpResponseMessage response = await PostBulkAsync("cancel", runId, new { job_ids = new[] { jobId.ToString() } }, "Cyber");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
		Assert.Equal("queued", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task BulkCancel_Admin_SucceedsOnNonOwnedRun()
	{
		Guid runId = await SeedRunAsyncWithInitiator("another-user");
		Guid jobId = await SeedQueuedJobAsync(runId);

		HttpResponseMessage response = await PostBulkAsync("cancel", runId, new { job_ids = new[] { jobId.ToString() } }, "Admin");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	private static Dictionary<string, string> ReadOutcomes(JsonDocument body)
	{
		Dictionary<string, string> outcomesByJobId = [];
		foreach (JsonElement item in body.RootElement.GetProperty("items").EnumerateArray())
		{
			outcomesByJobId[item.GetProperty("job_id").GetString()!] = item.GetProperty("outcome").GetString()!;
		}

		return outcomesByJobId;
	}

	// -- seed / read helpers --------------------------------------------------

	private async Task<Guid> SeedRunAsyncWithInitiator(string? initiatedBy)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, initiated_by, state) VALUES ('scan', '{}'::jsonb, $1, 'running') RETURNING id",
			connection);
		insert.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedQueuedJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state) VALUES ($1, 'scan', 1, 'queued') RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedRunningJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, claimed_by, claimed_at, lease_expires_at, heartbeat_at)
			VALUES ($1, 'scan', 1, 'running', 'worker-a', now(), now() + interval '5 minutes', now())
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedTerminalJobAsync(Guid runId, string terminalState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, finished_at) VALUES ($1, 'scan', 1, $2, now()) RETURNING id",
			connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(terminalState);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedFailedJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, attempt_count, max_attempts, finished_at, note)
			VALUES ($1, 'scan', 1, 'failed', 1, 5, now(), 'Simulated failure for test')
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> ReadJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> PostBulkAsync(string action, Guid runId, object body, string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/bulk-{action}")
		{
			Content = JsonContent.Create(body),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return await _client.SendAsync(request);
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE job_events, downloads, audit_log, jobs, runs, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
