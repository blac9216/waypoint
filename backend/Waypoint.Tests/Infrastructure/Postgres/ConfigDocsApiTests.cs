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
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #265 end to end against real Postgres: the /config-docs REST surface --
/// role gates (Viewer reads, Admin writes), write-a-new-version semantics, list-with-
/// author-and-timestamp, fetch-specific-@vN-and-latest, and the invalid-YAML 400 /
/// valid-YAML byte-stable-round-trip acceptance criteria. Fixtures use only fictional
/// placeholder identifiers (CLAUDE.md).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ConfigDocsApiTests : IAsyncLifetime
{
	private sealed class ConfigDocsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ConfigDocsApiFactory(string connectionString)
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

				services.AddSingleton(new ConfigDocRepository(_connectionString));
				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ConfigDocsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ConfigDocsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new ConfigDocsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Put_NewSlot_CreatesV1WithAuthorAndTimestamp()
	{
		Guid id = Guid.NewGuid();
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { kind = "attestation", profile = "esxi-stig", layer = "global", body = "waived: true\n" });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
		Assert.Equal("test-user", document.RootElement.GetProperty("author").GetString());
		Assert.Equal("global", document.RootElement.GetProperty("layer").GetString());
		Assert.True(document.RootElement.TryGetProperty("created_at", out _));
	}

	[Fact]
	public async Task Put_SameSlotTwice_CreatesV2AndHistoryShowsBothAuthors()
	{
		Guid id = Guid.NewGuid();
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { kind = "input", profile = "nsx-stig", layer = "global", body = "syslog: 192.0.2.10\n" });

		HttpResponseMessage second = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { body = "syslog: 198.51.100.10\n" });
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		using (JsonDocument document = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
		{
			Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
		}

		HttpResponseMessage historyResponse = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{id}/versions", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
		using JsonDocument history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
		JsonElement[] rows = history.RootElement.EnumerateArray().ToArray();
		Assert.Equal(2, rows.Length);
		Assert.Equal(2, rows[0].GetProperty("version").GetInt32());
		Assert.Equal(1, rows[1].GetProperty("version").GetInt32());

		// @v1 is fetchable directly and unmutated by the @v2 save.
		HttpResponseMessage v1Response = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{id}/versions/1", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, v1Response.StatusCode);
		using JsonDocument v1 = JsonDocument.Parse(await v1Response.Content.ReadAsStringAsync());
		Assert.Equal("syslog: 192.0.2.10\n", v1.RootElement.GetProperty("body").GetString());

		// GET /config-docs/{id} returns the latest.
		HttpResponseMessage latestResponse = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{id}", "Viewer", body: null);
		using JsonDocument latest = JsonDocument.Parse(await latestResponse.Content.ReadAsStringAsync());
		Assert.Equal("syslog: 198.51.100.10\n", latest.RootElement.GetProperty("body").GetString());
	}

	[Fact]
	public async Task Put_InvalidYaml_Is400WithStandardEnvelope()
	{
		Guid id = Guid.NewGuid();
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { kind = "attestation", profile = "bad-yaml-profile", layer = "global", body = "controls: [unterminated" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("invalid_yaml", document.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task Put_ValidYaml_RoundTripsByteStable()
	{
		const string yaml = "controls:\n  - id: V-1\n    status: not_applicable\n    justification: \"System does not run this service.\"\n";
		Guid id = Guid.NewGuid();
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { kind = "attestation", profile = "roundtrip-profile", layer = "global", body = yaml });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(yaml, document.RootElement.GetProperty("body").GetString());
	}

	[Fact]
	public async Task Put_TargetLayerReferencingUnknownTarget_Is400()
	{
		Guid id = Guid.NewGuid();
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Admin",
			new { kind = "input", profile = "orphan-target-profile", layer = $"target:{Guid.NewGuid()}", body = "a: 1\n" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("invalid_layer", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Viewer_CannotWrite_Is403()
	{
		Guid id = Guid.NewGuid();
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{id}", "Viewer",
			new { kind = "attestation", profile = "role-guard-profile", layer = "global", body = "a: 1\n" });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Unauthenticated_CannotRead_Is401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/config-docs");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task List_FiltersByKindProfileAndLayer()
	{
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile = "list-filter-profile", layer = "global", body = "a: 1\n" });
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "attestation", profile = "list-filter-profile", layer = "global", body = "b: 1\n" });

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, "/api/v1/config-docs?kind=input&profile=list-filter-profile&layer=global", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
		Assert.Single(rows);
		Assert.Equal("input", rows[0].GetProperty("kind").GetString());
	}

	[Fact]
	public async Task GetUnknownId_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{Guid.NewGuid()}", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>
	/// Issue #270 AC: "GET-by-id on a doc with no versions returns a clean response, not
	/// 500". A doc row with current_version = 0 and zero config_versions rows is exactly
	/// the shape a pre-#270 mid-save crash could have left behind (the atomic-write fix in
	/// ConfigDocRepository.SaveAsync makes new ones unreachable, but does not retroactively
	/// clean up any that already exist) -- inserted directly here since the fixed write
	/// path can no longer produce it. Get, ListVersions, and GetVersion must all 404
	/// cleanly rather than the pre-fix null-forgiving deref 500.
	/// </summary>
	[Fact]
	public async Task Get_OrphanDocWithNoVersions_Is404NotServerError()
	{
		Guid orphanId = Guid.NewGuid();
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync().ConfigureAwait(false);
			await using NpgsqlCommand insertOrphan = new(
				"INSERT INTO config_docs (id, kind, profile, layer_type, layer_ref, current_version) VALUES ($1, 'input', 'orphan-api-profile', 'global', NULL, 0)", connection);
			insertOrphan.Parameters.AddWithValue(orphanId);
			await insertOrphan.ExecuteNonQueryAsync().ConfigureAwait(false);
		}

		HttpResponseMessage getResponse = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{orphanId}", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

		HttpResponseMessage versionsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{orphanId}/versions", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, versionsResponse.StatusCode);

		HttpResponseMessage versionResponse = await SendAsync(HttpMethod.Get, $"/api/v1/config-docs/{orphanId}/versions/1", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, versionResponse.StatusCode);
	}

	[Fact]
	public async Task Resolve_TargetOverridesSiteOverridesGlobal_MostSpecificWins()
	{
		string profile = $"resolve-profile-{Guid.NewGuid():N}";
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync();

		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile, layer = "global", body = "syslog_host: syslog.example.internal\n" });
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile, layer = $"site:{siteId}", body = "syslog_host: site-log.example.internal\n" });
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile, layer = $"target:{targetId}", body = "syslog_host: target-log.example.internal\n" });

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile={profile}&target={targetId}&kind=input", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
		Assert.Single(rows);
		Assert.Equal($"target:{targetId}", rows[0].GetProperty("layer").GetString());
		Assert.Equal("syslog_host: target-log.example.internal\n", rows[0].GetProperty("body").GetString());
	}

	[Fact]
	public async Task Resolve_NoTargetLayer_FallsThroughToSite()
	{
		string profile = $"resolve-fallthrough-{Guid.NewGuid():N}";
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync();

		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile, layer = "global", body = "a: global\n" });
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "input", profile, layer = $"site:{siteId}", body = "a: site\n" });

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile={profile}&target={targetId}&kind=input", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.Equal($"site:{siteId}", row.GetProperty("layer").GetString());
		Assert.Equal("a: site\n", row.GetProperty("body").GetString());
	}

	[Fact]
	public async Task Resolve_NoDocAtAnyLayer_ResolvesToNullLayerAndBody()
	{
		string profile = $"resolve-empty-{Guid.NewGuid():N}";
		(_, Guid targetId) = await CreateSiteAndTargetAsync();

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile={profile}&target={targetId}&kind=input", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();

		// Null fields are omitted entirely (WaypointJsonOptions: WhenWritingNull), the same
		// convention every other response in this API follows.
		Assert.False(row.TryGetProperty("layer", out _));
		Assert.False(row.TryGetProperty("body", out _));
	}

	[Fact]
	public async Task Resolve_ExpiredTargetAttestation_FallsThroughAndReportsExpired()
	{
		string profile = $"resolve-expired-{Guid.NewGuid():N}";
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync();

		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "attestation", profile, layer = $"site:{siteId}", body = "status: Not_A_Finding\njustification: site waiver\nexpires: 2099-01-01\n" });
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "attestation", profile, layer = $"target:{targetId}", body = "status: Not_A_Finding\njustification: lapsed waiver\nexpires: 2020-01-01\n" });

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile={profile}&target={targetId}&kind=attestation", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.Equal($"site:{siteId}", row.GetProperty("layer").GetString());
		Assert.True(row.GetProperty("attestation_expired").GetBoolean());
	}

	[Fact]
	public async Task Resolve_NoKindFilter_ReturnsAllThreeKinds()
	{
		string profile = $"resolve-allkinds-{Guid.NewGuid():N}";
		(_, Guid targetId) = await CreateSiteAndTargetAsync();

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile={profile}&target={targetId}", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
		Assert.Equal(3, rows.Length);
		Assert.Contains(rows, row => row.GetProperty("kind").GetString() == "input");
		Assert.Contains(rows, row => row.GetProperty("kind").GetString() == "attestation");
		Assert.Contains(rows, row => row.GetProperty("kind").GetString() == "remediation-input");
	}

	[Fact]
	public async Task Resolve_UnknownTarget_Is400()
	{
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?profile=any&target={Guid.NewGuid()}", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Resolve_MissingProfile_Is400()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync();
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/config-docs/resolve?target={targetId}", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	private async Task<(Guid SiteId, Guid TargetId)> CreateSiteAndTargetAsync()
	{
		SiteRepository sites = new(_fixture.ConnectionString);
		TargetRepository targets = new(_fixture.ConnectionString);

		Guid siteId = (await sites.CreateAsync($"resolve-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(TargetWriteOutcome outcome, Guid? targetId) = await targets.CreateAsync(
			siteId, "vsphere", $"resolve-target-{Guid.NewGuid():N}", """{"host":"esxi-01.example.internal"}""", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return (siteId, targetId!.Value);
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
			"TRUNCATE TABLE config_versions, config_docs, targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
