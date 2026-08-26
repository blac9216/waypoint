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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #193 (epic #9 slice 1) end to end against real Postgres: the depot catalog
/// REST surface backed by the real repository and the real job engine -- role gates,
/// filters/pagination with X-Total-Count, and that POST /catalog/sync creates a run
/// plus exactly one queued catalog-index job (no handler registered yet is correct
/// for this slice; <c>JobDispatcherHostedServiceTests.NoHandlerRegistered_...</c>
/// already proves that job fails with a clear note once dispatched).
///
/// Issue #728 (epic #726 Wave 1 remainder) adds the unrelated <c>GET
/// /catalog/products</c> / <c>GET /catalog/products/{id}</c> execution-catalog read
/// surface to this same controller/route prefix -- see the fixtures/tests below
/// prefixed <c>GetProducts_</c>/<c>GetProduct_</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class CatalogApiTests : IAsyncLifetime
{
	private sealed class CatalogApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public CatalogApiFactory(string connectionString)
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

				services.AddSingleton<IDepotArtifactRepository>(new DepotArtifactRepository(_connectionString));
				// Issue #687: CatalogController now also depends on these two --
				// same "point every repository at the real fixture connection string,
				// not whatever ConnectionStrings:Waypoint the Testing appsettings
				// resolves to" override this factory already does for the others.
				services.AddSingleton<ICatalogPullStateRepository>(new CatalogPullStateRepository(_connectionString));
				services.AddSingleton<IDepotEnrollmentRepository>(new DepotEnrollmentRepository(_connectionString));
				// Issue #728: the unrelated normalized compliance execution-catalog
				// read surface (GET /catalog/products) added to this same controller --
				// same "point every repository at the real fixture connection string"
				// override this factory already applies to the others above.
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				// Issue #415: one JobQueueRepository instance satisfies both focused
				// interfaces CatalogController (control) and the runner path resolve.
				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private CatalogApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public CatalogApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetCatalogDataAsync();

		_factory = new CatalogApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task GetArtifacts_ReturnsSeededRows_WithFilteringAndXTotalCount()
	{
		DepotArtifactRepository repository = new(_fixture.ConnectionString);
		string tag = Guid.NewGuid().ToString("N");
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-1", "sha-1", "indexed", """{"product":"VCF","version":"9.0"}"""), CancellationToken.None);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-2", "sha-2", "present", """{"product":"VCF","version":"9.1"}"""), CancellationToken.None);
		await repository.UpsertAsync(new DepotArtifactUpsert($"{tag}-3", "sha-3", "indexed", """{"product":"NSX","version":"4.2"}"""), CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/artifacts?product=VCF&limit=200&offset=0");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.Equal(2, int.Parse(totals!.Single(), System.Globalization.CultureInfo.InvariantCulture));

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] externalIds = document.RootElement.EnumerateArray()
			.Select(item => item.GetProperty("external_id").GetString()!)
			.Where(id => id.StartsWith(tag, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(2, externalIds.Length);
	}

	[Fact]
	public async Task GetArtifacts_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/catalog/artifacts");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task PostSync_WithAdminRole_CreatesRunAndOneQueuedCatalogIndexJob()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = Guid.Parse(document.RootElement.GetProperty("run_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runType = new("SELECT run_type FROM runs WHERE id = $1", connection))
		{
			runType.Parameters.AddWithValue(runId);
			Assert.Equal("catalog-index", (string)(await runType.ExecuteScalarAsync())!);
		}

		await using NpgsqlCommand jobs = new("SELECT job_type, state FROM jobs WHERE run_id = $1", connection);
		jobs.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await jobs.ExecuteReaderAsync();
		int rowCount = 0;
		while (await reader.ReadAsync())
		{
			rowCount++;
			Assert.Equal("catalog-index", reader.GetString(0));
			Assert.Equal("queued", reader.GetString(1));
		}

		Assert.Equal(1, rowCount);
	}

	[Fact]
	public async Task PostSync_WithViewerRole_Returns403()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// Issue #687: GET /catalog/pull always answers (never 404/500) and defaults to
	/// not-ready with an actionable reason -- migration 0049 seeds depot_enrollment at
	/// 'tool_unavailable' and catalog_pull_state with no attempt yet.
	/// </summary>
	[Fact]
	public async Task GetPull_DefaultState_ReportsNotReadyWithReason()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(document.RootElement.GetProperty("ready").GetBoolean());
		Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("not_ready_reason").GetString()));
		// Null-valued properties are omitted by the API's JSON serialization
		// (matches every other nullable response contract in this codebase) -- absent
		// is the "no success yet" signal, not a literal JSON null.
		Assert.False(document.RootElement.TryGetProperty("last_success_at", out _));
	}

	[Fact]
	public async Task PostPull_EnrollmentNotValidated_Returns409AndNeverQueuesAJob()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM jobs WHERE job_type = 'catalog-pull'", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PostPull_EnrollmentValidated_CreatesRunAndOneQueuedCatalogPullJob()
	{
		await SetEnrollmentValidatedAsync();

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = Guid.Parse(document.RootElement.GetProperty("run_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand runType = new("SELECT run_type FROM runs WHERE id = $1", connection))
		{
			runType.Parameters.AddWithValue(runId);
			Assert.Equal("catalog-pull", (string)(await runType.ExecuteScalarAsync())!);
		}

		await using NpgsqlCommand jobs = new("SELECT job_type, state FROM jobs WHERE run_id = $1", connection);
		jobs.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await jobs.ExecuteReaderAsync();
		int rowCount = 0;
		while (await reader.ReadAsync())
		{
			rowCount++;
			Assert.Equal("catalog-pull", reader.GetString(0));
			Assert.Equal("queued", reader.GetString(1));
		}

		Assert.Equal(1, rowCount);
	}

	[Fact]
	public async Task PostPull_WithViewerRole_Returns403()
	{
		await SetEnrollmentValidatedAsync();

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/pull");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// Issue #728 (epic #726 Wave 1 remainder): the execution-catalog read surface.
	/// Fixture is INVENTED and shaped only like docs/compliance-parity.md's sibling
	/// provenance-matrix rows -- not exported from any real system (CLAUDE.md
	/// sanitization policy). Covers the queryable-fields AC (transport, selector,
	/// required purposes, priority/report group, benchmark, remediation capability).
	/// </summary>
	[Fact]
	public async Task GetProducts_ReturnsQueryableFields_ForSeededExecutionProfile()
	{
		Guid executionProfileId = await SeedOneExecutionProfileAsync();

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/products");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray()
			.Single(item => item.GetProperty("execution_profile_id").GetString() == executionProfileId.ToString());

		Assert.Equal("vmware", row.GetProperty("component").GetProperty("transport").GetString());
		Assert.Equal("vcenter", row.GetProperty("component").GetProperty("selector_kind").GetString());
		Assert.Equal("vsphere", row.GetProperty("product").GetProperty("product_key").GetString());
		Assert.Equal("8.0.3", row.GetProperty("product_version").GetProperty("version_key").GetString());
		Assert.Equal("stig", row.GetProperty("content_release").GetProperty("kind").GetString());
		Assert.Equal(3, row.GetProperty("report_group").GetProperty("priority").GetInt32());
		Assert.Equal("vsphere-api", row.GetProperty("credential_requirements").EnumerateArray().Single().GetProperty("purpose").GetString());
		Assert.Equal("VMW_vSphere_8-0_vCenter_STIG", row.GetProperty("benchmark").GetProperty("benchmark_key").GetString());
		Assert.True(row.GetProperty("remediation").GetProperty("is_supported").GetBoolean());
	}

	[Fact]
	public async Task GetProducts_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/catalog/products");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetProduct_ById_ReturnsSameJoinedShapeAsListRow()
	{
		Guid executionProfileId = await SeedOneExecutionProfileAsync();

		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/catalog/products/{executionProfileId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(executionProfileId.ToString(), document.RootElement.GetProperty("execution_profile_id").GetString());
		Assert.Equal("vmware", document.RootElement.GetProperty("component").GetProperty("transport").GetString());
	}

	[Fact]
	public async Task GetProduct_UnknownId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/catalog/products/{Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetProduct_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync($"/api/v1/catalog/products/{Guid.NewGuid()}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	/// <summary>
	/// Invented fixture: one vSphere 8.0 STIG vCenter execution profile, shaped like
	/// docs/compliance-parity.md's "vSphere 8-0 / STIG" row (vmware-transport,
	/// vcenter-selector component). Not exported from any real system.
	/// </summary>
	private async Task<Guid> SeedOneExecutionProfileAsync()
	{
		CatalogRepository repository = new(_fixture.ConnectionString);
		Guid sourceRevisionId = (await repository.UpsertSourceRevisionAsync("test-revision-api-1", "invented fixture revision", CancellationToken.None)).Id;
		CatalogProduct product = await repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogContentRelease release = await repository.UpsertContentReleaseAsync(
			sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "VMware vSphere 8.0 STIG v2r3", CancellationToken.None);
		CatalogReportGroup reportGroup = await repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent component = await repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);

		CatalogExecutionProfile executionProfile = await repository.CreateExecutionProfileAsync(
			component.Id, release.Id, reportGroup.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await repository.AddCredentialRequirementAsync(executionProfile.Id, "vsphere-api", true, CancellationToken.None);
		await repository.SetBenchmarkReferenceAsync(executionProfile.Id, "VMW_vSphere_8-0_vCenter_STIG", "v2r3", CancellationToken.None);
		await repository.SetRemediationDefinitionAsync(executionProfile.Id, true, "PowerCLI remediation script", CancellationToken.None);

		return executionProfile.Id;
	}

	private async Task SetEnrollmentValidatedAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new(
			"""
			UPDATE depot_enrollment
			SET state = 'validated', depot_id = 'WPT-0001-DEPOT-ID', depot_id_generated_at = now(),
			    paired_asset_id = 'WPT-0001-DEPOT-ID', paired_at = now()
			WHERE id = 1
			""", connection);
		await update.ExecuteNonQueryAsync();
	}

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);

		// Issue #728: the unrelated execution-catalog tables (migration 0050) share
		// this fixture's Postgres instance but not its identity/reset lifecycle above --
		// truncate them independently so GetProducts_* tests below start from empty.
		await using NpgsqlCommand truncateCatalog = new(
			"""
			TRUNCATE TABLE
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await truncateCatalog.ExecuteNonQueryAsync().ConfigureAwait(false);

		await using NpgsqlCommand resetEnrollment = new(
			"""
			UPDATE depot_enrollment
			SET state = 'tool_unavailable', depot_id = NULL, depot_id_generated_at = NULL,
			    paired_asset_id = NULL, paired_at = NULL, last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await resetEnrollment.ExecuteNonQueryAsync().ConfigureAwait(false);

		await using NpgsqlCommand resetPullState = new(
			"""
			UPDATE catalog_pull_state
			SET last_attempt_at = NULL, last_outcome = NULL, last_failure_reason = NULL,
			    last_success_at = NULL, last_success_item_count = NULL
			WHERE id = 1
			""", connection);
		await resetPullState.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
