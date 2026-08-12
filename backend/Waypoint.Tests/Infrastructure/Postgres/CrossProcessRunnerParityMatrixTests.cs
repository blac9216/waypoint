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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #444's fresh-stack M1/M2 parity matrix, backend slice: explicit cross-process
/// proofs that the runner architecture (ADRs 0013/0014) preserves the M1/M2 job-queue
/// contract once execution moved out of the API host (#443). "Cross-process" here means
/// what it means in the deployed topology -- a <see cref="WaypointApiFactory"/>-hosted
/// API (enqueue-only, via <see cref="IJobControlRepository"/>) and one or more
/// independent <see cref="JobQueueRepository"/> instances standing in for
/// compliance-runner/download-runner processes (claim/lease/advance, via
/// <see cref="IJobRunnerRepository"/>), each with its own connection, racing or
/// restarting against the one database both sides share. Every scenario below is new
/// composition of already-proven primitives (<c>ScanRunFanOutTests</c>'s API fan-out,
/// <c>JobQueueRepositoryAllowlistClaimTests</c>'s allowlist claim,
/// <c>JobLeaseRecoveryTests</c>'s lease expiry) into the specific matrix rows #444 asks
/// for; it does not re-prove what those files already cover in isolation.
///
/// Mapped in docs/testing.md's parity-matrix section under "cross-process queue
/// behavior", "API restart before claim", "runner restart mid-run recovery",
/// "duplicate-replica claim safety", and "one-domain-unavailable isolation".
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class CrossProcessRunnerParityMatrixTests : IAsyncLifetime
{
	private sealed class MatrixApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;

		public MatrixApiFactory(string connectionString, string keyPath)
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

				// Base WaypointApiFactory wires no master key -- needed for the
				// personal-credential (run_secrets) restart-survival scenario below,
				// same pattern RunSecretScanRunTests uses.
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();

				var storeDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRunSecretStore));
				if (storeDescriptor != null)
				{
					services.Remove(storeDescriptor);
				}

				services.AddSingleton<IRunSecretStore>(serviceProvider => new RunSecretStore(
					_connectionString,
					serviceProvider.GetRequiredService<IEnvelopeCipher>(),
					serviceProvider.GetRequiredService<Waypoint.Core.Logging.ISecretTracker>(),
					serviceProvider.GetRequiredService<ILogger<RunSecretStore>>()));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-matrix-key").FullName;
	private string _keyPath = null!;
	private MatrixApiFactory _factory = null!;
	private HttpClient _client = null!;

	public CrossProcessRunnerParityMatrixTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(_keyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

		_factory = new MatrixApiFactory(_fixture.ConnectionString, _keyPath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		Directory.Delete(_keyDirectory, recursive: true);
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	/// <summary>
	/// Matrix row: "API enqueues, runner-scoped registry claims". The API host (via
	/// <see cref="IJobControlRepository"/>, reached only through HTTP here -- no
	/// shortcut into its DI container) fans a scan run out into queued jobs; a
	/// standalone <see cref="JobQueueRepository"/> instance, constructed exactly the
	/// way a real compliance-runner process would construct its own repository from a
	/// shared connection string, claims and completes the job with no shared in-process
	/// state whatsoever between "enqueue" and "claim".
	/// </summary>
	[Fact]
	public async Task ApiEnqueues_IndependentRunnerRepositoryClaims_AdvancesToTerminal()
	{
		Guid siteId = await CreateSiteAsync("cross-process-site");
		// Issue #259: "ssh" (SRG), not "vsphere" -- avoids an auto-queued discover job
		// so the compliance allowlist claims exactly the one scan job below.
		Guid target = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { target } });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		// Simulates the compliance-runner process: its own repository instance, its own
		// connection, no reference to the API's DI container or HTTP client.
		JobQueueRepository runnerSideRepository = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		ClaimedJob? claimed = await runnerSideRepository.ClaimJobAsync(
			"compliance-runner-1", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);

		Assert.NotNull(claimed);
		Assert.Equal("scan", claimed.JobType);
		Assert.Equal(target, claimed.TargetId);

		bool advanced = await runnerSideRepository.AdvanceStateAsync(
			claimed.Id, "compliance-runner-1", "running", "failed", "invented-unreachable-target", clearLease: true, CancellationToken.None);
		Assert.True(advanced);

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{claimed.RunId!.Value}/jobs", "Viewer", body: null);
		using JsonDocument jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement job = jobs.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == claimed.Id);
		Assert.Equal("failed", job.GetProperty("state").GetString());
	}

	/// <summary>
	/// Matrix row: "API restart before claim (run-secret survival)". The personal-
	/// credential handoff (#434) stores the secret as an encrypted, run-scoped Postgres
	/// row rather than API process memory specifically so an API restart between
	/// enqueue and claim cannot lose it. This test proves the restart, not just the
	/// storage: it disposes and rebuilds the <see cref="MatrixApiFactory"/> host (a
	/// full ASP.NET host teardown/rebuild against the same database, standing in for a
	/// container restart) between the enqueue call and the runner-side claim, then
	/// decrypts through a fresh <c>RunSecretStore</c> instance exactly as a
	/// never-restarted compliance-runner would. <c>RunSecretStoreTests</c> already
	/// proves the store/decrypt round trip in isolation; this is the "survives an API
	/// process boundary" half #444 asks for, which that file's fixture-scoped store
	/// never restarts across.
	/// </summary>
	[Fact]
	public async Task ApiRestartsBeforeClaim_RunSecretStillDecryptsForTheRunnerSideClaim()
	{
		Guid siteId = await CreateSiteAsync("restart-secret-site");
		// Issue #259: "ssh" (SRG), not "vsphere" -- avoids an auto-queued discover job
		// so the compliance allowlist claims exactly the one scan job below.
		Guid target = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");

		HttpResponseMessage runResponse = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, target_ids = new[] { target } }),
			credential = new { kind = "personal", username = "adhoc-operator@example.internal", secret = "invented-restart-canary" }
		});
		Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		// "API restart": tear the whole ASP.NET host down and stand a fresh one up
		// against the same database, with the master key re-read from the same file
		// path (a persisted volume across a real container restart, not process
		// memory) -- nothing survives in process memory across this boundary except
		// what is durably in Postgres and on that mounted key file.
		_client.Dispose();
		_factory.Dispose();
		_factory = new MatrixApiFactory(_fixture.ConnectionString, _keyPath);
		_client = _factory.CreateClient();

		JobQueueRepository runnerSideRepository = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		ClaimedJob? claimed = await runnerSideRepository.ClaimJobAsync(
			"compliance-runner-1", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(runId, claimed.RunId);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand exists = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", connection);
		exists.Parameters.AddWithValue(runId);
		Assert.Equal(1L, (long)(await exists.ExecuteScalarAsync())!);

		// The decisive proof: a runner process that never knew about the pre-restart
		// API instance can still decrypt the secret through the freshly-restarted
		// API's store (a real runner would call the API's own decrypt-serving surface;
		// here the store itself is the reachable unit under Postgres-fixture tests).
		IRunSecretStore postRestartStore = _factory.Services.GetRequiredService<IRunSecretStore>();
		using DecryptedRunSecret? decrypted = await postRestartStore.DecryptAsync(runId, claimed.Id, "compliance-runner-1", CancellationToken.None);
		Assert.NotNull(decrypted);
		Assert.Equal("invented-restart-canary", decrypted!.Secret);
	}

	/// <summary>
	/// Matrix row: "runner restart mid-run recovery". A job claimed by one runner
	/// worker id (simulating compliance-runner instance A) never renews its lease and
	/// never advances -- standing in for the process crashing mid-execution. A second,
	/// independent worker id (instance A restarted, or its replacement) then relies on
	/// <see cref="IJobRunnerRepository.RecoverExpiredLeasesAsync"/> to observe the
	/// expired lease and requeue the job, which it can then claim and complete itself.
	/// <c>JobLeaseRecoveryTests</c> proves the recovery primitive in isolation; this
	/// composes it with a claim before AND after, across two distinct worker
	/// identities, to prove the end-to-end "runner process died, its replacement
	/// finishes the work" story #444 asks for.
	/// </summary>
	[Fact]
	public async Task RunnerRestartsMidRun_ExpiredLeaseRecovered_AndClaimedByTheReplacementWorker()
	{
		Guid siteId = await CreateSiteAsync("runner-restart-site");
		Guid target = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { target } });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		JobQueueRepository crashedWorker = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		ClaimedJob? claimed = await crashedWorker.ClaimJobAsync(
			"compliance-runner-instance-a", TimeSpan.FromMilliseconds(50), JobCapabilities.Compliance, CancellationToken.None);
		Assert.NotNull(claimed);

		// The lease expires and instance-a never renews or advances -- the crash.
		await Task.Delay(TimeSpan.FromMilliseconds(200));

		JobQueueRepository replacementWorker = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		IReadOnlyList<RecoveredJob> recovered = await replacementWorker.RecoverExpiredLeasesAsync(batchSize: 10, CancellationToken.None);
		Assert.Contains(recovered, job => job.Id == claimed.Id);

		ClaimedJob? reclaimedByReplacement = await replacementWorker.ClaimJobAsync(
			"compliance-runner-instance-a-restarted", TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
		Assert.NotNull(reclaimedByReplacement);
		Assert.Equal(claimed.Id, reclaimedByReplacement.Id);

		bool advanced = await replacementWorker.AdvanceStateAsync(
			reclaimedByReplacement.Id, "compliance-runner-instance-a-restarted", "running", "failed",
			"invented-unreachable-target", clearLease: true, CancellationToken.None);
		Assert.True(advanced);
	}

	/// <summary>
	/// Matrix row: "duplicate-replica claim safety". Compose can scale
	/// <c>compliance-runner</c> to more than one replica (ADR-0013/#442); two
	/// independent <see cref="JobQueueRepository"/> instances standing in for two
	/// replicas of the SAME runner image race to claim from a shared queue of several
	/// jobs. Every job must be claimed exactly once across the pair -- no job claimed
	/// by both, no job claimed zero times.
	/// <c>JobQueueRepositoryAllowlistClaimTests.IdenticalReplicas_...</c> already proves
	/// this at higher concurrency (64-way) against directly-seeded rows; this row
	/// specifically threads it through the API's own fan-out (multiple targets on one
	/// scan run) so the replica-safety proof composes with the real enqueue path rather
	/// than a hand-seeded queue.
	/// </summary>
	[Fact]
	public async Task TwoReplicasOfTheSameRunner_RaceAFannedOutRun_NoDoubleClaimNoOrphan()
	{
		Guid siteId = await CreateSiteAsync("replica-race-site");
		Guid[] targets =
		[
			await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}"""),
			await CreateTargetAsync(siteId, "vsphere", "vcsa-02", """{"host":"vcsa-02.example.internal"}"""),
			await CreateTargetAsync(siteId, "vsphere", "vcsa-03", """{"host":"vcsa-03.example.internal"}"""),
			await CreateTargetAsync(siteId, "vsphere", "vcsa-04", """{"host":"vcsa-04.example.internal"}"""),
		];

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = targets });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		JobQueueRepository replicaOne = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		JobQueueRepository replicaTwo = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		async Task<List<ClaimedJob>> DrainAsync(JobQueueRepository repository, string workerId)
		{
			List<ClaimedJob> claimed = [];
			while (true)
			{
				ClaimedJob? job = await repository.ClaimJobAsync(workerId, TimeSpan.FromMinutes(5), JobCapabilities.Compliance, CancellationToken.None);
				if (job is null)
				{
					return claimed;
				}

				claimed.Add(job);
			}
		}

		List<ClaimedJob>[] results = await Task.WhenAll(
			DrainAsync(replicaOne, "compliance-runner-replica-1"),
			DrainAsync(replicaTwo, "compliance-runner-replica-2"));

		Guid[] allClaimedIds = [.. results.SelectMany(list => list).Select(job => job.Id)];
		Guid[] allClaimedScanIds = [.. results.SelectMany(list => list).Where(job => job.JobType == "scan").Select(job => job.Id)];

		Assert.Equal(allClaimedIds.Length, allClaimedIds.Distinct().Count());
		Assert.Equal(targets.Length, allClaimedScanIds.Length);
	}

	/// <summary>
	/// Matrix row: "one-domain-unavailable isolation". A download-only allowlisted
	/// repository instance (standing in for the download-runner process being the only
	/// one running -- compliance-runner stopped/unavailable) must never claim a scan
	/// job even when it is the only queued work and nothing else is competing for it;
	/// the scan job stays queued, exactly as it would if compliance-runner really were
	/// down, and a download job queued in the same run is still claimed and completed
	/// normally. This is the fan-out-and-partial-availability shape #444's "stopping
	/// one runner leaves the other domain functional" smoke-script step also covers
	/// live; this is its Postgres-backed proof that no query-level fallback exists to
	/// undermine that operational property.
	/// </summary>
	[Fact]
	public async Task OneExecutionDomainUnavailable_TheOtherDomainsQueueIsUnaffected()
	{
		Guid siteId = await CreateSiteAsync("domain-isolation-site");
		Guid target = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		HttpResponseMessage scanResponse = await PostRunAsync(new { site_id = siteId, target_ids = new[] { target } });
		Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

		Guid downloadJobId = await SeedDownloadJobAsync();

		// download-runner is the only process still up; compliance-runner is down.
		JobQueueRepository downloadOnlyRunner = new(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		ClaimedJob? claimed = await downloadOnlyRunner.ClaimJobAsync(
			"download-runner-1", TimeSpan.FromMinutes(5), JobCapabilities.Download, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(downloadJobId, claimed.Id);
		Assert.Equal("download", claimed.JobType);

		// Nothing else claimable within the download allowlist -- the scan job(s) from
		// the fanned-out run are invisible to it, exactly as they would be if
		// compliance-runner were still down rather than merely unqueried here.
		ClaimedJob? nothingElse = await downloadOnlyRunner.ClaimJobAsync(
			"download-runner-1", TimeSpan.FromMinutes(5), JobCapabilities.Download, CancellationToken.None);
		Assert.Null(nothingElse);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM jobs WHERE job_type = 'scan' AND state = 'queued'", connection);
		Assert.True((long)(await count.ExecuteScalarAsync())! >= 1L);
	}

	private async Task<Guid> SeedDownloadJobAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, target_name) VALUES ('download', 1, 'queued', $1) RETURNING id", connection);
		insert.Parameters.AddWithValue($"invented-artifact-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> PostRunAsync(object scopeBody)
	{
		return await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber",
			new { run_type = "scan", scope = JsonSerializer.Serialize(scopeBody) });
	}

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}" });
		if (!response.IsSuccessStatusCode)
		{
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"CreateSiteAsync failed: {(int)response.StatusCode} {errorBody}");
		}

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
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE jobs, run_secrets, runs, targets, sites RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
