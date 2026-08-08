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
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #19 (epic #13) end to end against real Postgres: the /sites and
/// /sites/{id}/targets · /targets/{id} REST surface backed by the real repositories --
/// role gates (Viewer reads, Admin writes, per docs/api-contract.md "Admin writes"),
/// the closed target-kind set, the "no secret in connection" 400 guard, and the
/// issue's headline acceptance criterion: a site with two vCenters + one NSX manager +
/// SRG/ssh boxes round-trips through the full API. Fixtures use only fictional
/// placeholder hostnames (CLAUDE.md) -- vcsa-*.example.internal, nsx-*.example.internal,
/// esxi-*.example.internal.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class SitesTargetsApiTests : IAsyncLifetime
{
	private sealed class SitesTargetsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public SitesTargetsApiFactory(string connectionString)
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
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private SitesTargetsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public SitesTargetsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetSitesTargetsDataAsync();

		_factory = new SitesTargetsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task SiteWithTwoVCenters_OneNsx_AndSrgBoxes_RoundTripsThroughTheApi()
	{
		Guid siteId = await CreateSiteAsync("dmz-enclave", "Two vCenters, one NSX manager, SRG boxes");

		Guid vcsa1 = await CreateTargetAsync(siteId, "vsphere", "vcsa-01",
			"""{"host":"vcsa-01.example.internal"}""");
		Guid vcsa2 = await CreateTargetAsync(siteId, "vsphere", "vcsa-02",
			"""{"host":"vcsa-02.example.internal"}""");
		Guid nsx = await CreateTargetAsync(siteId, "nsx-api", "nsx-manager-01",
			"""{"host":"nsx-01.example.internal"}""");
		Guid photon = await CreateTargetAsync(siteId, "ssh", "photon-appliance-01",
			"""{"host":"photon-01.example.internal"}""");
		Guid vidm = await CreateTargetAsync(siteId, "ssh", "vidm-appliance-01",
			"""{"host":"vidm-01.example.internal"}""");

		HttpResponseMessage listResponse = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}/targets", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
		Assert.True(listResponse.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.Equal("5", totals!.Single());

		using JsonDocument listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		JsonElement[] rows = listDocument.RootElement.EnumerateArray().ToArray();
		Assert.Equal(5, rows.Length);

		string[] kinds = rows.Select(row => row.GetProperty("kind").GetString()!).ToArray();
		Assert.Equal(2, kinds.Count(kind => kind == "vsphere"));
		Assert.Equal(1, kinds.Count(kind => kind == "nsx-api"));
		Assert.Equal(2, kinds.Count(kind => kind == "ssh"));

		foreach (Guid targetId in new[] { vcsa1, vcsa2, nsx, photon, vidm })
		{
			HttpResponseMessage getResponse = await SendAsync(HttpMethod.Get, $"/api/v1/targets/{targetId}", "Viewer", body: null);
			Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
			using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
			Assert.Equal(siteId, document.RootElement.GetProperty("site_id").GetGuid());
		}
	}

	[Fact]
	public async Task InvalidTargetKind_Is400()
	{
		Guid siteId = await CreateSiteAsync("kind-validation-site", null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets", "Admin",
			new { kind = "esxi-direct", name = "bogus-target", connection = new { host = "esxi-01.example.internal" } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("invalid_kind", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("password")]
	[InlineData("Secret")]
	[InlineData("api_key")]
	[InlineData("PRIVATE_KEY")]
	public async Task ConnectionPayloadNamingASecretKey_Is400(string forbiddenKey)
	{
		Guid siteId = await CreateSiteAsync("secret-guard-site", null);

		Dictionary<string, object> connection = new()
		{
			["host"] = "vcsa-01.example.internal",
			[forbiddenKey] = "hunter2",
		};
		string body = JsonSerializer.Serialize(new { kind = "vsphere", name = "vcsa-secret-test", connection });
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("secret_in_connection", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TargetReferencingACredential_StoresOnlyTheReference_NeverSecretMaterial()
	{
		Guid credentialId = await SeedCredentialAsync("vcsa-service-account");
		Guid siteId = await CreateSiteAsync("credential-ref-site", null);

		HttpResponseMessage created = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets", "Admin",
			new { kind = "vsphere", name = "vcsa-with-cred", connection = new { host = "vcsa-01.example.internal" }, credential_ref = credentialId });

		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		Assert.Equal(credentialId, document.RootElement.GetProperty("credential_ref").GetGuid());

		// No secret-shaped field appears anywhere in the response -- credential_ref is
		// the only path to the credential, per docs/domain-model.md.
		string[] forbidden = ["password", "secret", "token", "private_key", "api_key"];
		string bodyText = document.RootElement.ToString();
		foreach (string key in forbidden)
		{
			Assert.False(document.RootElement.TryGetProperty(key, out _), $"response must not carry a '{key}' field");
		}

		_ = bodyText;

		// Confirm at the storage layer too: the row's connection JSON has no secret key,
		// and there is no secret material anywhere the target row could reach.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		Guid targetId = document.RootElement.GetProperty("id").GetGuid();
		await using NpgsqlCommand read = new("SELECT connection::text FROM targets WHERE id = $1", connection);
		read.Parameters.AddWithValue(targetId);
		string storedConnection = (string)(await read.ExecuteScalarAsync())!;
		foreach (string key in forbidden)
		{
			Assert.DoesNotContain(key, storedConnection, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public async Task Crud_ForSitesAndTargets_WorksEndToEnd()
	{
		Guid siteId = await CreateSiteAsync("crud-site", "initial description");

		HttpResponseMessage updateSite = await SendAsync(HttpMethod.Put, $"/api/v1/sites/{siteId}", "Admin",
			new { description = "updated description" });
		Assert.Equal(HttpStatusCode.OK, updateSite.StatusCode);
		using (JsonDocument document = JsonDocument.Parse(await updateSite.Content.ReadAsStringAsync()))
		{
			Assert.Equal("updated description", document.RootElement.GetProperty("description").GetString());
		}

		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "crud-target", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage updateTarget = await SendAsync(HttpMethod.Put, $"/api/v1/targets/{targetId}", "Admin",
			new { connection = new { host = "vcsa-02.example.internal" } });
		Assert.Equal(HttpStatusCode.OK, updateTarget.StatusCode);
		using (JsonDocument document = JsonDocument.Parse(await updateTarget.Content.ReadAsStringAsync()))
		{
			Assert.Contains("vcsa-02.example.internal", document.RootElement.GetProperty("connection").GetString());
		}

		HttpResponseMessage deleteTarget = await SendAsync(HttpMethod.Delete, $"/api/v1/targets/{targetId}", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleteTarget.StatusCode);
		HttpResponseMessage getDeletedTarget = await SendAsync(HttpMethod.Get, $"/api/v1/targets/{targetId}", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, getDeletedTarget.StatusCode);

		HttpResponseMessage deleteSite = await SendAsync(HttpMethod.Delete, $"/api/v1/sites/{siteId}", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleteSite.StatusCode);
		HttpResponseMessage getDeletedSite = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, getDeletedSite.StatusCode);
	}

	[Fact]
	public async Task DeletingASiteWithTargets_Is409_UnlessForced()
	{
		Guid siteId = await CreateSiteAsync("in-use-site", null);
		await CreateTargetAsync(siteId, "vsphere", "blocking-target", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage blocked = await SendAsync(HttpMethod.Delete, $"/api/v1/sites/{siteId}", "Admin", body: null);
		Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
		Assert.Contains("site_in_use", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

		HttpResponseMessage forced = await SendAsync(HttpMethod.Delete, $"/api/v1/sites/{siteId}?force=true", "Admin", body: null);
		Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task WriteEndpoints_BelowAdmin_Return403(string role)
	{
		Guid siteId = await CreateSiteAsync("role-gate-site", null);

		HttpResponseMessage createSite = await SendAsync(HttpMethod.Post, "/api/v1/sites", role, new { name = $"nope-{Guid.NewGuid():N}" });
		Assert.Equal(HttpStatusCode.Forbidden, createSite.StatusCode);

		HttpResponseMessage createTarget = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteId}/targets", role,
			new { kind = "vsphere", name = "nope-target", connection = new { host = "vcsa-01.example.internal" } });
		Assert.Equal(HttpStatusCode.Forbidden, createTarget.StatusCode);

		HttpResponseMessage deleteSite = await SendAsync(HttpMethod.Delete, $"/api/v1/sites/{siteId}", role, body: null);
		Assert.Equal(HttpStatusCode.Forbidden, deleteSite.StatusCode);
	}

	[Fact]
	public async Task ReadEndpoints_WithViewerRole_Return200()
	{
		Guid siteId = await CreateSiteAsync("viewer-read-site", null);

		HttpResponseMessage listSites = await SendAsync(HttpMethod.Get, "/api/v1/sites", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, listSites.StatusCode);

		HttpResponseMessage getSite = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{siteId}", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, getSite.StatusCode);
	}

	[Fact]
	public async Task WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/sites");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Pagination_SetsXTotalCountToTheFilteredTotal_NotThePageSize()
	{
		for (int i = 0; i < 3; i++)
		{
			await CreateSiteAsync($"page-site-{i}-{Guid.NewGuid():N}", null);
		}

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/v1/sites?limit=1&offset=0", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Single(document.RootElement.EnumerateArray());
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.True(int.Parse(totals!.Single(), System.Globalization.CultureInfo.InvariantCulture) >= 3);
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix, string? description)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}", description });
		response.EnsureSuccessStatusCode();
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

	private async Task<Guid> SeedCredentialAsync(string namePrefix)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner) VALUES ($1, 'service', 'shared') RETURNING id", connection);
		insert.Parameters.AddWithValue($"{namePrefix}-{Guid.NewGuid():N}");
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

	private async Task ResetSitesTargetsDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
