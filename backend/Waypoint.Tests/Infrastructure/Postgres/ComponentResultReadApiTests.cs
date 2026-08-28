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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #745 (finding-list/artifact-metadata read remainder, stated by PR #952/#961's
/// bodies): <c>GET /api/v1/jobs/{id}/component-results/findings</c> and
/// <c>GET /api/v1/jobs/{id}/component-results/artifacts</c> end to end against real
/// Postgres. Seeds through the real <see cref="ComponentResultRepository"/>/
/// <see cref="ScanPlanRepository"/>/<see cref="CatalogRepository"/> chain -- the same
/// seeding shape <c>ComponentResultRepositoryTests</c> uses -- rather than through the
/// full HDF-parsing pipeline, because this class's focus is the REST surface reading
/// recorded rows back, not re-proving <c>HdfFindingsParser</c>. All identities invented
/// (AGENTS.md).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ComponentResultReadApiTests : IAsyncLifetime
{
	private sealed class ComponentResultReadApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ComponentResultReadApiFactory(string connectionString)
		{
			_connectionString = connectionString;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:Waypoint"] = _connectionString,
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

				services.AddSingleton<IComponentResultRepository>(new ComponentResultRepository(_connectionString));

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
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ComponentResultReadApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ComponentResultRepository _componentResults = null!;
	private CatalogRepository _catalog = null!;
	private ScanPlanRepository _scanPlans = null!;

#pragma warning restore CA1001

	public ComponentResultReadApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_componentResults = new ComponentResultRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_scanPlans = new ScanPlanRepository(_fixture.ConnectionString);

		_factory = new ComponentResultReadApiFactory(_fixture.ConnectionString);
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
				component_result_findings, component_result_artifacts, component_results,
				scan_plan_items, scan_plans, run_scope_snapshots, jobs, runs,
				baselines, content_revisions, components, targets, sites,
				catalog_execution_profiles, catalog_report_groups, catalog_content_releases, catalog_components,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO runs (run_type) VALUES ('scan') RETURNING id", connection);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedJobAsync(Guid runId, Guid scanPlanItemId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret, scan_plan_item_id)
			VALUES ($1, 'scan', 1, 'queued', true, $2) RETURNING id
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(scanPlanItemId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedNonScanPlanJobAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret) VALUES ($1, 'download', 1, 'done', false) RETURNING id", connection);
		command.Parameters.AddWithValue(runId);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	/// <summary>Full 0050 identity tree + one scan_plans row carrying one item -- mirrors ComponentResultRepositoryTests' own seeding.</summary>
	private async Task<(Guid RunId, Guid ComponentId, Guid ScanPlanItemId)> SeedPlanItemAsync(string suffix)
	{
		Guid runId = await SeedRunAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid siteId;
		await using (NpgsqlCommand site = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection))
		{
			site.Parameters.AddWithValue($"site-{suffix}");
			siteId = (Guid)(await site.ExecuteScalarAsync())!;
		}

		Guid targetId;
		await using (NpgsqlCommand target = new(
			"INSERT INTO targets (site_id, kind, name, connection) VALUES ($1, 'vsphere', $2, '{}'::jsonb) RETURNING id", connection))
		{
			target.Parameters.AddWithValue(siteId);
			target.Parameters.AddWithValue($"target-{suffix}");
			targetId = (Guid)(await target.ExecuteScalarAsync())!;
		}

		Guid componentId;
		await using (NpgsqlCommand component = new(
			"""
			INSERT INTO components (parent_target_id, catalog_component_key, vendor_identity, display_name, lifecycle)
			VALUES ($1, 'esxi', $2, $2, 'active') RETURNING id
			""", connection))
		{
			component.Parameters.AddWithValue(targetId);
			component.Parameters.AddWithValue($"host-{suffix}");
			componentId = (Guid)(await component.ExecuteScalarAsync())!;
		}

		CatalogSourceRevision sourceRevision = await _catalog.UpsertSourceRevisionAsync($"source-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(sourceRevision.Id, "VMware", $"vsphere-{suffix}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id, new CatalogComponentDefinition($"esxi-{suffix}", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null), CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(sourceRevision.Id, CatalogKinds.Srg, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 2, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "1.0.0", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		ScanPlanItem item = new(
			componentId, executionProfile.Id, BaselineId: null, BenchmarkRevisionId: null,
			Transport: CatalogTransports.VMware, SelectorKind: CatalogSelectorKinds.Esxi, SelectorName: null,
			ReportGroupKey: $"group-{suffix}", Priority: 2, OutputKind: CatalogOutputKinds.HdfAndCkl,
			RequiredPurposes: ["vsphere-api"], DeclaredInputNames: ["target_ip"]);

		ScanPlan plan = new(runId, ScanPlanSchema.CurrentVersion, [item], [], $"digest-{suffix}", "1 of 1 accepted");
		IReadOnlyDictionary<Guid, Guid> itemIds = await _scanPlans.RecordAsync(runId, runScopeSnapshotId: null, plan, CancellationToken.None);
		return (runId, componentId, itemIds[componentId]);
	}

	private static ComponentResultRecord CompletedRecord(Guid runId, Guid jobId, Guid scanPlanItemId, Guid componentId, int attempt, string status = "completed") =>
		new(
			RunId: runId,
			JobId: jobId,
			ScanPlanItemId: scanPlanItemId,
			ComponentId: componentId,
			AttemptNumber: attempt,
			Status: status,
			Detail: status == ComponentResultStatuses.ExecutionError ? "invented execution error detail" : null,
			Findings:
			[
				new ComponentResultFinding("SV-2", "SV-2r1_rule", "invented title 2", ComponentFindingSeverities.CatI, ComponentFindingStatuses.Failed, "invented failure evidence"),
				new ComponentResultFinding("SV-1", "SV-1r1_rule", "invented title 1", ComponentFindingSeverities.CatII, ComponentFindingStatuses.Passed, null),
				new ComponentResultFinding("SV-3", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, null),
			],
			Artifacts:
			[
				new ComponentResultArtifact(ComponentResultArtifactKinds.HdfRaw, "invented-raw.json", "deadbeef01", 1024),
				new ComponentResultArtifact(ComponentResultArtifactKinds.Ckl, "invented.ckl", "deadbeef02", 2048),
			]);

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? role)
	{
		HttpRequestMessage request = new(method, path);
		if (role is not null)
		{
			request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		}

		return await _client.SendAsync(request);
	}

	// -- GET /jobs/{id}/component-results/findings -----------------------------------

	[Fact]
	public async Task GetFindings_HappyPath_ReturnsLatestAttemptFindingsUnaltered()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-happy");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal(jobId.ToString(), root.GetProperty("job_id").GetString());
		Assert.Equal(1, root.GetProperty("attempt_number").GetInt32());
		Assert.Equal("completed", root.GetProperty("component_result_status").GetString());
		Assert.Equal(3, root.GetProperty("total_count").GetInt32());

		JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
		Assert.Equal(3, items.Length);
		// Ordered by control_id -- proves no silent re-sort/re-bucketing happened.
		Assert.Equal("SV-1", items[0].GetProperty("control_id").GetString());
		Assert.Equal("passed", items[0].GetProperty("status").GetString());
		Assert.Equal("SV-2", items[1].GetProperty("control_id").GetString());
		Assert.Equal("failed", items[1].GetProperty("status").GetString());
		Assert.Equal("cat_i", items[1].GetProperty("severity").GetString());
		Assert.Equal("SV-3", items[2].GetProperty("control_id").GetString());
		// Epic #726 §6: an applicable-but-unexecuted control is `not_reviewed`, never
		// `not_applicable` and never omitted.
		Assert.Equal("not_reviewed", items[2].GetProperty("status").GetString());
		// Omitted-null fields: SV-3 has no rule_id/title/evidence.
		Assert.False(items[2].TryGetProperty("rule_id", out _));
		Assert.False(items[2].TryGetProperty("title", out _));
		Assert.False(items[2].TryGetProperty("evidence", out _));
	}

	[Fact]
	public async Task GetFindings_ExecutionErrorStatus_IsDistinctFromFailed()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-exec-error");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(
			CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1, status: ComponentResultStatuses.ExecutionError),
			CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", "Viewer");

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		// The component-result-level status is execution_error -- never silently
		// collapsed into "failed" or "completed" (epic #726 §6: execution_error != fail).
		Assert.Equal("execution_error", document.RootElement.GetProperty("component_result_status").GetString());
	}

	[Fact]
	public async Task GetFindings_UsesOnlyTheLatestAttempt()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-latest-attempt");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);

		await _componentResults.RecordAsync(new ComponentResultRecord(
			runId, jobId, scanPlanItemId, componentId, AttemptNumber: 1, Status: ComponentResultStatuses.ExecutionError, Detail: "first attempt failed",
			Findings: [], Artifacts: []), CancellationToken.None);
		await _componentResults.RecordAsync(
			CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 2), CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", "Viewer");

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal(2, root.GetProperty("attempt_number").GetInt32());
		Assert.Equal("completed", root.GetProperty("component_result_status").GetString());
		Assert.Equal(3, root.GetProperty("total_count").GetInt32());
	}

	[Fact]
	public async Task GetFindings_Paging_RespectsLimitAndOffset()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-paging");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings?limit=1&offset=1", "Viewer");

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal(3, root.GetProperty("total_count").GetInt32());
		Assert.Equal(1, root.GetProperty("limit").GetInt32());
		Assert.Equal(1, root.GetProperty("offset").GetInt32());
		JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
		Assert.Single(items);
		// Ordered by control_id: SV-1, SV-2, SV-3 -- offset 1 skips SV-1, lands on SV-2.
		Assert.Equal("SV-2", items[0].GetProperty("control_id").GetString());
	}

	[Fact]
	public async Task GetFindings_InvalidLimit_Is400()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-bad-limit");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		_ = componentId;

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings?limit=0", "Viewer");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task GetFindings_InvalidOffset_Is400()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-bad-offset");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		_ = componentId;

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings?offset=-1", "Viewer");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task GetFindings_JobWithNoRecordedAttemptYet_ReturnsEmptyNot404()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedNonScanPlanJobAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.False(root.TryGetProperty("attempt_number", out _));
		Assert.False(root.TryGetProperty("component_result_status", out _));
		Assert.Empty(root.GetProperty("items").EnumerateArray());
		Assert.Equal(0, root.GetProperty("total_count").GetInt32());
	}

	/// <summary>Post-#963/#966 purge hard-deletes component_results/findings rows -- a purged job's finding list is honestly empty, never half-rendered.</summary>
	[Fact]
	public async Task GetFindings_AfterPurge_ReturnsEmptyNotStaleData()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("findings-purged");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		await PurgeComplianceEvidenceAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.False(root.TryGetProperty("attempt_number", out _));
		Assert.Empty(root.GetProperty("items").EnumerateArray());
		Assert.Equal(0, root.GetProperty("total_count").GetInt32());
	}

	[Fact]
	public async Task GetFindings_UnknownJob_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{Guid.NewGuid()}/component-results/findings", "Viewer");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetFindings_NoAuthHeader_Is401()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedNonScanPlanJobAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/findings", role: null);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	// -- GET /jobs/{id}/component-results/artifacts -----------------------------------

	[Fact]
	public async Task GetArtifacts_HappyPath_ReturnsMetadataOnly()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("artifacts-happy");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/artifacts", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal(jobId.ToString(), root.GetProperty("job_id").GetString());
		Assert.Equal(1, root.GetProperty("attempt_number").GetInt32());
		Assert.Equal("completed", root.GetProperty("component_result_status").GetString());

		JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
		Assert.Equal(2, items.Length);
		// Ordered by kind: ckl < hdf_raw alphabetically.
		Assert.Equal("ckl", items[0].GetProperty("kind").GetString());
		Assert.Equal("invented.ckl", items[0].GetProperty("path").GetString());
		Assert.Equal("deadbeef02", items[0].GetProperty("digest").GetString());
        Assert.Equal(2048, items[0].GetProperty("size_bytes").GetInt64());
		Assert.Equal("hdf_raw", items[1].GetProperty("kind").GetString());
		Assert.Equal(1024, items[1].GetProperty("size_bytes").GetInt64());

		// Metadata only -- no byte payload/content field anywhere on the wire.
		Assert.False(items[0].TryGetProperty("content", out _));
		Assert.False(items[0].TryGetProperty("bytes", out _));
	}

	[Fact]
	public async Task GetArtifacts_JobWithNoRecordedAttemptYet_ReturnsEmptyNot404()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedNonScanPlanJobAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/artifacts", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.False(root.TryGetProperty("attempt_number", out _));
		Assert.Empty(root.GetProperty("items").EnumerateArray());
	}

	[Fact]
	public async Task GetArtifacts_AfterPurge_ReturnsEmptyNotStaleData()
	{
		(Guid runId, Guid componentId, Guid scanPlanItemId) = await SeedPlanItemAsync("artifacts-purged");
		Guid jobId = await SeedJobAsync(runId, scanPlanItemId);
		await _componentResults.RecordAsync(CompletedRecord(runId, jobId, scanPlanItemId, componentId, attempt: 1), CancellationToken.None);

		await PurgeComplianceEvidenceAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/artifacts", "Viewer");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.False(root.TryGetProperty("attempt_number", out _));
		Assert.Empty(root.GetProperty("items").EnumerateArray());
	}

	[Fact]
	public async Task GetArtifacts_UnknownJob_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{Guid.NewGuid()}/component-results/artifacts", "Viewer");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetArtifacts_NoAuthHeader_Is401()
	{
		Guid runId = await SeedRunAsync();
		Guid jobId = await SeedNonScanPlanJobAsync(runId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/component-results/artifacts", role: null);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	/// <summary>
	/// Mirrors migration 0066's carve-out exactly as <c>RunPurgeComplianceEvidenceTests</c>
	/// exercises it directly against the DB (no HTTP surface for a bare database-phase
	/// purge exists independent of <c>RunPurgeService</c>, which needs the full job-queue
	/// repository graph this test class does not otherwise wire) -- deletes
	/// component_results/findings/artifacts for the run under the session-local purge GUC.
	/// </summary>
	private async Task PurgeComplianceEvidenceAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

		await using (NpgsqlCommand setGuc = new("SELECT set_config('waypoint.purge_run_id', $1, true)", connection, transaction))
		{
			setGuc.Parameters.AddWithValue(runId.ToString());
			await setGuc.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand deleteFindings = new(
			"""
			DELETE FROM component_result_findings
			WHERE component_result_id IN (SELECT id FROM component_results WHERE run_id = $1)
			""", connection, transaction))
		{
			deleteFindings.Parameters.AddWithValue(runId);
			await deleteFindings.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand deleteArtifacts = new(
			"""
			DELETE FROM component_result_artifacts
			WHERE component_result_id IN (SELECT id FROM component_results WHERE run_id = $1)
			""", connection, transaction))
		{
			deleteArtifacts.Parameters.AddWithValue(runId);
			await deleteArtifacts.ExecuteNonQueryAsync();
		}

		await using (NpgsqlCommand deleteResults = new("DELETE FROM component_results WHERE run_id = $1", connection, transaction))
		{
			deleteResults.Parameters.AddWithValue(runId);
			await deleteResults.ExecuteNonQueryAsync();
		}

		await transaction.CommitAsync();
	}
}
