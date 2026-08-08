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
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #276 (fourth slice of the #23 split): the ADR-0011 ad hoc "my credentials"
/// scan flow. <see cref="CreateScanRun_WithEphemeralCredential_NeverPersisted_CanaryProof"/>
/// is the heart of the slice -- a redactor-canary-style test that runs an ad hoc scan
/// run end to end through the real API and Postgres, then greps every persistence
/// surface (<c>credentials</c>, <c>credential_secrets</c>, <c>jobs.payload</c>,
/// <c>jobs.credential_id</c>, <c>job_events</c>, <c>audit_log.detail</c>, and the
/// process's own captured log lines) for the canary secret and asserts zero hits.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class EphemeralCredentialScanRunTests : IAsyncLifetime
{
	private sealed class EphemeralCredentialApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public CapturingLogger<EphemeralCredentialCache> CacheLogger { get; } = new();

		public EphemeralCredentialApiFactory(string connectionString)
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

				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));

				var jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IJobQueueRepository));
				if (jobsDescriptor != null)
				{
					services.Remove(jobsDescriptor);
				}

				services.AddSingleton<IJobQueueRepository>(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));

				// Swap in a capturing logger for the cache so the canary test can assert
				// the cache's own log lines (job-id/actor only, per its doc comment)
				// never carry the secret either -- and swap the cache itself for one
				// built against that logger so both sides see the same instance.
				var cacheDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEphemeralCredentialCache));
				if (cacheDescriptor != null)
				{
					services.Remove(cacheDescriptor);
				}

				services.AddSingleton<IEphemeralCredentialCache>(serviceProvider => new EphemeralCredentialCache(
					serviceProvider.GetRequiredService<Waypoint.Core.Logging.ISecretTracker>(), _connectionString, CacheLogger));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private EphemeralCredentialApiFactory _factory = null!;
	private HttpClient _client = null!;

	public EphemeralCredentialScanRunTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new EphemeralCredentialApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	/// <summary>
	/// The proof test (AC): the ephemeral secret supplied at run initiation touches
	/// none of the persistence surfaces a stored/decrypted secret would -- not
	/// <c>credentials</c>, not <c>credential_secrets</c>, not <c>jobs.payload</c>, not
	/// <c>jobs.credential_id</c> (which stays NULL -- there is no row to reference),
	/// not <c>job_events</c>, not <c>audit_log.detail</c>, and not the cache's own log
	/// lines. It DOES reach the in-memory <see cref="IEphemeralCredentialCache"/>,
	/// exactly once per fanned-out job, and a best-effort
	/// <c>secret.ephemeral_registered</c> audit row with <c>credential_id NULL</c>.
	/// </summary>
	[Fact]
	public async Task CreateScanRun_WithEphemeralCredential_NeverPersisted_CanaryProof()
	{
		const string canarySecret = "invented-adhoc-canary-a1b2c3"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface
		const string canaryUsername = "adhoc-operator@example.internal";

		Guid siteId = await CreateSiteAsync("adhoc-canary-site");
		Guid target = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId }),
			credential = new { kind = "personal", username = canaryUsername, secret = canarySecret },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", "Viewer", body: null);
		using JsonDocument jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement[] rows = [.. jobs.RootElement.EnumerateArray()];
		Assert.Single(rows);
		Guid jobId = rows[0].GetProperty("id").GetGuid();

		// The response body itself must never carry the secret (write-only API,
		// security.md control 3).
		string responseBody = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canarySecret, responseBody, StringComparison.Ordinal);
		string jobsBody = await jobsResponse.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canarySecret, jobsBody, StringComparison.Ordinal);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// jobs: neither payload nor credential_id carries the secret; credential_id is
		// NULL outright for an ad hoc job (no stored row exists to reference).
		await using (NpgsqlCommand jobRow = new(
			"SELECT payload::text, credential_id FROM jobs WHERE id = $1", connection))
		{
			jobRow.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await jobRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.DoesNotContain(canarySecret, reader.GetString(0), StringComparison.Ordinal);
			Assert.True(reader.IsDBNull(1), "an ad hoc job's credential_id must be NULL -- never a stored row reference.");
		}

		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM jobs WHERE payload::text LIKE '%' || $1 || '%' OR note LIKE '%' || $1 || '%'", canarySecret);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", canarySecret);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM audit_log WHERE detail::text LIKE '%' || $1 || '%'", canarySecret);

		// credentials / credential_secrets: the ADR-0011 headline claim -- an ad hoc run
		// creates ZERO rows in either table. Not "redacted", not "one row with a
		// scrubbed value" -- absent.
		await using (NpgsqlCommand credentialCount = new("SELECT count(*) FROM credentials", connection))
		{
			Assert.Equal(0L, (long)(await credentialCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand secretCount = new("SELECT count(*) FROM credential_secrets", connection))
		{
			Assert.Equal(0L, (long)(await secretCount.ExecuteScalarAsync())!);
		}

		// audit_log: the best-effort intake row landed, attributed, with no stored
		// credential to reference.
		await using (NpgsqlCommand auditRow = new(
			"SELECT credential_id, job_id, run_id, actor FROM audit_log WHERE event_type = 'secret.ephemeral_registered' AND job_id = $1", connection))
		{
			auditRow.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await auditRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "expected a secret.ephemeral_registered audit row for the ad hoc job.");
			Assert.True(reader.IsDBNull(0), "audit_log.credential_id must be NULL for an ephemeral registration.");
			Assert.Equal(runId, reader.GetGuid(2));
			Assert.Equal("test-user", reader.GetString(3));
		}

		// Captured log lines from the cache itself: job id and actor are fine to log,
		// the secret value is not.
		foreach (CapturedLogEntry entry in _factory.CacheLogger.Entries)
		{
			Assert.DoesNotContain(canarySecret, entry.Message, StringComparison.Ordinal);
		}

		// The cache DID receive it -- this is the one place it is allowed to live, and
		// only until first consumption.
		IEphemeralCredentialCache cache = _factory.Services.GetRequiredService<IEphemeralCredentialCache>();
		EphemeralCredential? taken = cache.TryTake(jobId);
		Assert.NotNull(taken);
		Assert.Equal(canaryUsername, taken!.Username);
		Assert.Equal(canarySecret, taken.Secret);

		// Single-shot: a second take (e.g. a naive resume path) finds nothing, which is
		// the documented fail-closed behavior for a resumed job with no cached secret.
		Assert.Null(cache.TryTake(jobId));
	}

	[Fact]
	public async Task CreateScanRun_WithEphemeralCredential_BelowOperatorRole_Returns403()
	{
		Guid siteId = await CreateSiteAsync("adhoc-role-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId }),
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM runs", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task CreateScanRun_WithEphemeralCredentialAndCredentialId_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-mutex-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId }),
			credential_id = Guid.NewGuid(),
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithEphemeralCredential_UnsupportedKind_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-kind-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId }),
			credential = new { kind = "vault-wrapped", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithEphemeralCredential_NotAScanRun_Returns400()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "download",
			scope = "{}",
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	private static async Task AssertNoCanaryAsync(NpgsqlConnection connection, string sql, string canary)
	{
		await using NpgsqlCommand command = new(sql, connection);
		command.Parameters.AddWithValue(canary);
		Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
		if (!response.IsSuccessStatusCode)
		{
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"CreateSiteAsync failed: {(int)response.StatusCode} {errorBody}");
		}

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> CreateTargetAsync(Guid siteId, string kind, string name, string connectionJson)
	{
		using JsonDocument connectionDocument = JsonDocument.Parse(connectionJson);
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		string body = JsonSerializer.Serialize(new { kind, name, connection = connectionDocument.RootElement });
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");

		HttpResponseMessage response = await _client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
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
			"TRUNCATE TABLE job_events, audit_log, jobs, runs, targets, sites, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
