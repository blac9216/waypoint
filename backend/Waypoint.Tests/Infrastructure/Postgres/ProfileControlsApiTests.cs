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
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ConfigDocs;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #598 end to end against real Postgres: <c>GET /profiles/{id}/controls</c>
/// (docs/api-contract.md: "Control, severity, title, effective input + scope, attest
/// status"). Covers the 404-unknown-profile case, the "installed profile with zero
/// parsed controls" vs. "no content installed" distinction (an empty controls array is
/// a valid response, not a 404), Viewer+ role gating, and the profile-level (not truly
/// per-control -- see <c>ProfileControlResponse</c>'s doc comment) effective-input/
/// attest-status resolution once a <c>target</c> query param is supplied.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ProfileControlsApiTests : IAsyncLifetime
{
	private sealed class ProfileControlsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ProfileControlsApiFactory(string connectionString)
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

				// Same "wire the controller's dependencies directly" pattern
				// ComplianceContentApiTests uses -- this factory has no
				// ConnectionStrings:Waypoint, so AddWaypointInfrastructure's
				// connection-string-gated registration never runs.
				services.AddSingleton<IProfileRepository>(new ProfileRepository(_connectionString));
				services.AddSingleton<IProfileControlRepository>(new ProfileControlRepository(_connectionString));
				services.AddSingleton(new ConfigDocRepository(_connectionString));
				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ProfileControlsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ProfileControlsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new ProfileControlsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE profile_controls, profiles, config_versions, config_docs, targets, sites RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedProfileAsync(string profileKey)
	{
		ProfileRepository profiles = new(_fixture.ConnectionString);
		await profiles.ReplaceAllAsync([new ProfileUpsert(profileKey, profileKey, "1.0", "commit1", ProfileStates.Current)], CancellationToken.None);
		Profile seeded = Assert.Single(await profiles.ListAsync(CancellationToken.None), p => p.ProfileKey == profileKey);
		return seeded.Id;
	}

	[Fact]
	public async Task GetControls_UnknownProfileId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{Guid.NewGuid()}/controls");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetControls_InstalledProfileWithNoParsedControls_ReturnsEmptyArray_Not404()
	{
		Guid profileId = await SeedProfileAsync("profile-empty-controls");

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{profileId}/controls");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(document.RootElement.EnumerateArray());
	}

	[Fact]
	public async Task GetControls_ReturnsControlIdSeverityTitle()
	{
		Guid profileId = await SeedProfileAsync("profile-with-controls");
		ProfileControlRepository controls = new(_fixture.ConnectionString);
		await controls.ReplaceForProfileAsync(
			profileId,
			[
				new ProfileControlUpsert("V-1001", "Disable weak ciphers", "high"),
				new ProfileControlUpsert("V-1002", null, null),
			],
			CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{profileId}/controls");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] items = [.. document.RootElement.EnumerateArray()];
		Assert.Equal(2, items.Length);

		JsonElement first = items[0];
		Assert.Equal("V-1001", first.GetProperty("control_id").GetString());
		Assert.Equal("Disable weak ciphers", first.GetProperty("title").GetString());
		Assert.Equal("high", first.GetProperty("severity").GetString());
		Assert.Equal("none", first.GetProperty("attest_status").GetString());
		// WaypointJsonOptions omits null properties -- no target supplied, so
		// effective_input/effective_input_layer/attest_layer are absent, not present-as-null.
		Assert.False(first.TryGetProperty("effective_input", out _));

		JsonElement second = items[1];
		Assert.Equal("V-1002", second.GetProperty("control_id").GetString());
		Assert.False(second.TryGetProperty("title", out _));
		Assert.False(second.TryGetProperty("severity", out _));
	}

	[Fact]
	public async Task GetControls_ViewerRole_Returns200()
	{
		Guid profileId = await SeedProfileAsync("profile-viewer-role");

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{profileId}/controls");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task GetControls_NoRole_Returns401()
	{
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{Guid.NewGuid()}/controls");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetControls_WithTarget_ResolvesEffectiveInputAndAttestStatus_AppliedAtSameLayerForEveryControl()
	{
		Guid profileId = await SeedProfileAsync("profile-with-target-resolution");
		ProfileControlRepository controlsRepo = new(_fixture.ConnectionString);
		await controlsRepo.ReplaceForProfileAsync(
			profileId,
			[new ProfileControlUpsert("V-2001", "Control one", "medium"), new ProfileControlUpsert("V-2002", "Control two", "low")],
			CancellationToken.None);

		Guid siteId = (await new SiteRepository(_fixture.ConnectionString).CreateAsync("Site A", null, null, CancellationToken.None))!.Value;
		(_, Guid? targetId) = await new TargetRepository(_fixture.ConnectionString).CreateAsync(
			siteId, "vsphere", "Target A", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);

		ConfigDocRepository configDocs = new(_fixture.ConnectionString);
		await configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, "profile-with-target-resolution", ConfigDocLayers.Global, null,
			"tester", "some_input: applied-globally", CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{profileId}/controls?target={targetId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] items = [.. document.RootElement.EnumerateArray()];
		Assert.Equal(2, items.Length);

		// Same profile-level resolution surfaces identically on every control row --
		// this endpoint does not resolve per control id (see ProfileControlResponse's
		// doc comment on why).
		Assert.All(items, item =>
		{
			Assert.Equal("some_input: applied-globally", item.GetProperty("effective_input").GetString());
			Assert.Equal("global", item.GetProperty("effective_input_layer").GetString());
			Assert.Equal("none", item.GetProperty("attest_status").GetString());
		});
	}

	/// <summary>
	/// <see cref="ProfileControlRepository.ReplaceForProfileAsync"/>'s "replace, not
	/// merge" contract: a second call for the same profile with a different control set
	/// drops the control(s) no longer present, mirroring <c>ProfileRepository.ReplaceAllAsync</c>'s
	/// upsert/replace-per-parent shape.
	/// </summary>
	[Fact]
	public async Task ReplaceForProfileAsync_SecondCall_DropsControlsNoLongerPresent()
	{
		Guid profileId = await SeedProfileAsync("profile-replace-drops-stale");
		ProfileControlRepository controls = new(_fixture.ConnectionString);

		await controls.ReplaceForProfileAsync(
			profileId, [new ProfileControlUpsert("V-5001", "Stale control", "low"), new ProfileControlUpsert("V-5002", "Kept control", "high")],
			CancellationToken.None);

		await controls.ReplaceForProfileAsync(profileId, [new ProfileControlUpsert("V-5002", "Kept control", "high")], CancellationToken.None);

		ProfileControl survivor = Assert.Single(await controls.ListByProfileAsync(profileId, CancellationToken.None));
		Assert.Equal("V-5002", survivor.ControlId);
	}

	[Fact]
	public async Task GetControls_WithUnknownTarget_Returns400()
	{
		Guid profileId = await SeedProfileAsync("profile-unknown-target");

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/profiles/{profileId}/controls?target={Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
