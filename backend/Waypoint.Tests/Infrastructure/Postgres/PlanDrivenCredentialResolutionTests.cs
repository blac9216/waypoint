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

using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #736 (epic #726 Wave 2, ADR-0024), end to end against real Postgres and the
/// real API: a scan run's <c>target_scope</c> plan (#734/PR #857) drives credential
/// purpose resolution from each ACCEPTED plan item's own catalog-derived
/// <c>catalog_credential_requirements</c>, not the coarse static
/// <c>CredentialPurposeMatrix.RequiredScanPurposes(target.Kind)</c> matrix. Covers the
/// AC "VCSA SSH is required exactly when a selected VCSA component consumes it", the
/// ADR-0024 precedence chain (ad hoc &gt; saved override &gt; run-level &gt; target
/// binding) applied per-component, per-component-isolated skip-not-fail on an
/// unresolved purpose, multi-credential snapshot (two purposes for one target whose
/// plan items jointly require both), and that a legacy (no <c>target_scope</c>)
/// request is completely unaffected.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class PlanDrivenCredentialResolutionTests : IAsyncLifetime
{
	private sealed class PlanCredentialApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;

		public PlanCredentialApiFactory(string connectionString, string keyPath)
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
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));
				services.AddSingleton<IProfileRepository>(new ProfileRepository(_connectionString));
				services.AddSingleton<IComponentRepository>(new ComponentRepository(_connectionString));
				services.AddSingleton<ICatalogRepository>(new CatalogRepository(_connectionString));
				services.AddSingleton<ScopeResolutionService>();
				services.AddSingleton<IRunScopeSnapshotRepository>(new RunScopeSnapshotRepository(_connectionString));
				services.AddSingleton<IBaselineRepository>(new BaselineRepository(_connectionString));

				// Issue #735: ScanPlannerService now also depends on PlanConfigResolutionService
				// (config-doc resolution), which in turn depends on ConfigDocRepository -- both
				// must be re-registered against THIS fixture's connection string, same reason
				// every other repository above is, or CreateScanRunAsync's plan-compile step
				// 500s trying to reach Program.cs's default (unreachable-in-test) connection.
				services.AddSingleton(new Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository(_connectionString));
				services.AddSingleton<Waypoint.Infrastructure.ConfigDocs.PlanConfigResolutionService>();
				services.AddSingleton<ScanPlannerService>();
				services.AddSingleton<Waypoint.Core.Scans.IScanPlanRepository>(new ScanPlanRepository(_connectionString));

				foreach (Type serviceType in new[] { typeof(IJobControlRepository), typeof(IJobRunnerRepository) })
				{
					var jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
					if (jobsDescriptor != null)
					{
						services.Remove(jobsDescriptor);
					}
				}

				services.AddSingleton(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
				services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
				services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());

				// Base WaypointApiFactory wires no master key (most suites never touch
				// encryption) -- the ad hoc credential test needs the real IRunSecretStore
				// to actually encrypt/store, same pattern as RunSecretScanRunTests. The
				// default IRunSecretStore registration (Program.cs) also points at
				// appsettings.json's base connection string rather than this fixture's, so
				// it must be re-registered here too (same reason IProfileRepository is
				// above) or every ad hoc scan-run POST 500s against the wrong database.
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();

				var runSecretStoreDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRunSecretStore));
				if (runSecretStoreDescriptor != null)
				{
					services.Remove(runSecretStoreDescriptor);
				}

				services.AddSingleton<IRunSecretStore>(serviceProvider => new RunSecretStore(
					_connectionString,
					serviceProvider.GetRequiredService<IEnvelopeCipher>(),
					serviceProvider.GetRequiredService<Waypoint.Core.Logging.ISecretTracker>(),
					serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunSecretOptions>>(),
					serviceProvider.GetRequiredService<ILogger<RunSecretStore>>()));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-plan-credential-key").FullName;
	private PlanCredentialApiFactory _factory = null!;
	private HttpClient _client = null!;
	private ComponentRepository _components = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private Guid _profileId;

	public PlanDrivenCredentialResolutionTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

		_factory = new PlanCredentialApiFactory(_fixture.ConnectionString, keyPath);
		_client = _factory.CreateClient();
		_components = new ComponentRepository(_fixture.ConnectionString);
		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);

		ProfileRepository profiles = new(_fixture.ConnectionString);
		await profiles.ReplaceAllAsync(
			[new ProfileUpsert("plan-credential-profile", "Plan Credential Test Profile", "1.0.0", "invented-commit-plancred", ProfileStates.Current)],
			CancellationToken.None);
		_profileId = (await profiles.ListAsync(CancellationToken.None)).Single().Id;
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		Directory.Delete(_keyDirectory, recursive: true);
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	[Fact]
	public async Task CreateScanRun_PlanItemRequiringOnlyVSphereApi_NeverRequiresVcsaSsh()
	{
		// AC: VCSA SSH is required exactly when a selected VCSA component consumes it --
		// a plain ESXi-shaped component whose catalog execution profile declares only
		// vsphere-api must NOT demand a vcsa-ssh binding at all, unlike the old coarse
		// per-KIND matrix which required it opportunistically for every vsphere target.
		Guid siteId = await CreateSiteAsync("plan-cred-esxi-only");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "esxi-only");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		Guid componentId = await SeedComponentWithRequirementsAsync(targetId, "esxi-only", ["vsphere-api"]);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentId } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		IReadOnlyDictionary<string, Guid?> snapshot = await ReadScanJobSnapshotAsync(runId, targetId);
		Assert.Equal(["vsphere-api"], snapshot.Keys);
	}

	[Fact]
	public async Task CreateScanRun_VcsaComponentRequiringBothPurposes_SnapshotsBothAsMultiCredential()
	{
		// A VCSA component whose catalog execution profile declares BOTH vsphere-api and
		// vcsa-ssh -- multiple credentials for one execution item genuinely required.
		Guid siteId = await CreateSiteAsync("plan-cred-vcsa-dual");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-dual");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		Guid vcsaSshCred = await SeedCredentialAsync("ssh", "root");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		await SeedBindingAsync(targetId, "vcsa-ssh", vcsaSshCred);

		Guid componentId = await SeedComponentWithRequirementsAsync(targetId, "vcsa-dual", ["vsphere-api", "vcsa-ssh"]);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { componentId } },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		IReadOnlyDictionary<string, Guid?> snapshot = await ReadScanJobSnapshotAsync(runId, targetId);
		Assert.Equal(2, snapshot.Count);
		Assert.Equal(vcenterCred, snapshot["vsphere-api"]);
		Assert.Equal(vcsaSshCred, snapshot["vcsa-ssh"]);
	}

	[Fact]
	public async Task CreateScanRun_AdHocOverrideOutranksSavedOverrideAndTargetBinding_ForAPlanDrivenPurpose()
	{
		// ADR-0024 precedence for an interactive run: run-scoped override (ad hoc, here)
		// > component/target binding. This proves the precedence chain still applies
		// per-purpose even when the purpose set itself came from the plan, not the
		// static matrix.
		Guid siteId = await CreateSiteAsync("plan-cred-adhoc-precedence");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-precedence");
		Guid boundCred = await SeedCredentialAsync("vcenter", "bound@example.internal");
		await SeedBindingAsync(targetId, "vsphere-api", boundCred);

		Guid componentId = await SeedComponentWithRequirementsAsync(targetId, "precedence", ["vsphere-api"]);

		HttpResponseMessage response = await PostRunAsync(
			new
			{
				site_id = siteId,
				target_scope = new { mode = "explicit", component_ids = new[] { componentId } },
			},
			adHocCredentials: [new { target_id = targetId, purpose = "vsphere-api", username = "adhoc-user", secret = "adhoc-secret-value" }]);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// The ad hoc override wins: the snapshot row for vsphere-api is a run-secret
		// (credential_id null, is_run_secret true), never the target's own binding.
		(bool isRunSecret, Guid? credentialId) = await ReadSnapshotDetailAsync(runId, targetId, "vsphere-api");
		Assert.True(isRunSecret);
		Assert.Null(credentialId);
	}

	[Fact]
	public async Task CreateScanRun_ComponentWithUnresolvableCredential_SkipsOnlyThatComponent_SiblingStillRuns()
	{
		// ADR-0024: "A missing, incompatible, or ambiguous credential affects only
		// components requiring that purpose... The run is incomplete, not rejected
		// wholesale." Two independent VCSA-shaped components on two different targets;
		// only one target has the vcsa-ssh binding assigned. The whole run must still
		// succeed (202), the bound target's job must carry its full snapshot, and the
		// unbound target must fan out with NO scan job at all (its only candidate
		// component was demoted to a plan skip, leaving it with nothing to execute) --
		// proving the run is incomplete, not rejected.
		Guid siteId = await CreateSiteAsync("plan-cred-per-component-skip");
		Guid boundTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-bound");
		Guid unboundTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-unbound");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		Guid vcsaSshCred = await SeedCredentialAsync("ssh", "root");
		await SeedBindingAsync(boundTarget, "vsphere-api", vcenterCred);
		await SeedBindingAsync(boundTarget, "vcsa-ssh", vcsaSshCred);
		await SeedBindingAsync(unboundTarget, "vsphere-api", vcenterCred);
		// Deliberately no vcsa-ssh binding for unboundTarget.

		Guid boundComponent = await SeedComponentWithRequirementsAsync(boundTarget, "bound", ["vsphere-api", "vcsa-ssh"]);
		Guid unboundComponent = await SeedComponentWithRequirementsAsync(unboundTarget, "unbound", ["vsphere-api", "vcsa-ssh"]);

		HttpResponseMessage response = await PostRunAsync(new
		{
			site_id = siteId,
			target_scope = new { mode = "explicit", component_ids = new[] { boundComponent, unboundComponent } },
		});

		// The run as a whole succeeds -- one target's per-component credential gap never
		// rejects the whole run (this is the behavior this issue changes: pre-#736 this
		// would have been an all-or-nothing credential_binding_gaps 400 at the target
		// granularity for the coarse static matrix).
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		IReadOnlyDictionary<string, Guid?> boundSnapshot = await ReadScanJobSnapshotAsync(runId, boundTarget);
		Assert.Equal(2, boundSnapshot.Count);
		Assert.Equal(vcenterCred, boundSnapshot["vsphere-api"]);
		Assert.Equal(vcsaSshCred, boundSnapshot["vcsa-ssh"]);

		ScanPlanRepository plans = new(_fixture.ConnectionString);
		Waypoint.Core.Scans.ScanPlan? plan = await plans.GetForRunAsync(runId, CancellationToken.None);
		Assert.NotNull(plan);
		Assert.Contains(plan!.Skips, skip => skip.ComponentId == unboundComponent && skip.Reason == "missing_binding");
		Assert.DoesNotContain(plan.Items, item => item.ComponentId == unboundComponent);
	}

	[Fact]
	public async Task CreateScanRun_LegacyRequestWithNoTargetScope_KeepsStaticMatrixBehaviorUnchanged()
	{
		// A legacy target_ids/profile_id-only request (no target_scope) has no plan at
		// all -- issue #736's plan-driven resolution must never engage, so this keeps
		// requiring vsphere-api only (the pre-#736 static-matrix default for a vsphere
		// target with no VCSA scan component selected on the wire).
		Guid siteId = await CreateSiteAsync("plan-cred-legacy-unaffected");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "legacy");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { targetId }, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		IReadOnlyDictionary<string, Guid?> snapshot = await ReadScanJobSnapshotAsync(runId, targetId);
		Assert.Equal(["vsphere-api"], snapshot.Keys);
	}

	/// <summary>Seeds one complete catalog execution-profile chain declaring exactly <paramref name="purposes"/> as required, with an active SRG baseline, and links one discovered component on <paramref name="targetId"/> to it.</summary>
	private async Task<Guid> SeedComponentWithRequirementsAsync(Guid targetId, string suffix, string[] purposes)
	{
		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}-{Guid.NewGuid():N}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, "vmware", $"vsphere-{suffix}-{Guid.NewGuid():N}", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "8.0.3", "8.0.3", CancellationToken.None);
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"comp-{suffix}", "Component", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, null),
			CancellationToken.None);
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, CatalogKinds.Srg, $"release-{suffix}-{Guid.NewGuid():N}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}-{Guid.NewGuid():N}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		foreach (string purpose in purposes)
		{
			await _catalog.AddCredentialRequirementAsync(executionProfile.Id, purpose, isRequired: true, CancellationToken.None);
		}

		ContentRevision revision = await _baselines.RecordStagedRevisionAsync(
			$"commit-{suffix}-{Guid.NewGuid():N}", $"digest-{suffix}-{Guid.NewGuid():N}", $"revisions/{suffix}-{Guid.NewGuid():N}", CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);

		string vendorIdentity = $"host-{suffix}-{Guid.NewGuid():N}";
		await _components.UpsertDiscoveredAsync(
			targetId, [new DiscoveredComponent("esxi", vendorIdentity, $"{vendorIdentity}.example.internal", null, catalogComponent.Id, "8.0.3")], CancellationToken.None);
		Component seeded = (await _components.ListForTargetAsync(targetId, includeRetired: true, CancellationToken.None))
			.Single(c => c.VendorIdentity == vendorIdentity);
		return seeded.Id;
	}

	private async Task<HttpResponseMessage> PostRunAsync(object scopeBody, object[]? adHocCredentials = null)
	{
		Dictionary<string, object?> body = new(StringComparer.Ordinal)
		{
			["run_type"] = "scan",
			["scope"] = JsonSerializer.Serialize(scopeBody),
		};

		// ad_hoc_credentials requires Operator+ (ADR-0011 personal tier, same floor as
		// the flat inline "credential" body) -- Cyber is sufficient for every other
		// scenario in this file, so only escalate the role when this request actually
		// carries an ad hoc entry.
		string role = adHocCredentials is null ? "Cyber" : "Operator";
		if (adHocCredentials is not null)
		{
			body["ad_hoc_credentials"] = adHocCredentials;
		}

		return await SendAsync(HttpMethod.Post, "/api/v1/runs", role, body);
	}

	private static async Task<Guid> ReadRunIdAsync(HttpResponseMessage response)
	{
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return Guid.Parse(created.RootElement.GetProperty("run_id").GetString()!);
	}

	private async Task<IReadOnlyDictionary<string, Guid?>> ReadScanJobSnapshotAsync(Guid runId, Guid targetId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			SELECT b.purpose, b.credential_id
			FROM job_credential_bindings b
			JOIN jobs j ON j.id = b.job_id
			WHERE j.run_id = $1 AND j.target_id = $2 AND j.job_type = 'scan'
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(targetId);
		Dictionary<string, Guid?> snapshot = new(StringComparer.Ordinal);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			snapshot[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetGuid(1);
		}

		return snapshot;
	}

	private async Task<(bool IsRunSecret, Guid? CredentialId)> ReadSnapshotDetailAsync(Guid runId, Guid targetId, string purpose)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			SELECT b.credential_id, b.is_run_secret
			FROM job_credential_bindings b
			JOIN jobs j ON j.id = b.job_id
			WHERE j.run_id = $1 AND j.target_id = $2 AND j.job_type = 'scan' AND b.purpose = $3
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue(purpose);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync(), $"no snapshot row for run {runId} target {targetId} purpose {purpose}");
		return (reader.GetBoolean(1), reader.IsDBNull(0) ? null : reader.GetGuid(0));
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new Dictionary<string, object?> { ["name"] = $"{namePrefix}-{Guid.NewGuid():N}" });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> CreateTargetAsync(Guid siteId, string kind, string name)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, discovery_status, last_refreshed)
			VALUES ($1, $2, $3, $4::jsonb, 'discovered', now())
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(siteId);
		command.Parameters.AddWithValue(kind);
		command.Parameters.AddWithValue($"{name}-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue($$"""{"host":"{{name}}.example.internal"}""");
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedCredentialAsync(string credentialType, string username)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type, username) VALUES ($1, $2, $3) RETURNING id", connection);
		command.Parameters.AddWithValue($"plan-credential-{credentialType}-{Guid.NewGuid():N}");
		command.Parameters.AddWithValue(credentialType);
		command.Parameters.AddWithValue(username);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task SeedBindingAsync(Guid targetId, string purpose, Guid credentialId)
	{
		await ExecuteAsync(
			"INSERT INTO target_credential_bindings (target_id, purpose, credential_id) VALUES ($1, $2, $3)",
			targetId, purpose, credentialId);
	}

	private async Task ExecuteAsync(string sql, params object[] parameters)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		foreach (object parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter);
		}

		await command.ExecuteNonQueryAsync();
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
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				jobs, runs, run_secrets, run_scope_snapshots, scan_plan_items, scan_plans,
				baselines, content_revisions,
				component_observations, components, targets, sites, credentials,
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions,
				benchmark_component_mappings, benchmark_rules, benchmark_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}
}
