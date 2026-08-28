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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Api.Contracts;
using Waypoint.Core.ComplianceContent;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #731's baseline-activation slice: the missing API surface for
/// <see cref="IBaselineRepository.CreateStagedBaselineAsync"/> and
/// <see cref="BaselineActivationService.ActivateAsync"/>/<c>RollbackAsync</c>, exercised
/// end to end against real Postgres and the real HTTP pipeline (role-matrix allow/deny
/// plus the actual state transitions). Round-5 live-lab validation (epic #726) found
/// these had zero non-test callers and no route -- this suite is the proof they are now
/// reachable and correct through <c>BaselinesController</c>. Every fixture value is
/// invented (CLAUDE.md/AGENTS.md sanitization policy).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class BaselinesApiTests : IAsyncLifetime
{
	private sealed class BaselinesApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public BaselinesApiFactory(string connectionString)
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

				services.AddSingleton<IBaselineRepository>(new BaselineRepository(_connectionString));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				services.AddSingleton<BaselineActivationService>();
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private BaselinesApiFactory _factory = null!;
	private HttpClient _client = null!;
	private BaselineRepository _baselines = null!;
	private CatalogRepository _catalog = null!;

#pragma warning restore CA1001

	public BaselinesApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_baselines = new BaselineRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_factory = new BaselinesApiFactory(_fixture.ConnectionString);
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
			"""
			TRUNCATE TABLE
				baselines, content_revisions,
				catalog_credential_requirements, catalog_execution_profiles, catalog_report_groups,
				catalog_content_releases, catalog_components, catalog_product_versions, catalog_products,
				catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private static HttpRequestMessage WithRole(HttpMethod method, string url, string role)
	{
		HttpRequestMessage request = new(method, url);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return request;
	}

	private async Task<Guid> SeedExecutionProfileAsync(string componentKey = "esxi")
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent component = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition(componentKey, "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			component.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		return executionProfile.Id;
	}

	private async Task<ContentRevision> StageRevisionAsync() =>
		await _baselines.RecordStagedRevisionAsync(
			$"commit-{Guid.NewGuid():N}", $"digest-{Guid.NewGuid():N}", $"revisions/{Guid.NewGuid():N}", CancellationToken.None);

	[Fact]
	public async Task Create_AsAdmin_StagesBaseline_AndListAndGetReflectIt()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();

		HttpRequestMessage createRequest = WithRole(HttpMethod.Post, "/api/v1/baselines", "Admin");
		createRequest.Content = JsonContent.Create(new
		{
			content_revision_id = revision.Id,
			catalog_execution_profile_id = executionProfileId,
		}, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage createResponse = await _client.SendAsync(createRequest);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		BaselineResponse? created = await createResponse.Content.ReadFromJsonAsync<BaselineResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.NotNull(created);
		Assert.Equal(BaselineStatuses.Staged, created!.Status);

		HttpResponseMessage listResponse = await _client.SendAsync(WithRole(HttpMethod.Get, "/api/v1/baselines", "Viewer"));
		Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
		using JsonDocument listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Single(listBody.RootElement.EnumerateArray());

		HttpResponseMessage getResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/baselines/{created.Id}", "Viewer"));
		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
	}

	[Fact]
	public async Task Create_AsViewer_IsForbidden()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();

		HttpRequestMessage request = WithRole(HttpMethod.Post, "/api/v1/baselines", "Viewer");
		request.Content = JsonContent.Create(new
		{
			content_revision_id = revision.Id,
			catalog_execution_profile_id = executionProfileId,
		}, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Create_WithUnknownContentRevision_Returns404()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();

		HttpRequestMessage request = WithRole(HttpMethod.Post, "/api/v1/baselines", "Admin");
		request.Content = JsonContent.Create(new
		{
			content_revision_id = Guid.NewGuid(),
			catalog_execution_profile_id = executionProfileId,
		}, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Activate_AsAdmin_WithConfirmation_TransitionsToActive()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		HttpRequestMessage activateRequest = WithRole(HttpMethod.Post, $"/api/v1/baselines/{staged.Id}/activate", "Admin");
		activateRequest.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(activateRequest);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		BaselineResponse? activated = await response.Content.ReadFromJsonAsync<BaselineResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.NotNull(activated);
		Assert.Equal(BaselineStatuses.Active, activated!.Status);
		Assert.NotNull(activated.ActivatedAt);
		Assert.Equal("test-user", activated.ActivatedBy);

		Baseline? fromRepo = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(fromRepo);
		Assert.Equal(staged.Id, fromRepo!.Id);
	}

	[Fact]
	public async Task Activate_AsCyber_IsForbidden()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		HttpRequestMessage request = WithRole(HttpMethod.Post, $"/api/v1/baselines/{staged.Id}/activate", "Cyber");
		request.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Activate_WithoutExactConfirmationPhrase_Returns400()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		HttpRequestMessage request = WithRole(HttpMethod.Post, $"/api/v1/baselines/{staged.Id}/activate", "Admin");
		request.Content = JsonContent.Create(new { confirmation = "yes" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Activate_UnknownBaseline_Returns404()
	{
		HttpRequestMessage request = WithRole(HttpMethod.Post, $"/api/v1/baselines/{Guid.NewGuid()}/activate", "Admin");
		request.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Activate_ThenRollback_SupersedesAndReactivatesPreviousBaseline()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();

		ContentRevision firstRevision = await StageRevisionAsync();
		Baseline first = await _baselines.CreateStagedBaselineAsync(firstRevision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);
		await _baselines.ActivateAsync(first.Id, "seed-fixture", CancellationToken.None);

		ContentRevision secondRevision = await StageRevisionAsync();
		Baseline second = await _baselines.CreateStagedBaselineAsync(secondRevision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		HttpRequestMessage activateSecond = WithRole(HttpMethod.Post, $"/api/v1/baselines/{second.Id}/activate", "Admin");
		activateSecond.Content = JsonContent.Create(new { confirmation = "ACTIVATE" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage activateSecondResponse = await _client.SendAsync(activateSecond);
		Assert.Equal(HttpStatusCode.OK, activateSecondResponse.StatusCode);

		Baseline? firstAfterSupersede = await _baselines.GetBaselineAsync(first.Id, CancellationToken.None);
		Assert.Equal(BaselineStatuses.Superseded, firstAfterSupersede!.Status);

		HttpRequestMessage rollback = WithRole(HttpMethod.Post, $"/api/v1/baselines/{first.Id}/rollback", "Admin");
		rollback.Content = JsonContent.Create(new { confirmation = "ROLLBACK" }, options: Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		HttpResponseMessage rollbackResponse = await _client.SendAsync(rollback);
		Assert.Equal(HttpStatusCode.OK, rollbackResponse.StatusCode);

		Baseline? activeAfterRollback = await _baselines.GetActiveBaselineAsync(executionProfileId, CancellationToken.None);
		Assert.NotNull(activeAfterRollback);
		Assert.Equal(first.Id, activeAfterRollback!.Id);

		Baseline? secondAfterRollback = await _baselines.GetBaselineAsync(second.Id, CancellationToken.None);
		Assert.Equal(BaselineStatuses.Superseded, secondAfterRollback!.Status);
	}

	[Fact]
	public async Task ImpactDiff_ForFreshExecutionProfile_ReportsPureAddition()
	{
		Guid executionProfileId = await SeedExecutionProfileAsync();
		ContentRevision revision = await StageRevisionAsync();
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfileId, benchmarkRevisionId: null, CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/baselines/{staged.Id}/impact-diff", "Viewer"));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		BaselineImpactDiffResponse? diff = await response.Content.ReadFromJsonAsync<BaselineImpactDiffResponse>(Waypoint.Core.Serialization.WaypointJsonOptions.Default);
		Assert.NotNull(diff);
		Assert.Equal(1, diff!.AddedProfiles);
		Assert.Equal(0, diff.ChangedProfiles);
	}
}
