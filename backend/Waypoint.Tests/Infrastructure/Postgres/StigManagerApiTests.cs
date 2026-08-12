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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #310 (first slice of the #25 split) end to end against real Postgres: the
/// global STIG Manager connection (<c>/stigman</c>), per-site override resolution
/// (<c>/sites/{id}/stigman</c> -- <c>sites.stigman_override</c> wins over the global
/// row), and <c>/stigman/test</c>'s three outcomes (ok / unreachable / auth-failed)
/// against a stubbed <see cref="IStigManagerProbe"/> -- there is no lab STIG Manager
/// instance this suite can reach, and CLAUDE.md forbids a real lab hostname in a
/// committed fixture, so the live network check is a manual owner step (see
/// <see cref="HttpStigManagerProbe"/>'s doc comment). Fixtures use only fictional
/// placeholder hostnames (stigman.example.internal).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory; Dispose removes the key dir.
public sealed class StigManagerApiTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	/// <summary>Deterministic stub: returns whatever <see cref="Result"/> is set to, recording the connection/secret it was called with for assertions.</summary>
	private sealed class StubStigManagerProbe : IStigManagerProbe
	{
		public StigManagerProbeResult Result { get; set; } = new(true, true, "1.2.3", null);

		public ResolvedStigManagerConnection? LastConnection { get; private set; }

		public string? LastClientSecret { get; private set; }

		public Task<StigManagerProbeResult> ProbeAsync(ResolvedStigManagerConnection connection, string? clientSecret, CancellationToken cancellationToken)
		{
			LastConnection = connection;
			LastClientSecret = clientSecret;
			return Task.FromResult(Result);
		}
	}

	private sealed class StigManagerApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;

		public StubStigManagerProbe Probe { get; } = new();

		public StigManagerApiFactory(string connectionString, string keyPath)
		{
			_connectionString = connectionString;
			_keyPath = keyPath;
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
				services.AddSingleton(new Waypoint.Infrastructure.StigManager.StigManagerRepository(_connectionString));
				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();
				services.AddSingleton<ISecretTracker>(new InPlaySecretRedactor());
				services.AddSingleton<ICredentialSecretStore>(provider => new CredentialSecretStore(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					provider.GetRequiredService<ISecretTracker>(),
					NullLogger<CredentialSecretStore>.Instance));
				services.AddSingleton<ICredentialCreationCoordinator>(provider => new CredentialCreationCoordinator(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					NullLogger<CredentialCreationCoordinator>.Instance));
				services.AddSingleton<IStigManagerProbe>(Probe);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-stigman-key").FullName;
	private StigManagerApiFactory _factory = null!;
	private HttpClient _client = null!;

	public StigManagerApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		_factory = new StigManagerApiFactory(_fixture.ConnectionString, keyPath);
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
		Directory.Delete(_keyDirectory, recursive: true);
	}

	[Fact]
	public async Task NoGlobalConnectionConfigured_Get_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/v1/stigman", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PutThenGet_RoundTripsTheGlobalConnection()
	{
		HttpResponseMessage put = await SendAsync(HttpMethod.Put, "/api/v1/stigman", "Admin", new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "vcf-collection",
			client_id = "waypoint-appliance",
		});
		Assert.Equal(HttpStatusCode.OK, put.StatusCode);

		using JsonDocument putBody = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
		Assert.Equal("https://stigman.example.internal/api", putBody.RootElement.GetProperty("endpoint").GetString());
		Assert.Equal("openid stig-manager:stig:read", putBody.RootElement.GetProperty("scope").GetString());

		HttpResponseMessage get = await SendAsync(HttpMethod.Get, "/api/v1/stigman", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, get.StatusCode);
		using JsonDocument getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal("vcf-collection", getBody.RootElement.GetProperty("collection").GetString());
	}

	[Fact]
	public async Task Put_MissingRequiredField_Is400()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, "/api/v1/stigman", "Admin", new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			// collection and client_id omitted
		});
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task SiteWithNoOverride_ResolvesToGlobal()
	{
		await PutGlobalAsync();
		Guid siteId = await CreateSiteAsync("connected-enclave");

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}/stigman", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("global", body.RootElement.GetProperty("source").GetString());
		Assert.Equal("vcf-collection", body.RootElement.GetProperty("collection").GetString());
	}

	/// <summary>The AC's headline behavior: a site's override wins over the global default.</summary>
	[Fact]
	public async Task SiteWithOverride_ResolvesToTheOverride_NotGlobal()
	{
		await PutGlobalAsync();
		Guid siteId = await CreateSiteAsync("scif-enclave");

		HttpResponseMessage overridePut = await SendAsync(HttpMethod.Put, $"/api/v1/sites/{siteId}/stigman", "Admin", new
		{
			endpoint = "https://stigman-scif.example.internal/api",
			authority = "https://keycloak-scif.example.internal/realms/stigman",
			collection = "scif-collection",
			client_id = "waypoint-scif",
		});
		Assert.Equal(HttpStatusCode.OK, overridePut.StatusCode);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}/stigman", "Viewer", body: null);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("site_override", body.RootElement.GetProperty("source").GetString());
		Assert.Equal("scif-collection", body.RootElement.GetProperty("collection").GetString());
		Assert.Equal("https://stigman-scif.example.internal/api", body.RootElement.GetProperty("endpoint").GetString());
	}

	[Fact]
	public async Task SiteWithNoOverride_AndNoGlobal_Is404()
	{
		Guid siteId = await CreateSiteAsync("unconfigured-enclave");
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}/stigman", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Test_NotConfigured_ReturnsNotConfiguredOutcome_AndNeverCallsTheProbe()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("not_configured", body.RootElement.GetProperty("outcome").GetString());
		Assert.Null(_factory.Probe.LastConnection);
	}

	[Fact]
	public async Task Test_Ok_ReturnsReachableAndAuthOk_WithApiVersion()
	{
		await PutGlobalAsync();
		_factory.Probe.Result = new StigManagerProbeResult(true, true, "1.4.0", null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("ok", body.RootElement.GetProperty("outcome").GetString());
		Assert.True(body.RootElement.GetProperty("reachable").GetBoolean());
		Assert.True(body.RootElement.GetProperty("auth_ok").GetBoolean());
		Assert.Equal("1.4.0", body.RootElement.GetProperty("api_version").GetString());
	}

	[Fact]
	public async Task Test_Unreachable_ReturnsUnreachableOutcome()
	{
		await PutGlobalAsync();
		_factory.Probe.Result = new StigManagerProbeResult(false, false, null, "Connection refused.");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("unreachable", body.RootElement.GetProperty("outcome").GetString());
		Assert.False(body.RootElement.GetProperty("reachable").GetBoolean());
	}

	[Fact]
	public async Task Test_AuthFailed_ReturnsAuthFailedOutcome_WhenReachableButTokenRejected()
	{
		await PutGlobalAsync();
		_factory.Probe.Result = new StigManagerProbeResult(true, false, null, "STIG Manager rejected the token (401).");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("auth_failed", body.RootElement.GetProperty("outcome").GetString());
		Assert.True(body.RootElement.GetProperty("reachable").GetBoolean());
		Assert.False(body.RootElement.GetProperty("auth_ok").GetBoolean());
	}

	/// <summary>The probe's boundary is a stub, but the credential decrypt path is real: a configured credential_id decrypts and its value reaches the probe as clientSecret, never as a response field.</summary>
	[Fact]
	public async Task Test_WithConfiguredCredential_DecryptsTheSecret_AndPassesItToTheProbe_NeverInAnyResponse()
	{
		const string canary = "invented-stigman-client-secret-9f21";
		Guid credentialId = await CreateTokenCredentialAsync(canary);
		await PutGlobalAsync(credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string responseBody = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canary, responseBody, StringComparison.Ordinal);

		Assert.Equal(canary, _factory.Probe.LastClientSecret);
	}

	[Fact]
	public async Task Test_WithConfiguredCredential_ButNoStoredSecret_ReturnsAuthFailed_AndNeverCallsTheProbe()
	{
		Guid credentialId = await CreateTokenCredentialAsync(secret: null);
		await PutGlobalAsync(credentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", "Admin", body: null);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("auth_failed", body.RootElement.GetProperty("outcome").GetString());
		Assert.Null(_factory.Probe.LastConnection);
	}

	/// <summary>
	/// Issue #430: a master-key decrypt failure is distinct from a wrong STIG Manager
	/// credential -- an operator seeing <c>auth_failed</c> would chase credential
	/// rotation for a problem that is actually appliance key remediation. Creates the
	/// credential through the normal (working-key) factory, then points a second
	/// factory sharing the same Postgres at a key file that does not exist -- the real
	/// <see cref="MasterKeyUnavailableException"/> from production
	/// <see cref="FileMasterKeyProvider"/>, not a fault-injected stand-in (mirrors
	/// <c>CredentialsApiTests.CreateWithSecret_NoMasterKeyConfigured_Is503MasterKeyUnavailable</c>).
	/// </summary>
	[Fact]
	public async Task Test_MasterKeyUnavailable_ReturnsMasterKeyUnavailableOutcome_NotAuthFailed_AndNeverCallsTheProbe()
	{
		Guid credentialId = await CreateTokenCredentialAsync("invented-stigman-client-secret-mk1");
		await PutGlobalAsync(credentialId);

		string missingKeyPath = Path.Combine(_keyDirectory, "does-not-exist.key");
		using StigManagerApiFactory noKeyFactory = new(_fixture.ConnectionString, missingKeyPath);
		using HttpClient noKeyClient = noKeyFactory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/stigman/test");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await noKeyClient.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("master_key_unavailable", body.RootElement.GetProperty("outcome").GetString());
		Assert.False(body.RootElement.GetProperty("auth_ok").GetBoolean());
		Assert.Null(noKeyFactory.Probe.LastConnection);

		// The detailed exception message (env var name, key path) never reaches the wire.
		string responseBody = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(missingKeyPath, responseBody, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task WriteEndpoints_BelowAdmin_Return403(string role)
	{
		Guid siteId = await CreateSiteAsync($"role-gate-{Guid.NewGuid():N}");

		HttpResponseMessage putGlobal = await SendAsync(HttpMethod.Put, "/api/v1/stigman", role, new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "c",
			client_id = "cid",
		});
		Assert.Equal(HttpStatusCode.Forbidden, putGlobal.StatusCode);

		HttpResponseMessage putSite = await SendAsync(HttpMethod.Put, $"/api/v1/sites/{siteId}/stigman", role, new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "c",
			client_id = "cid",
		});
		Assert.Equal(HttpStatusCode.Forbidden, putSite.StatusCode);

		HttpResponseMessage test = await SendAsync(HttpMethod.Post, "/api/v1/stigman/test", role, body: null);
		Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
	}

	[Fact]
	public async Task ReadEndpoints_WithViewerRole_Return200_NotForbidden()
	{
		await PutGlobalAsync();
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/v1/stigman", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Unauthenticated_Is401()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/stigman");
		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private async Task PutGlobalAsync(Guid? credentialId = null)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, "/api/v1/stigman", "Admin", new
		{
			endpoint = "https://stigman.example.internal/api",
			authority = "https://keycloak.example.internal/realms/stigman",
			collection = "vcf-collection",
			client_id = "waypoint-appliance",
			credential_id = credentialId,
		});
		response.EnsureSuccessStatusCode();
	}

	private async Task<Guid> CreateSiteAsync(string name)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin", new { name });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> CreateTokenCredentialAsync(string? secret)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/credentials", "Admin", new
		{
			name = $"stigman-client-{Guid.NewGuid():N}",
			credential_type = "token",
			secret,
		});
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
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE stigman_connections, targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
