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
using System.Text;
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
/// Issue #291, end to end against real Postgres: <c>DELETE /jobs/{id}</c> (thin wrapper
/// over #277's <see cref="IJobControlRepository.CancelJobAsync"/>) and
/// <c>POST /runs/{id}/resume-blocked</c> (the new true-swap primitive,
/// <see cref="IJobControlRepository.SwapAndResumeBlockedCredentialAsync"/>). Proves the
/// database effects the fake-repository role-gate tests cannot: jobs actually carrying
/// the new credential_id after a swap, and the audit_log row carrying both identities.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class JobsAndResumeBlockedEndpointTests : IAsyncLifetime
{
	private sealed class JobsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public JobsApiFactory(string connectionString)
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

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
				{
					var jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (jobsDescriptor != null)
					{
						services.Remove(jobsDescriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private JobsApiFactory _factory = null!;
	private HttpClient _client = null!;

	public JobsAndResumeBlockedEndpointTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new JobsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	// -- DELETE /jobs/{id} ---------------------------------------------------

	[Fact]
	public async Task CancelJob_Queued_Returns200Cancelled()
	{
		Guid credentialId = await SeedCredentialAsync();
		// Owned by "test-user" -- the fixed identity TestAuthHandler authenticates as
		// (issue #294: DELETE /jobs/{id} now enforces run ownership).
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("cancelled", body.RootElement.GetProperty("state").GetString());
		Assert.Equal(jobId.ToString(), body.RootElement.GetProperty("job_id").GetString());

		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_Running_Returns200CancelRequested()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedRunningJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("cancel_requested", body.RootElement.GetProperty("state").GetString());

		// Not immediately cancelled -- the cooperative flag is set, state is unchanged.
		Assert.Equal("running", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_AlreadyTerminal_Returns409()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedTerminalJobAsync(runId, credentialId, "done");

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_cancellable");
	}

	[Fact]
	public async Task CancelJob_MissingJob_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{Guid.NewGuid()}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task CancelJob_BelowCyber_Returns403()
	{
		Guid credentialId = await SeedCredentialAsync();
		// Owned by "test-user" so this exercises the role gate specifically, not
		// ownership (both would 403, but this keeps the test's intent unambiguous).
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// Issue #757's "Cyber controls owned live scans" owner decision lowered this
	/// floor from Operator+ to Cyber+ (docs/api-contract.md's role matrix, PR #819) --
	/// a Cyber caller on their OWN run now succeeds where it previously 403'd.
	/// </summary>
	[Fact]
	public async Task CancelJob_OwnerCyber_Returns200()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Cyber", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	// -- issue #294: job-cancel ownership -------------------------------------
	// Mirrors RunsController.EnforceRunOwnership's "Operator+ (own runs), Admin
	// any" scope (docs/api-contract.md). TestAuthHandler always authenticates as
	// "test-user"; SeedRunAsyncWithInitiator lets these tests control the run's
	// recorded initiator to exercise the owner/non-owner/ownerless branches.

	[Fact]
	public async Task CancelJob_OwnerOperator_Returns200()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_NonOwnerOperator_Returns403AndJobNotCancelled()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "another-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
		Assert.Equal("queued", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_Admin_SucceedsOnNonOwnedJob()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "another-user");
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Admin", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_OperatorOnOwnerlessJob_Returns403()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, initiatedBy: null);
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
		Assert.Equal("queued", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task CancelJob_AdminOnOwnerlessJob_Returns200()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, initiatedBy: null);
		Guid jobId = await SeedQueuedJobAsync(runId, credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/jobs/{jobId}", "Admin", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("cancelled", await ReadJobStateAsync(jobId));
	}

	// -- POST /runs/{runId}/jobs/{jobId}/retry (issue #297) ------------------
	// ADR-0012 §5's engine-level resume primitive gets its operator-facing HTTP
	// surface here: a failed job returns to queued with jobs.stage untouched, so the
	// next claim resumes the pipeline where it left off rather than restarting it.

	[Fact]
	public async Task RetryJob_Failed_ReturnsToQueued_AndSubsequentClaimResumesAtStage()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: "converting");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(jobId.ToString(), body.RootElement.GetProperty("job_id").GetString());
		Assert.Equal("queued", body.RootElement.GetProperty("state").GetString());
		Assert.Equal("converting", body.RootElement.GetProperty("stage").GetString());

		(string state, string? stage) = await ReadJobStateAndStageAsync(jobId);
		Assert.Equal("queued", state);
		Assert.Equal("converting", stage);

		// The crux (real Postgres): a subsequent claim hands the preserved stage marker
		// straight back via ClaimedJob.Stage, so the handler resumes at "converting"
		// instead of restarting the pipeline from the first stage.
		JobQueueRepository repository = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);
		ClaimedJob? claimed = await repository.ClaimJobAsync("worker-resume-test", TimeSpan.FromMinutes(5), JobCapabilities.All, CancellationToken.None);

		Assert.NotNull(claimed);
		Assert.Equal(jobId, claimed!.Id);
		Assert.Equal("converting", claimed.Stage);
	}

	[Fact]
	public async Task RetryJob_Failed_DoesNotIncrementAttemptCount_AndAudits()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: null, attemptCount: 3);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		// Retry-accounting: a manual operator retry is an override -- attempt_count is
		// left exactly as the auto-retry path last set it (never incremented by the
		// retry endpoint itself, never blocked by max_attempts).
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT attempt_count, claimed_by, lease_expires_at FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(3, reader.GetInt32(0));
		Assert.True(reader.IsDBNull(1));
		Assert.True(reader.IsDBNull(2));
		await reader.DisposeAsync();

		// Audit note records who retried which job/run.
		await using NpgsqlCommand auditQuery = new(
			"SELECT actor, run_id, detail FROM audit_log WHERE event_type = 'job.retried' AND run_id = $1", connection);
		auditQuery.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader auditReader = await auditQuery.ExecuteReaderAsync();
		Assert.True(await auditReader.ReadAsync());
		Assert.Equal("test-user", auditReader.GetString(0));
		Assert.Equal(runId, auditReader.GetGuid(1));
		using JsonDocument detail = JsonDocument.Parse(auditReader.GetString(2));
		Assert.Equal(jobId.ToString(), detail.RootElement.GetProperty("job_id").GetString());
	}

	[Theory]
	[InlineData("queued")]
	[InlineData("running")]
	[InlineData("done")]
	[InlineData("auth-failed")]
	[InlineData("cancelled")]
	public async Task RetryJob_NonFailedState_Returns409(string state)
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = state switch
		{
			"queued" => await SeedQueuedJobAsync(runId, credentialId),
			"running" => await SeedRunningJobAsync(runId, credentialId),
			_ => await SeedTerminalJobAsync(runId, credentialId, state),
		};

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_retryable");
		Assert.Equal(state, await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task RetryJob_JobNotInRun_Returns404()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid otherRunId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedFailedJobAsync(otherRunId, credentialId, stage: null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task RetryJob_MissingRun_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/jobs/{Guid.NewGuid()}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task RetryJob_BelowCyber_Returns403()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("failed", await ReadJobStateAsync(jobId));
	}

	/// <summary>Issue #757's role-floor change (see <see cref="CancelJob_OwnerCyber_Returns200"/>) applied identically to retry.</summary>
	[Fact]
	public async Task RetryJob_OwnerCyber_Returns200()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "test-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: "attesting");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Cyber", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		(string state, string? stage) = await ReadJobStateAndStageAsync(jobId);
		Assert.Equal("queued", state);
		Assert.Equal("attesting", stage);
	}

	[Fact]
	public async Task RetryJob_NonOwnerOperator_Returns403AndJobNotRetried()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "another-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
		Assert.Equal("failed", await ReadJobStateAsync(jobId));
	}

	[Fact]
	public async Task RetryJob_Admin_SucceedsOnNonOwnedJob()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsyncWithInitiator(credentialId, "another-user");
		Guid jobId = await SeedFailedJobAsync(runId, credentialId, stage: "attesting");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/runs/{runId}/jobs/{jobId}/retry", "Admin", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		(string state, string? stage) = await ReadJobStateAndStageAsync(jobId);
		Assert.Equal("queued", state);
		Assert.Equal("attesting", stage);
	}

	// -- POST /runs/{id}/resume-blocked --------------------------------------

	[Fact]
	public async Task ResumeBlocked_HappyPath_SwapsCredentialAndAuditsBothIds()
	{
		Guid oldCredentialId = await SeedCredentialAsync("cred-old");
		Guid newCredentialId = await SeedCredentialAsync("cred-new");
		Guid runId = await SeedRunAsync(oldCredentialId);
		Guid blockedJobId = await SeedBlockedJobAsync(runId, oldCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = newCredentialId.ToString() });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(oldCredentialId.ToString(), body.RootElement.GetProperty("old_credential_id").GetString());
		Assert.Equal(newCredentialId.ToString(), body.RootElement.GetProperty("new_credential_id").GetString());
		Assert.Equal(1, body.RootElement.GetProperty("resumed_job_count").GetInt32());

		// The job actually carries the NEW credential id, and is queued again.
		(string state, Guid? credentialId) = await ReadJobStateAndCredentialAsync(blockedJobId);
		Assert.Equal("queued", state);
		Assert.Equal(newCredentialId, credentialId);

		// The audit row carries BOTH credential identities.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand auditQuery = new(
			"SELECT credential_id, detail FROM audit_log WHERE event_type = 'credential.swapped' AND run_id = $1", connection);
		auditQuery.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await auditQuery.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(newCredentialId, reader.GetGuid(0));
		using JsonDocument detail = JsonDocument.Parse(reader.GetString(1));
		Assert.Equal(oldCredentialId.ToString(), detail.RootElement.GetProperty("old_credential_id").GetString());
		Assert.Equal(newCredentialId.ToString(), detail.RootElement.GetProperty("new_credential_id").GetString());
	}

	/// <summary>
	/// Issue #585 (epic #582): the swap must rewrite the blocked jobs' per-purpose
	/// snapshot rows (<c>job_credential_bindings</c>, migration 0044) in the same
	/// transaction as <c>jobs.credential_id</c> -- handlers prefer the snapshot row for
	/// their execution purpose, so a stale row would make the resumed job decrypt the
	/// very credential the swap replaced. A binding row naming a DIFFERENT credential
	/// (an unrelated vcsa-ssh purpose) is untouched.
	/// </summary>
	[Fact]
	public async Task ResumeBlocked_SwapsMatchingJobCredentialBindingRows_LeavesOtherPurposesAlone()
	{
		Guid oldCredentialId = await SeedCredentialAsync("cred-old-bound");
		Guid newCredentialId = await SeedCredentialAsync("cred-new-bound");
		Guid unrelatedVcsaCredentialId = await SeedCredentialAsync("cred-vcsa-untouched", credentialType: "ssh");
		Guid runId = await SeedRunAsync(oldCredentialId);
		Guid blockedJobId = await SeedBlockedJobAsync(runId, oldCredentialId);
		await SeedJobBindingAsync(blockedJobId, "vsphere-api", oldCredentialId);
		await SeedJobBindingAsync(blockedJobId, "vcsa-ssh", unrelatedVcsaCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = newCredentialId.ToString() });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand bindingsQuery = new(
			"SELECT purpose, credential_id FROM job_credential_bindings WHERE job_id = $1 ORDER BY purpose", connection);
		bindingsQuery.Parameters.AddWithValue(blockedJobId);
		Dictionary<string, Guid?> bindings = [];
		await using NpgsqlDataReader reader = await bindingsQuery.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			bindings[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetGuid(1);
		}

		Assert.Equal(2, bindings.Count);
		Assert.Equal(newCredentialId, bindings["vsphere-api"]);
		Assert.Equal(unrelatedVcsaCredentialId, bindings["vcsa-ssh"]);
	}

	private async Task SeedJobBindingAsync(Guid jobId, string purpose, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO job_credential_bindings (job_id, purpose, credential_id) VALUES ($1, $2, $3)", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(purpose);
		command.Parameters.AddWithValue(credentialId);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task ResumeBlocked_RunNotHalted_Returns409()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsync(credentialId);
		Guid replacementId = await SeedCredentialAsync("cred-replacement");

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = replacementId.ToString() });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "run_not_halted");
	}

	[Fact]
	public async Task ResumeBlocked_ReplacementCredentialNotFound_Returns404()
	{
		Guid oldCredentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsync(oldCredentialId);
		await SeedBlockedJobAsync(runId, oldCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = Guid.NewGuid().ToString() });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task ResumeBlocked_ReplacementCredentialItselfHalted_Returns409()
	{
		Guid oldCredentialId = await SeedCredentialAsync("cred-old");
		Guid replacementId = await SeedCredentialAsync("cred-also-halted");
		Guid runId = await SeedRunAsync(oldCredentialId);
		await SeedBlockedJobAsync(runId, oldCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await HaltCredentialAsync(replacementId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = replacementId.ToString() });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "replacement_credential_halted");
	}

	[Fact]
	public async Task ResumeBlocked_ReplacementCredentialTypeMismatch_Returns400()
	{
		Guid oldCredentialId = await SeedCredentialAsync("cred-vcenter", credentialType: "vcenter");
		Guid replacementId = await SeedCredentialAsync("cred-ssh", credentialType: "ssh");
		Guid runId = await SeedRunAsync(oldCredentialId);
		await SeedBlockedJobAsync(runId, oldCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", "Admin", new { credential_id = replacementId.ToString() });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task ResumeBlocked_BelowAdmin_Returns403(string role)
	{
		Guid oldCredentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsync(oldCredentialId);
		Guid replacementId = await SeedCredentialAsync("cred-replacement");
		await SeedBlockedJobAsync(runId, oldCredentialId);
		await HaltCredentialAsync(oldCredentialId);
		await MarkRunBlockedAsync(runId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/runs/{runId}/resume-blocked", role, new { credential_id = replacementId.ToString() });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	// -- regression: UnblockCredentialAsync's own behavior is unchanged ------

	[Fact]
	public async Task Regression_UnblockCredentialAsync_StillDoesSameCredentialRetryOnly()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid runId = await SeedRunAsync(credentialId);
		Guid blockedJobId = await SeedBlockedJobAsync(runId, credentialId);
		await HaltCredentialAsync(credentialId);
		await MarkRunBlockedAsync(runId);

		JobQueueRepository repository = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);
		CredentialUnblockResult result = await repository.UnblockCredentialAsync(credentialId, "operator retried", CancellationToken.None);

		Assert.True(result.WasHalted);
		Assert.Contains(blockedJobId, result.UnblockedJobIds);

		// Same credential id -- UnblockCredentialAsync never mutates jobs.credential_id.
		(string state, Guid? readCredentialId) = await ReadJobStateAndCredentialAsync(blockedJobId);
		Assert.Equal("queued", state);
		Assert.Equal(credentialId, readCredentialId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand haltedQuery = new("SELECT queue_halted FROM credentials WHERE id = $1", connection);
		haltedQuery.Parameters.AddWithValue(credentialId);
		Assert.False((bool)(await haltedQuery.ExecuteScalarAsync())!);
	}

	// -- seed / read helpers --------------------------------------------------

	private async Task<Guid> SeedCredentialAsync(string namePrefix = "cred", string credentialType = "token")
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue($"{namePrefix}-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedRunAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, credential_id, initiated_by, state) VALUES ('scan', '{}'::jsonb, $1, 'tester', 'running') RETURNING id",
			connection);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Like <see cref="SeedRunAsync"/> but with a caller-controlled <c>initiated_by</c>
	/// (issue #294 ownership tests need to place the recorded initiator relative to
	/// TestAuthHandler's fixed "test-user" identity, including the null/system-run case).
	/// </summary>
	private async Task<Guid> SeedRunAsyncWithInitiator(Guid credentialId, string? initiatedBy)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, credential_id, initiated_by, state) VALUES ('scan', '{}'::jsonb, $1, $2, 'running') RETURNING id",
			connection);
		insert.Parameters.AddWithValue(credentialId);
		insert.Parameters.AddWithValue((object?)initiatedBy ?? DBNull.Value);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedQueuedJobAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id) VALUES ($1, 'scan', 1, 'queued', $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedRunningJobAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, credential_id, claimed_by, claimed_at, lease_expires_at, heartbeat_at)
			VALUES ($1, 'scan', 1, 'running', $2, 'worker-a', now(), now() + interval '5 minutes', now())
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedTerminalJobAsync(Guid runId, Guid credentialId, string terminalState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id, finished_at) VALUES ($1, 'scan', 1, $2, $3, now()) RETURNING id",
			connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(terminalState);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// A <c>failed</c> job carrying a stage marker (issue #297: mirrors ADR-0012 §5's
	/// "a job that fails at, say, converting keeps stage = 'converting' on its failed
	/// row"), optionally with a caller-controlled <paramref name="attemptCount"/> so
	/// retry-accounting tests can prove the manual retry endpoint leaves it untouched.
	/// </summary>
	private async Task<Guid> SeedFailedJobAsync(Guid runId, Guid credentialId, string? stage, int attemptCount = 1)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, credential_id, stage, attempt_count, max_attempts, finished_at, note)
			VALUES ($1, 'scan', 1, 'failed', $2, $3, $4, 5, now(), 'Simulated failure for test')
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		insert.Parameters.AddWithValue((object?)stage ?? DBNull.Value);
		insert.Parameters.AddWithValue(attemptCount);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedBlockedJobAsync(Guid runId, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id, note) VALUES ($1, 'scan', 1, 'blocked', $2, 'Credential queue halted') RETURNING id",
			connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task HaltCredentialAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE credentials SET queue_halted = true, queue_halted_reason = 'test halt', queue_halted_at = now() WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);
		await update.ExecuteNonQueryAsync();
	}

	private async Task MarkRunBlockedAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"UPDATE runs SET blocked = true, blocked_reason = 'test halt' WHERE id = $1", connection);
		update.Parameters.AddWithValue(runId);
		await update.ExecuteNonQueryAsync();
	}

	private async Task<string> ReadJobStateAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		return (string)(await command.ExecuteScalarAsync())!;
	}

	private async Task<(string State, string? Stage)> ReadJobStateAndStageAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state, stage FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
	}

	private async Task<(string State, Guid? CredentialId)> ReadJobStateAndCredentialAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT state, credential_id FROM jobs WHERE id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

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
