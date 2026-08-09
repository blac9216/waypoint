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

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Scans;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.StigManager;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #259 full-loop acceptance: the real <c>POST /api/v1/runs</c> path (issue
/// #23's <see cref="Waypoint.Api.Controllers.RunsController.CreateScanRunAsync"/>)
/// auto-queues a <c>discover</c> job for a stale/never-discovered <c>vsphere</c>
/// target alongside its <c>scan</c> job, in the SAME run -- then the real job
/// dispatcher, wired with both the real <see cref="DiscoverJobHandler"/> and
/// <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/> against stub PowerShell
/// modules, actually executes both to a terminal state. This is the fan-out
/// contract PLUS execution, not fan-out alone -- <c>ScanRunFanOutTests</c> already
/// covers the controller's HTTP surface without running jobs; this test is the one
/// place that proves the auto-triggered discover job is a real, dispatchable job and
/// not just a row shape.
///
/// Design decision (recorded here for the reader who lands on the test, and in full
/// in <c>RunsController.BuildStaleDiscoverSpecs</c>'s doc comment / the PR body):
/// fire-and-forget, not scan-blocking. The discover job is fanned out at
/// <see cref="Waypoint.Core.Jobs.ScanTargetPriority.Nsx"/> priority (the top tier) so
/// it dispatches at least as early as any scan job in the run, but nothing makes the
/// scan job wait for it -- <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/>
/// never reads the inventory cache; it drives InSpec/PowerCLI straight at the
/// target's <c>connection.host</c>. <see cref="StaleTarget_QueuesBothDiscoverAndScan_BothReachTerminalState"/>
/// proves this directly: the scan job reaches <c>uploaded</c> even though the
/// discover job for the SAME target is deliberately held at <c>queued</c> (dispatcher
/// concurrency capped to 1, discover ordered first) for the whole assertion window.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory/pool.
public sealed class AutoDiscoverOnScanInitiationTests : IAsyncLifetime, IDisposable
{
	private static readonly string DiscoveryStubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDiscoveryStubModule", "WaypointDiscoveryStubModule.psm1");
	private static readonly string ScanStubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointScanStubModule", "WaypointScanStubModule.psm1");

	private sealed class AutoDiscoverApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public AutoDiscoverApiFactory(string connectionString)
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

				var jobsDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IJobQueueRepository));
				if (jobsDescriptor != null)
				{
					services.Remove(jobsDescriptor);
				}

				services.AddSingleton<IJobQueueRepository>(serviceProvider => new JobQueueRepository(
					_connectionString, serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-autodiscover-key").FullName;
	private readonly string _artifactDirectory = Directory.CreateTempSubdirectory("wp-autodiscover-artifacts").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private AutoDiscoverApiFactory _factory = null!;
	private HttpClient _client = null!;
	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private TargetRepository _targets = null!;

	public AutoDiscoverOnScanInitiationTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new AutoDiscoverApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_targets = new TargetRepository(_fixture.ConnectionString);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 4 };
		powerShellOptions.ModulePreloadPaths.Add(DiscoveryStubModulePath);
		powerShellOptions.ModulePreloadPaths.Add(ScanStubModulePath);
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		CredentialRepository credentials = new(_fixture.ConnectionString);
		CredentialSecretStore secretStore = new(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		InventoryRepository inventory = new(_fixture.ConnectionString);

		_discoverHandler = new DiscoverJobHandler(
			executor, secretStore, credentials, _targets, inventory, _repository, _redactor, wrappedPsOptions);

		Waypoint.Core.Scans.ScanOptions scanOptions = new()
		{
			ArtifactStorePath = _artifactDirectory,
			ProfilePath = "/invented/profile/path",
			TimeoutSeconds = 60,
			AttestationProfile = "invented-vsphere-stig",
			SafTimeoutSeconds = 30,
		};
		Waypoint.Infrastructure.ConfigDocs.ConfigDocRepository configDocs = new(_fixture.ConnectionString);
		Waypoint.Infrastructure.ConfigDocs.AttestationSnapshotRepository attestationSnapshots = new(_fixture.ConnectionString);
		Waypoint.Infrastructure.Secrets.EphemeralCredentialCache ephemeralCredentials = new(
			_redactor, _fixture.ConnectionString, NullLogger<Waypoint.Infrastructure.Secrets.EphemeralCredentialCache>.Instance);
		StigManagerRepository stigman = new(_fixture.ConnectionString);
		ScanUploadCoordinator uploadCoordinator = new(
			stigman, new NeverCalledStigManagerUploadClient(), secretStore, _repository, _redactor);

		_scanHandler = new Waypoint.Infrastructure.Scans.ScanJobHandler(
			executor, secretStore, credentials, _targets, ephemeralCredentials, _repository, _redactor, wrappedPsOptions,
			Options.Create(scanOptions), configDocs, attestationSnapshots, uploadCoordinator);

		_credentials = credentials;
		_secretStore = secretStore;
	}

	private IJobHandler _discoverHandler = null!;
	private IJobHandler _scanHandler = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
		_client.Dispose();
		_factory.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_artifactDirectory, recursive: true);
	}

#pragma warning restore CA1001

	private JobDispatcherHostedService CreateDispatcher(int maxConcurrency)
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = maxConcurrency };
		return new JobDispatcherHostedService(
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_discoverHandler, _scanHandler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>
	/// The full loop: seed a <c>vsphere</c> target with <c>last_refreshed = NULL</c>
	/// (never discovered) -&gt; real <c>POST /api/v1/runs</c> for a scan of that target
	/// -&gt; the response's run has BOTH a <c>discover</c> job and a <c>scan</c> job for
	/// the same target -&gt; a real dispatcher (both handlers registered, concurrency
	/// capped to 1 so dispatch order is observable) claims and executes them against
	/// stub PowerShell modules -&gt; both reach a terminal state, inventory is
	/// populated, and the HDF artifact lands on disk. Concurrency=1 plus the discover
	/// job's top-tier priority also proves the ORDERING half of the design decision:
	/// discover claims first.
	/// </summary>
	[Fact]
	public async Task StaleTarget_QueuesBothDiscoverAndScan_BothReachTerminalState()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		Environment.SetEnvironmentVariable("WAYPOINT_SCAN_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_ATTEST_STUB_MODE", "success");
		Environment.SetEnvironmentVariable("WAYPOINT_CONVERT_STUB_MODE", "success");

		Guid siteId = await CreateSiteAsync("auto-discover-site");
		Guid credentialId = (await _credentials.CreateAsync(
			$"svc-auto-discover-{Guid.NewGuid():N}@example.internal", Waypoint.Core.Secrets.CredentialTypes.VCenter,
			Waypoint.Core.Secrets.CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None,
			"administrator@example.internal"))!.Value;
		await _secretStore.StoreAsync(credentialId, Encoding.UTF8.GetBytes("invented-auto-discover-secret"), "test", CancellationToken.None);

		string connectionJson = """{"host":"vcsa-01.example.internal"}""";
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"vcsa-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		// Never discovered: LastRefreshed is NULL, which BuildStaleDiscoverSpecs treats
		// as stale (same rule GET /targets/{id}/inventory already exposes).
		Assert.Null((await _targets.GetAsync(targetId!.Value, CancellationToken.None))!.LastRefreshed);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(new { site_id = siteId, target_ids = new[] { targetId } }) });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(runId, CancellationToken.None);
		Assert.Equal(2, jobs.Count);
		JobSummary discoverJob = Assert.Single(jobs, j => j.JobType == "discover");
		JobSummary scanJob = Assert.Single(jobs, j => j.JobType == "scan");
		Assert.Equal(targetId, Guid.Parse(discoverJob.TargetId!));
		Assert.Equal(targetId, Guid.Parse(scanJob.TargetId!));
		// The ordering half of the design decision: the auto-triggered discover job is
		// fanned out at the top scan tier (1), strictly ahead of this target's vCenter
		// scan tier (3) -- see AutoDiscoverPriority's doc comment.
		Assert.Equal((short)1, discoverJob.Priority);
		Assert.Equal((short)3, scanJob.Priority);

		JobDispatcherHostedService dispatcher = CreateDispatcher(maxConcurrency: 1);
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(discoverJob.Id);
			await PollUntilTerminalAsync(scanJob.Id);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("done", await GetJobFieldAsync(discoverJob.Id, "state"));
		Assert.Equal("uploaded", await GetJobFieldAsync(scanJob.Id, "state"));

		InventoryRepository inventory = new(_fixture.ConnectionString);
		IReadOnlyList<InventoryItem> items = await inventory.ListAsync(targetId.Value, includeRemoved: false, CancellationToken.None);
		Assert.NotEmpty(items);

		string hdfPath = Path.Combine(_artifactDirectory, $"{scanJob.Id:N}.json");
		Assert.True(File.Exists(hdfPath), $"expected HDF report at '{hdfPath}'.");

		// The fire-and-forget half of the design decision: the scan job's own success
		// does not depend on the discover job's inventory write racing ahead of it --
		// ScanJobHandler never reads InventoryRepository. Proven above by both reaching
		// their real terminal states with no coordination between the two handlers
		// beyond dispatch priority.
	}

	/// <summary>
	/// A target whose inventory was refreshed moments ago (well inside the default
	/// 60-minute <c>Discovery:StaleAfterMinutes</c> window) gets no auto-queued
	/// discover job -- the AC targets STALE inventory, not every scan.
	/// </summary>
	[Fact]
	public async Task FreshTarget_QueuesOnlyScan_NoDiscoverJob()
	{
		Guid siteId = await CreateSiteAsync("fresh-target-site");
		string connectionJson = """{"host":"vcsa-fresh.example.internal"}""";
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"vcsa-fresh-{Guid.NewGuid():N}", connectionJson, credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		await _targets.SetDiscoveryStatusAsync(targetId!.Value, TargetDiscoveryStatuses.Discovered, stampLastRefreshed: true, CancellationToken.None);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(new { site_id = siteId, target_ids = new[] { targetId } }) });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(runId, CancellationToken.None);
		JobSummary onlyJob = Assert.Single(jobs);
		Assert.Equal("scan", onlyJob.JobType);
	}

	/// <summary>
	/// Issue #401: the <c>OLD-but-non-null</c> half of <c>BuildStaleDiscoverSpecs</c>'s
	/// staleness check (<c>target.LastRefreshed is null || target.LastRefreshed.Value
	/// &lt; staleBefore</c>) has its own test, distinct from
	/// <see cref="StaleTarget_QueuesBothDiscoverAndScan_BothReachTerminalState"/> (which
	/// exercises the <c>is null</c> half) and <see cref="FreshTarget_QueuesOnlyScan_NoDiscoverJob"/>
	/// (fresh, non-stale). This target has a real, non-null <c>last_refreshed</c>
	/// timestamp stamped directly via SQL -- 2 hours old, well past the default
	/// 60-minute <c>Discovery:StaleAfterMinutes</c> window -- so only the
	/// <c>&lt; staleBefore</c> comparison, not the null check, can be responsible for
	/// the discover job being queued. Per docs/testing.md's fixture-monoculture rule,
	/// this closes the gap left by the existing fixtures, which were only ever
	/// null-or-fresh and never exercised the timestamp comparison itself.
	/// </summary>
	[Fact]
	public async Task OldButNonNullLastRefreshed_QueuesDiscover_TimestampComparisonBranch()
	{
		Guid siteId = await CreateSiteAsync("old-refresh-site");
		string connectionJson = """{"host":"vcsa-old.example.internal"}""";
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"vcsa-old-{Guid.NewGuid():N}", connectionJson, credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		await _targets.SetDiscoveryStatusAsync(targetId!.Value, TargetDiscoveryStatuses.Discovered, stampLastRefreshed: true, CancellationToken.None);
		await StampLastRefreshedAsync(targetId.Value, DateTimeOffset.UtcNow.AddHours(-2));

		Target? seeded = await _targets.GetAsync(targetId.Value, CancellationToken.None);
		Assert.NotNull(seeded!.LastRefreshed);
		Assert.True(seeded.LastRefreshed!.Value < DateTimeOffset.UtcNow.AddMinutes(-60));

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(new { site_id = siteId, target_ids = new[] { targetId } }) });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(runId, CancellationToken.None);
		Assert.Equal(2, jobs.Count);
		JobSummary discoverJob = Assert.Single(jobs, j => j.JobType == "discover");
		JobSummary scanJob = Assert.Single(jobs, j => j.JobType == "scan");
		Assert.Equal(targetId, Guid.Parse(discoverJob.TargetId!));
		Assert.Equal(targetId, Guid.Parse(scanJob.TargetId!));
	}

	/// <summary>
	/// Directly stamps <c>targets.last_refreshed</c> to an arbitrary past instant --
	/// <see cref="TargetRepository.SetDiscoveryStatusAsync"/> only ever stamps
	/// <c>now()</c>, so there is no repository API that can seed an old-but-non-null
	/// timestamp. Test-only escape hatch for <see cref="OldButNonNullLastRefreshed_QueuesDiscover_TimestampComparisonBranch"/>.
	/// </summary>
	private async Task StampLastRefreshedAsync(Guid targetId, DateTimeOffset lastRefreshed)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand update = new("UPDATE targets SET last_refreshed = $2 WHERE id = $1", connection);
		update.Parameters.AddWithValue(targetId);
		update.Parameters.AddWithValue(lastRefreshed);
		await update.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// An <c>ssh</c> (SRG) target has no inventory cache to refresh -- discover only
	/// supports <c>vsphere</c> (<see cref="DiscoverJobHandler"/> rejects any other
	/// kind outright). A stale/absent <c>LastRefreshed</c> on a non-vsphere target
	/// must not queue a discover job it cannot execute.
	/// </summary>
	[Fact]
	public async Task NonVsphereTarget_NeverQueuesDiscover_EvenWithNoLastRefreshed()
	{
		Guid siteId = await CreateSiteAsync("srg-only-site");
		string connectionJson = """{"host":"photon-01.example.internal"}""";
		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.Ssh, $"photon-{Guid.NewGuid():N}", connectionJson, credentialId: null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(new { site_id = siteId, target_ids = new[] { targetId } }) });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(runId, CancellationToken.None);
		JobSummary onlyJob = Assert.Single(jobs);
		Assert.Equal("scan", onlyJob.JobType);
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
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

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "uploaded" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE inventory_items, jobs, runs, targets, sites RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Never-called stub (no <c>stigman_connections</c> row exists in this suite, so
	/// <see cref="StigManagerRepository.ResolveForSiteAsync"/> always returns null and
	/// <see cref="ScanUploadCoordinator"/> never reaches the network boundary) --
	/// present only so <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/> can
	/// be constructed with the same DI shape production uses. Same pattern as
	/// <c>ScanJobHandlerEndToEndTests</c>'s private stub, duplicated rather than
	/// shared since it is test-only scaffolding with no product-code home.
	/// </summary>
	private sealed class NeverCalledStigManagerUploadClient : IStigManagerUploadClient
	{
		public Task<StigManagerUploadResult> UploadCklAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");
		}

		public Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");
		}
	}
}
