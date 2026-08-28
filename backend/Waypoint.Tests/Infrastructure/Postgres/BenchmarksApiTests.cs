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
using Waypoint.Core.Catalog;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Core.Serialization;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #730 remainder (epic #726 Wave 1, migration 0052 model from PR #828): the
/// benchmark revision/rule read surface, the mapping coverage/history read surface,
/// and the Admin-only explicit mapping-override write, end to end against real
/// Postgres and the real job-free HTTP pipeline. Every fixture is INVENTED, shaped
/// like public DISA STIG XCCDF structure only (CLAUDE.md/AGENTS.md sanitization
/// policy) -- never real STIG content or lab data.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class BenchmarksApiTests : IAsyncLifetime
{
	private sealed class BenchmarksApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public BenchmarksApiFactory(string connectionString)
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

				services.AddSingleton<IBenchmarkRepository>(new BenchmarkRepository(_connectionString));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private BenchmarksApiFactory _factory = null!;
	private HttpClient _client = null!;
	private BenchmarkRepository _repository = null!;
	private CatalogRepository _catalogRepository = null!;

#pragma warning restore CA1001

	public BenchmarksApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_repository = new BenchmarkRepository(_fixture.ConnectionString);
		_catalogRepository = new CatalogRepository(_fixture.ConnectionString);
		_factory = new BenchmarksApiFactory(_fixture.ConnectionString);
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
				benchmark_component_mappings, benchmark_rules, benchmark_revisions,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private static BenchmarkImportCandidate InventedCandidate(string benchmarkKey = "xccdf_invented.example_benchmark_EX-1-0_STIG", string ruleTitle = "r1") =>
		new(
			benchmarkKey,
			"Invented Example STIG",
			"2",
			"3",
			ComputeInventedDigest(benchmarkKey, ruleTitle),
			[new XccdfRule("SV-000001r1_rule", "V-000001", BenchmarkRuleSeverities.High, ruleTitle)]);

	private static string ComputeInventedDigest(string benchmarkKey, string ruleTitle) =>
		Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(benchmarkKey + ruleTitle))).ToLowerInvariant();

	private async Task<Guid> SeedCatalogComponentAsync(string componentKey = "vcenter")
	{
		CatalogSourceRevision sourceRevision = await _catalogRepository.UpsertSourceRevisionAsync($"test-revision-{Guid.NewGuid():N}", "invented fixture revision", CancellationToken.None);
		CatalogProduct product = await _catalogRepository.UpsertProductAsync(sourceRevision.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _catalogRepository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogComponent component = await _catalogRepository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition(componentKey, "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		return component.Id;
	}

	/// <summary>
	/// Issue #1002: seeds a catalog component bound to an execution profile of the
	/// given <paramref name="kind"/> (stig|srg) -- the join
	/// <c>GET /benchmark-mappings/{id}</c>'s <c>derived_state</c> depends on.
	/// </summary>
	private async Task<Guid> SeedCatalogComponentWithKindAsync(string kind, string componentKey)
	{
		CatalogSourceRevision sourceRevision = await _catalogRepository.UpsertSourceRevisionAsync($"test-revision-{Guid.NewGuid():N}", "invented fixture revision", CancellationToken.None);
		CatalogProduct product = await _catalogRepository.UpsertProductAsync(sourceRevision.Id, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _catalogRepository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogComponent component = await _catalogRepository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition(componentKey, "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease contentRelease = await _catalogRepository.UpsertContentReleaseAsync(
			sourceRevision.Id, kind, $"{componentKey}-{kind}-release", $"Invented {kind} release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalogRepository.UpsertReportGroupAsync($"{componentKey}-{kind}-group", $"Invented {kind} report group", 3, CancellationToken.None);
		await _catalogRepository.CreateExecutionProfileAsync(
			component.Id, contentRelease.Id, reportGroup.Id, "V1", kind == CatalogKinds.Stig ? CatalogOutputKinds.HdfAndCkl : CatalogOutputKinds.Hdf, CancellationToken.None);
		return component.Id;
	}

	private static HttpRequestMessage WithRole(HttpMethod method, string url, string role)
	{
		HttpRequestMessage request = new(method, url);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return request;
	}

	/// <summary>
	/// JsonContent.Create's default options are NOT WaypointJsonOptions.Default's
	/// snake_case policy, so an <see cref="BenchmarkMappingOverrideRequest"/> body sent
	/// through the plain overload silently serializes as PascalCase and the controller
	/// model-binds every field to its default (a real bug this test file caught in its
	/// own first draft: the SRG/history/unknown-revision tests all failed for this
	/// reason). Route every request body through the same options the API itself uses.
	/// </summary>
	private static JsonContent MappingRequestContent(BenchmarkMappingOverrideRequest body) =>
		JsonContent.Create(body, options: WaypointJsonOptions.Default);

	[Fact]
	public async Task GetBenchmarks_ReturnsDistinctKeys()
	{
		await _repository.ImportRevisionAsync(InventedCandidate("zzz-benchmark", "a"), BenchmarkSources.ManualUpload, CancellationToken.None);
		await _repository.ImportRevisionAsync(InventedCandidate("aaa-benchmark", "b"), BenchmarkSources.ManualUpload, CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, "/api/v1/benchmarks", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string[]? keys = await response.Content.ReadFromJsonAsync<string[]>();
		Assert.Equal(["aaa-benchmark", "zzz-benchmark"], keys);
	}

	[Fact]
	public async Task GetBenchmarks_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/benchmarks");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetRevisionsByKey_MultipleRevisions_AreBothReturned_ProvingCoexistence()
	{
		BenchmarkRevision first = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "original"), BenchmarkSources.ManualUpload, CancellationToken.None);
		BenchmarkRevision second = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "changed"), BenchmarkSources.ManualUpload, CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(
			WithRole(HttpMethod.Get, $"/api/v1/benchmarks/by-key/{first.BenchmarkKey}", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] ids = document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
		Assert.Contains(first.Id.ToString(), ids);
		Assert.Contains(second.Id.ToString(), ids);
		Assert.Equal(2, ids.Length);
	}

	[Fact]
	public async Task GetRevision_ById_IncludesDigestAndLifecycleState()
	{
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmarks/{revision.Id}", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(revision.ContentDigest, document.RootElement.GetProperty("content_digest").GetString());
		Assert.Equal(BenchmarkLifecycleStates.Staged, document.RootElement.GetProperty("lifecycle_state").GetString());
	}

	[Fact]
	public async Task GetRevision_UnknownId_Returns404()
	{
		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmarks/{Guid.NewGuid()}", "Viewer"));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetRules_ReturnsSeededRule()
	{
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmarks/{revision.Id}/rules", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement rule = Assert.Single(document.RootElement.EnumerateArray());
		Assert.Equal("SV-000001r1_rule", rule.GetProperty("rule_id").GetString());
		Assert.Equal("V-000001", rule.GetProperty("vuln_id").GetString());
	}

	[Fact]
	public async Task GetRules_UnknownRevision_Returns404()
	{
		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmarks/{Guid.NewGuid()}/rules", "Viewer"));
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetMappingCoverage_CountsEachStatusCorrectly()
	{
		Guid mapped = await SeedCatalogComponentAsync("vcenter");
		Guid ambiguous = await SeedCatalogComponentAsync("esxi");
		Guid unmapped = await SeedCatalogComponentAsync("nsx-manager");
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);

		await _repository.SetMappingAsync(mapped, revision.Id, BenchmarkMappingStatuses.Mapped, false, 0, "exact match", "system", CancellationToken.None);
		await _repository.SetMappingAsync(ambiguous, null, BenchmarkMappingStatuses.Ambiguous, false, 2, "two candidates", "system", CancellationToken.None);
		await _repository.SetMappingAsync(unmapped, null, BenchmarkMappingStatuses.Unmapped, false, 0, "no candidate", "system", CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, "/api/v1/benchmark-mappings", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(1, document.RootElement.GetProperty("mapped_count").GetInt32());
		Assert.Equal(1, document.RootElement.GetProperty("ambiguous_count").GetInt32());
		Assert.Equal(1, document.RootElement.GetProperty("unmapped_count").GetInt32());
		Assert.Equal(0, document.RootElement.GetProperty("suggested_count").GetInt32());
		Assert.Equal(3, document.RootElement.GetProperty("mappings").GetArrayLength());
	}

	[Fact]
	public async Task GetCurrentMapping_NeverMapped_Returns404()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}", "Viewer"));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PutMapping_WithViewerRole_Returns403()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Viewer");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, BenchmarkMappingStatuses.Unmapped, "reason"));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PutMapping_WithCyberRole_Returns403()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Cyber");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, BenchmarkMappingStatuses.Unmapped, "reason"));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PutMapping_UnknownComponent_Returns404()
	{
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{Guid.NewGuid()}", "Admin");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, BenchmarkMappingStatuses.Unmapped, "reason"));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PutMapping_UnknownRevisionId_Returns404()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Admin");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(Guid.NewGuid().ToString(), BenchmarkMappingStatuses.Mapped, "reason"));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PutMapping_InvalidStatus_Returns400()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Admin");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, "not-a-real-status", "reason"));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	/// <summary>
	/// Issue #1002: the endpoint no longer accepts "SRG has no published benchmark" as
	/// an admin-stated fact -- a caller still sending the removed field with a truthy
	/// value gets a pointed 400 naming the replacement, matching this endpoint's
	/// existing fail-closed convention for every other rejected shape.
	/// </summary>
	[Fact]
	public async Task PutMapping_IsSrgNoBenchmarkTrue_IsRejectedAsRemovedField()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Admin");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, BenchmarkMappingStatuses.Unmapped, "reason", IsSrgNoBenchmark: true));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("is_srg_no_benchmark", body, StringComparison.Ordinal);
		Assert.Contains("1002", body, StringComparison.Ordinal);
	}

	/// <summary>A legacy client still echoing a previously-read `false` should not break.</summary>
	[Fact]
	public async Task PutMapping_IsSrgNoBenchmarkFalse_IsAccepted()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		HttpRequestMessage request = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Admin");
		request.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(null, BenchmarkMappingStatuses.Unmapped, "reason", IsSrgNoBenchmark: false));

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	/// <summary>
	/// Issue #1002 item 1: a component bound to an `srg`-kind catalog content release
	/// renders `not_applicable_srg` -- computed at read time from the catalog join,
	/// never from any admin-stated field.
	/// </summary>
	[Fact]
	public async Task GetCurrentMapping_SrgComponent_RendersNotApplicableSrgDerivedState()
	{
		Guid componentId = await SeedCatalogComponentWithKindAsync(CatalogKinds.Srg, "srg-component");
		await _repository.SetMappingAsync(componentId, null, BenchmarkMappingStatuses.Unmapped, false, 0, "no candidate", "system", CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("not_applicable_srg", document.RootElement.GetProperty("derived_state").GetString());
	}

	/// <summary>
	/// Issue #1002 item 2: a stig-kind component with no mapped benchmark revision on
	/// its CURRENT mapping renders a standing `benchmark_missing` alert -- non-blocking,
	/// still queryable, never an error.
	/// </summary>
	[Fact]
	public async Task GetCurrentMapping_StigComponentWithoutBenchmark_RendersBenchmarkMissingDerivedState()
	{
		Guid componentId = await SeedCatalogComponentWithKindAsync(CatalogKinds.Stig, "stig-component");
		await _repository.SetMappingAsync(componentId, null, BenchmarkMappingStatuses.Unmapped, false, 0, "no candidate", "system", CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("benchmark_missing", document.RootElement.GetProperty("derived_state").GetString());
	}

	/// <summary>A stig component WITH a mapped benchmark revision has nothing further to surface -- `derived_state` is null.</summary>
	[Fact]
	public async Task GetCurrentMapping_StigComponentWithBenchmark_HasNullDerivedState()
	{
		Guid componentId = await SeedCatalogComponentWithKindAsync(CatalogKinds.Stig, "stig-mapped-component");
		BenchmarkRevision revision = await _repository.ImportRevisionAsync(InventedCandidate(), BenchmarkSources.ManualUpload, CancellationToken.None);
		await _repository.SetMappingAsync(componentId, revision.Id, BenchmarkMappingStatuses.Mapped, false, 0, "exact match", "system", CancellationToken.None);

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		// WaypointJsonOptions.Default sets DefaultIgnoreCondition.WhenWritingNull, so a
		// null derived_state is OMITTED from the payload entirely, not present-as-null.
		Assert.False(document.RootElement.TryGetProperty("derived_state", out _));
	}

	[Fact]
	public async Task PutMapping_AdminOverride_SupersedesPriorMapping_AndHistoryIsVisible()
	{
		Guid componentId = await SeedCatalogComponentAsync();
		BenchmarkRevision revisionOne = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "first"), BenchmarkSources.ManualUpload, CancellationToken.None);
		BenchmarkRevision revisionTwo = await _repository.ImportRevisionAsync(InventedCandidate(ruleTitle: "second"), BenchmarkSources.ManualUpload, CancellationToken.None);

		await _repository.SetMappingAsync(componentId, revisionOne.Id, BenchmarkMappingStatuses.Suggested, false, 0, "system suggestion", null, CancellationToken.None);

		HttpRequestMessage overrideRequest = WithRole(HttpMethod.Put, $"/api/v1/benchmark-mappings/{componentId}", "Admin");
		overrideRequest.Content = MappingRequestContent(new BenchmarkMappingOverrideRequest(
			revisionTwo.Id.ToString(), BenchmarkMappingStatuses.Mapped, "admin confirmed the correct revision"));

		HttpResponseMessage overrideResponse = await _client.SendAsync(overrideRequest);
		Assert.Equal(HttpStatusCode.OK, overrideResponse.StatusCode);
		using JsonDocument overrideDocument = JsonDocument.Parse(await overrideResponse.Content.ReadAsStringAsync());
		Assert.True(overrideDocument.RootElement.GetProperty("is_admin_override").GetBoolean());
		Assert.Equal(revisionTwo.Id.ToString(), overrideDocument.RootElement.GetProperty("benchmark_revision_id").GetString());

		HttpResponseMessage currentResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}", "Viewer"));
		using JsonDocument currentDocument = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
		Assert.Equal(revisionTwo.Id.ToString(), currentDocument.RootElement.GetProperty("benchmark_revision_id").GetString());

		HttpResponseMessage historyResponse = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}/history", "Viewer"));
		using JsonDocument historyDocument = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
		JsonElement[] history = historyDocument.RootElement.EnumerateArray().ToArray();
		Assert.Equal(2, history.Length);
		Assert.Contains(history, e => e.GetProperty("benchmark_revision_id").GetString() == revisionOne.Id.ToString() && !e.GetProperty("is_current").GetBoolean());
		Assert.Contains(history, e => e.GetProperty("benchmark_revision_id").GetString() == revisionTwo.Id.ToString() && e.GetProperty("is_current").GetBoolean());
	}

	[Fact]
	public async Task GetMappingHistory_NeverMapped_ReturnsEmptyArrayNot404()
	{
		Guid componentId = await SeedCatalogComponentAsync();

		HttpResponseMessage response = await _client.SendAsync(WithRole(HttpMethod.Get, $"/api/v1/benchmark-mappings/{componentId}/history", "Viewer"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(document.RootElement.EnumerateArray());
	}
}
