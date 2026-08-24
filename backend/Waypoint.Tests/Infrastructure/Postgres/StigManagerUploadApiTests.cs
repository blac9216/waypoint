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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #311 end to end against real Postgres and a real (temp-directory) artifact
/// store: <c>POST /jobs/{id}/stigman-upload-retry</c> against a stubbed
/// <see cref="IStigManagerUploadClient"/> (no lab STIG Manager instance reachable from
/// CI -- see that interface's doc comment) covering uploaded/conflict/failed outcomes,
/// the "never fails the caller" contract on a repeat failure, and the two 4xx guard
/// rails (non-scan job_type, missing CKL artifact). The convert-stage upload path
/// itself (the first-attempt half of this issue) is proven by
/// <c>ScanUploadCoordinator</c>'s direct unit coverage below plus
/// <c>ScanJobHandlerEndToEndTests</c>' existing full-pipeline run (which never
/// configures a STIG Manager connection, so it exercises the "no connection configured"
/// degrade path of the same coordinator).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory and removes the temp dir.
public sealed class StigManagerUploadApiTests : IAsyncLifetime, IDisposable
{
	/// <summary>Deterministic stub: returns whatever <see cref="Result"/> is set to, recording the CKL path it was called with.</summary>
	private sealed class StubStigManagerUploadClient : IStigManagerUploadClient
	{
		public StigManagerUploadResult Result { get; set; } = new(StigManagerUploadOutcome.Uploaded, null);

		public string? LastCklPath { get; private set; }

		public int CallCount { get; private set; }

		public Task<StigManagerUploadResult> UploadCklAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken)
		{
			CallCount++;
			LastCklPath = cklPath;
			return Task.FromResult(Result);
		}

		public Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken) =>
			Task.FromResult(fallback);
	}

	private sealed class UploadApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _artifactStorePath;

		public StubStigManagerUploadClient UploadClient { get; } = new();

		public UploadApiFactory(string connectionString, string artifactStorePath)
		{
			_connectionString = connectionString;
			_artifactStorePath = artifactStorePath;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Scans:ArtifactStorePath"] = _artifactStorePath,
				});
			});

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
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
				{
					ServiceDescriptor? jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (jobsDescriptor != null)
					{
						services.Remove(jobsDescriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());

				// Real StigManagerRepository backed by the same Postgres, but with a
				// stubbed HTTP boundary -- the site created in each test writes a
				// stigman_override so ResolveForSiteAsync finds a connection to hand
				// the stub, exercising the full resolve path without any real network.
				services.AddSingleton(new Waypoint.Infrastructure.StigManager.StigManagerRepository(_connectionString));
				services.AddSingleton<IStigManagerUploadClient>(UploadClient);

				// The base host's ICredentialSecretStore/IMasterKeyProvider come from
				// appsettings.json's ConnectionStrings:Waypoint (Host=postgres), which
				// this sandbox cannot resolve -- issue #430's regression test needs
				// ScanUploadCoordinator's decrypt call to reach the *test* Postgres
				// (Retry_MasterKeyUnavailable_* deliberately configures no key file, so
				// FileMasterKeyProvider.GetKey() throws the real
				// MasterKeyUnavailableException; every other test here never sets a
				// credential_id, so this override is a silent no-op for them).
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(keyFilePath: null));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();
				services.AddSingleton<ISecretTracker>(new InPlaySecretRedactor());
				services.AddSingleton<ICredentialSecretStore>(provider => new CredentialSecretStore(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					provider.GetRequiredService<ISecretTracker>(),
					NullLogger<CredentialSecretStore>.Instance));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _artifactStorePath = Directory.CreateTempSubdirectory("wp-stigman-upload-api").FullName;
	private UploadApiFactory _factory = null!;
	private HttpClient _client = null!;

	public StigManagerUploadApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new UploadApiFactory(_fixture.ConnectionString, _artifactStorePath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		Directory.Delete(_artifactStorePath, recursive: true);
	}

	[Fact]
	public async Task Retry_Uploaded_SetsUploadStatusAndReturnsIt()
	{
		(_, Guid targetId) = await CreateSiteWithStigManagerAsync("retry-ok-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "failed");
		WriteCkl(jobId);
		_factory.UploadClient.Result = new StigManagerUploadResult(StigManagerUploadOutcome.Uploaded, null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("uploaded", document.RootElement.GetProperty("upload_status").GetString());
		Assert.Equal("uploaded", await GetJobFieldAsync(jobId, "upload_status"));
		Assert.Equal(1, _factory.UploadClient.CallCount);
	}

	[Fact]
	public async Task Retry_Conflict_SetsConflictStatus()
	{
		(_, Guid targetId) = await CreateSiteWithStigManagerAsync("retry-conflict-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "uploaded");
		WriteCkl(jobId);
		_factory.UploadClient.Result = new StigManagerUploadResult(StigManagerUploadOutcome.Conflict, "already exists");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("conflict", document.RootElement.GetProperty("upload_status").GetString());
		Assert.Equal("conflict", await GetJobFieldAsync(jobId, "upload_status"));
	}

	[Fact]
	public async Task Retry_RepeatFailure_Returns200NotError()
	{
		// Issue #311 AC: a retry that fails again must never surface as a 5xx -- the
		// caller (Results screen) needs to render the fresh failure, not a crash.
		(_, Guid targetId) = await CreateSiteWithStigManagerAsync("retry-fail-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "failed");
		WriteCkl(jobId);
		_factory.UploadClient.Result = new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "STIG Manager returned 503.");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("failed", document.RootElement.GetProperty("upload_status").GetString());
		Assert.Equal("failed", await GetJobFieldAsync(jobId, "upload_status"));
	}

	/// <summary>
	/// Issue #430: a master-key decrypt failure on the site's STIG Manager credential
	/// must fail closed with the master-key cause recorded -- not proceed secret-less
	/// to <see cref="IStigManagerUploadClient"/>, which would only ever produce a
	/// misleading remote-auth failure with the real cause invisible. No
	/// <c>WAYPOINT_MASTER_KEY_FILE</c> is configured in this factory (the Testing
	/// environment leaves it unset), so <c>ScanUploadCoordinator</c>'s decrypt call
	/// throws the real <c>MasterKeyUnavailableException</c> from production
	/// <c>FileMasterKeyProvider</c>, not a fault-injected stand-in -- exercised via a
	/// credential-bearing connection (a bare row insert stands in for the ciphertext:
	/// the master key never loads far enough to read it).
	/// </summary>
	[Fact]
	public async Task Retry_MasterKeyUnavailable_RecordsMasterKeyCause_NotRemoteAuthFailure()
	{
		(_, Guid targetId) = await CreateSiteWithStigManagerAndCredentialAsync("retry-mk-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "failed");
		WriteCkl(jobId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("failed", document.RootElement.GetProperty("upload_status").GetString());
		string? detail = document.RootElement.GetProperty("upload_detail").GetString();
		Assert.Contains("master key", detail, StringComparison.OrdinalIgnoreCase);
		Assert.Equal("failed", await GetJobFieldAsync(jobId, "upload_status"));

		// The upload client is never called secret-less -- the coordinator fails
		// closed before reaching it.
		Assert.Equal(0, _factory.UploadClient.CallCount);

		// No key path or env-var detail crosses the wire (matches #409's hygiene rule).
		Assert.DoesNotContain("WAYPOINT_MASTER_KEY_FILE", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Retry_NoCklArtifact_Is409()
	{
		(_, Guid targetId) = await CreateSiteWithStigManagerAsync("retry-no-ckl-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "failed");
		// No WriteCkl call -- the CKL was never produced (or was pruned).

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(0, _factory.UploadClient.CallCount);
	}

	[Fact]
	public async Task Retry_NonScanJob_Is400()
	{
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutJobAsync(runId, jobType: "download", state: "failed");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Retry_UnknownJob_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{Guid.NewGuid()}/stigman-upload-retry", "Operator", body: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Retry_ViewerRole_Is403()
	{
		(_, Guid targetId) = await CreateSiteWithStigManagerAsync("retry-viewer-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, "failed");
		WriteCkl(jobId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/stigman-upload-retry", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private void WriteCkl(Guid jobId)
	{
		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.ckl");
		File.WriteAllText(path, "<CHECKLIST><STIGS/></CHECKLIST>");
	}

	/// <summary>
	/// Same shape as <see cref="CreateSiteWithStigManagerAsync"/> but with a
	/// <c>credential_id</c> on the override, so <c>ScanUploadCoordinator</c> attempts a
	/// decrypt instead of taking the no-credential/public-client path. The
	/// <c>credential_secrets</c> row is a bare placeholder -- with no master key
	/// configured in this factory, <c>FileMasterKeyProvider.GetKey()</c> throws before
	/// the ciphertext bytes are ever read, so their content is irrelevant.
	/// </summary>
	private async Task<(Guid SiteId, Guid TargetId)> CreateSiteWithStigManagerAndCredentialAsync(string namePrefix)
	{
		SiteRepository sites = new(_fixture.ConnectionString);
		TargetRepository targets = new(_fixture.ConnectionString);

		Guid credentialId;
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insertCredential = new(
				"INSERT INTO credentials (name, credential_type) VALUES ($1, 'token') RETURNING id", connection);
			insertCredential.Parameters.AddWithValue($"{namePrefix}-credential-{Guid.NewGuid():N}");
			credentialId = (Guid)(await insertCredential.ExecuteScalarAsync())!;

			await using NpgsqlCommand insertSecret = new(
				"""
				INSERT INTO credential_secrets (credential_id, ciphertext, data_key_wrapped, master_key_id, algorithm)
				VALUES ($1, $2, $3, 'wpk-invented-placeholder', 'AES-256-GCM')
				""", connection);
			insertSecret.Parameters.AddWithValue(credentialId);
			insertSecret.Parameters.AddWithValue(new byte[] { 0x00 });
			insertSecret.Parameters.AddWithValue(new byte[] { 0x00 });
			await insertSecret.ExecuteNonQueryAsync();
		}

		Guid siteId = (await sites.CreateAsync($"{namePrefix}-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;

		string overrideJson = JsonSerializer.Serialize(new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "vcf-collection",
			client_id = "waypoint-appliance",
			credential_id = credentialId,
		});
		await sites.UpdateAsync(siteId, name: null, description: null, stigmanOverrideJson: overrideJson, clearDescription: false, clearStigmanOverride: false, CancellationToken.None);

		(Waypoint.Core.Sites.TargetWriteOutcome outcome, Guid? targetId) = await targets.CreateAsync(
			siteId, "vsphere", $"{namePrefix}-{Guid.NewGuid():N}", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);
		Assert.Equal(Waypoint.Core.Sites.TargetWriteOutcome.Ok, outcome);
		return (siteId, targetId!.Value);
	}

	private async Task<(Guid SiteId, Guid TargetId)> CreateSiteWithStigManagerAsync(string namePrefix)
	{
		SiteRepository sites = new(_fixture.ConnectionString);
		TargetRepository targets = new(_fixture.ConnectionString);

		Guid siteId = (await sites.CreateAsync($"{namePrefix}-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;

		// A site-level stigman_override with no credential_id (public-client style) so
		// StigManagerRepository.ResolveForSiteAsync finds a connection and the
		// coordinator calls the stubbed upload client instead of short-circuiting on
		// "no connection configured".
		string overrideJson = JsonSerializer.Serialize(new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "vcf-collection",
			client_id = "waypoint-appliance",
		});
		await sites.UpdateAsync(siteId, name: null, description: null, stigmanOverrideJson: overrideJson, clearDescription: false, clearStigmanOverride: false, CancellationToken.None);

		(Waypoint.Core.Sites.TargetWriteOutcome outcome, Guid? targetId) = await targets.CreateAsync(
			siteId, "vsphere", $"{namePrefix}-{Guid.NewGuid():N}", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);
		Assert.Equal(Waypoint.Core.Sites.TargetWriteOutcome.Ok, outcome);
		return (siteId, targetId!.Value);
	}

	private async Task<Guid> CreateRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, initiated_by) VALUES ('scan', '{}', 'tester') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private Task<Guid> FanOutScanJobAsync(Guid runId, Guid targetId, string state) => FanOutJobAsync(runId, "scan", state, targetId);

	private async Task<Guid> FanOutJobAsync(Guid runId, string jobType, string state, Guid? targetId = null)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO jobs (run_id, job_type, target_id, target_name, priority, state, payload)
			VALUES ($1, $2, $3, $4, 3, $5, $6::jsonb)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(jobType);
		insert.Parameters.AddWithValue((object?)targetId ?? DBNull.Value);
		insert.Parameters.AddWithValue(targetId is null ? "no-target" : $"target-{targetId:N}");
		insert.Parameters.AddWithValue(state);
		insert.Parameters.AddWithValue(targetId is null ? "{}" : JsonSerializer.Serialize(new { target_id = targetId }));
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT COALESCE({field}::text, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
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
			"TRUNCATE TABLE config_versions, config_docs, jobs, runs, targets, sites RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
