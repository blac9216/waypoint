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
using Npgsql;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1517 (epic #1180, migration 0103) end to end against real Postgres:
/// <c>/api/v1/repo-credentials</c>'s store-binding CRUD, role gates (the issue's own
/// AC: "A non-Admin cannot create, read, or rotate a repo-serving credential"), and
/// the store/type validation the underlying <see cref="RepoCredentialBindingRepository"/>
/// enforces. Uses the same <see cref="TestAuthHandler"/> role-gate idiom
/// <see cref="SitesTargetsApiTests"/> established for its own
/// <c>target_credential_bindings</c> binding surface (inlined here rather than
/// subclassing <c>RoleGuardedApiFactory</c>, which is sealed).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class RepoCredentialsApiTests : IAsyncLifetime
{
	private sealed class RepoCredentialsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public RepoCredentialsApiFactory(string connectionString)
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

				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton(new RepoCredentialBindingRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private RepoCredentialsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public RepoCredentialsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new RepoCredentialsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task SetThenGet_RoundTripsTheBinding()
	{
		Guid credentialId = await SeedCredentialAsync("depot-repo-cred", "repo-basic-auth");

		HttpResponseMessage set = await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/depot", "Admin", new { credential_ref = credentialId });
		Assert.Equal(HttpStatusCode.OK, set.StatusCode);

		HttpResponseMessage get = await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials/depot", "Admin", body: null);
		Assert.Equal(HttpStatusCode.OK, get.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal("depot", document.RootElement.GetProperty("store").GetString());
		Assert.Equal(credentialId, document.RootElement.GetProperty("credential_ref").GetGuid());

		// The write-only contract holds here too: no secret-shaped field on the binding response.
		Assert.False(document.RootElement.TryGetProperty("secret", out _));
	}

	[Fact]
	public async Task List_ReturnsOnlyStoresWithABinding()
	{
		Guid credentialId = await SeedCredentialAsync("umds-repo-cred", "repo-basic-auth");
		await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/umds", "Admin", new { credential_ref = credentialId });

		HttpResponseMessage list = await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials", "Admin", body: null);
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
		Assert.Equal(1, document.RootElement.GetArrayLength());
		Assert.Equal("umds", document.RootElement[0].GetProperty("store").GetString());
	}

	[Fact]
	public async Task Get_UnknownStore_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials/made-up-store", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Set_UnknownStore_Is400()
	{
		Guid credentialId = await SeedCredentialAsync("bad-store-cred", "repo-basic-auth");

		HttpResponseMessage response = await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/made-up-store", "Admin", new { credential_ref = credentialId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("invalid_store", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Set_IncompatibleCredentialType_Is400()
	{
		Guid tokenCredentialId = await SeedCredentialAsync("wrong-type-cred", "token");

		HttpResponseMessage response = await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/photon", "Admin", new { credential_ref = tokenCredentialId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("incompatible_credential_type", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Clear_RemovesTheBinding()
	{
		Guid credentialId = await SeedCredentialAsync("vks-repo-cred", "repo-basic-auth");
		await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/vks", "Admin", new { credential_ref = credentialId });

		HttpResponseMessage clear = await SendAsync(HttpMethod.Delete, "/api/v1/repo-credentials/vks", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

		HttpResponseMessage get = await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials/vks", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
	}

	[Fact]
	public async Task Clear_NoExistingBinding_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, "/api/v1/repo-credentials/vmtools", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>Issue #1517 AC: "A non-Admin cannot create, read, or rotate a repo-serving credential" -- every endpoint on this controller (list/get/set/clear) is Admin-only, stricter than <c>TargetsController</c>'s own Viewer-readable binding reads.</summary>
	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task EveryEndpoint_BelowAdmin_Returns403(string role)
	{
		Guid credentialId = await SeedCredentialAsync("role-gate-cred", "repo-basic-auth");
		await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/content-libraries", "Admin", new { credential_ref = credentialId });

		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials", role, body: null)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/repo-credentials/content-libraries", role, body: null)).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Put, "/api/v1/repo-credentials/content-libraries", role, new { credential_ref = credentialId })).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, "/api/v1/repo-credentials/content-libraries", role, body: null)).StatusCode);
	}

	private async Task<Guid> SeedCredentialAsync(string namePrefix, string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner) VALUES ($1, $2, 'shared') RETURNING id", connection);
		insert.Parameters.AddWithValue($"{namePrefix}-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);
		return (Guid)(await insert.ExecuteScalarAsync())!;
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
			"TRUNCATE TABLE repo_credential_bindings, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
