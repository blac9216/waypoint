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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #10 (M1 vertical slice) end to end against real Postgres: the
/// <c>/downloads</c> REST surface backed by the real repositories and the real job
/// engine -- role gates, that POST fans out one <c>download</c> job (its own
/// single-job run) per requested artifact, list/pagination, and that DELETE cancels
/// cleanly. No handler dispatch happens in these tests (no dispatcher is started) --
/// the full claim-through-verify loop is <c>DownloadJobHandlerEndToEndTests</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class DownloadsApiTests : IAsyncLifetime
{
	private sealed class DownloadsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public DownloadsApiFactory(string connectionString)
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
				services.AddSingleton<IDownloadRepository>(new DownloadRepository(_connectionString));
				// Issue #415: one JobQueueRepository instance satisfies both focused
				// interfaces DownloadsController (control) and the runner path resolve.
				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);

				// Issue #560: GET /downloads/readiness needs both -- same
				// container-level override CredentialsApiTests uses (config-level
				// connection-string overrides do not stick for this minimal-hosting
				// factory).
				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton<IWorkerRegistryReader>(new WorkerRegistryRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private DownloadsApiFactory _factory = null!;
	private HttpClient _client = null!;
	private DepotArtifactRepository _artifacts = null!;

#pragma warning restore CA1001

	public DownloadsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetCatalogDataAsync();

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_factory = new DownloadsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task PostDownloads_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.PostAsync("/api/v1/downloads", JsonBody(new { depot_artifact_ids = Array.Empty<string>() }));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	public async Task PostDownloads_BelowOperator_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>
	/// Issue #30: POST widened from Admin-only (M1 stopgap) to Operator+, matching
	/// api-contract.md's `/downloads` row ("Operator+") and domain-model.md's Operator
	/// capability ("download/catalog/content-library management").
	/// </summary>
	[Fact]
	public async Task PostDownloads_WithOperatorRole_QueuesSuccessfully()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
	}

	/// <summary>
	/// The N-artifacts fan-out acceptance criterion (ADR-0008): queuing three artifacts
	/// creates ONE run containing three queued <c>download</c> jobs (one per artifact),
	/// and the 202 body returns that single run_id -- not three separate runs.
	/// </summary>
	[Fact]
	public async Task PostDownloads_WithAdminRole_QueuesOneRunWithOneJobPerArtifact()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact1 = await SeedArtifactAsync($"{tag}-1");
		Guid artifact2 = await SeedArtifactAsync($"{tag}-2");
		Guid artifact3 = await SeedArtifactAsync($"{tag}-3");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact1.ToString(), artifact2.ToString(), artifact3.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] downloadIds = document.RootElement.GetProperty("download_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(3, downloadIds.Length);

		// The 202 returns one honest batch run_id (ADR-0008), not an arbitrary first run.
		string responseRunId = document.RootElement.GetProperty("run_id").GetString()!;

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// All three downloads belong to that single run, and it carries exactly three jobs.
		await using (NpgsqlCommand runCount = new(
			"SELECT count(DISTINCT run_id) FROM downloads WHERE id::text = ANY($1)", connection))
		{
			runCount.Parameters.AddWithValue(downloadIds);
			Assert.Equal(1L, (long)(await runCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'download' AND state = 'queued'", connection))
		{
			jobCount.Parameters.AddWithValue(Guid.Parse(responseRunId));
			Assert.Equal(3L, (long)(await jobCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand runMatch = new(
			"SELECT count(*) FROM downloads WHERE id::text = ANY($1) AND run_id = $2", connection))
		{
			runMatch.Parameters.AddWithValue(downloadIds);
			runMatch.Parameters.AddWithValue(Guid.Parse(responseRunId));
			Assert.Equal(3L, (long)(await runMatch.ExecuteScalarAsync())!);
		}
	}

	[Fact]
	public async Task PostDownloads_UnknownArtifactId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { Guid.NewGuid().ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>
	/// Issue #1479: <c>POST /downloads/binaries</c> with explicit depot artifact ids
	/// creates ONE run of type <c>binaries-download</c> containing one queued
	/// <c>binaries-download</c> job per artifact (the same scan-style fanout
	/// <see cref="PostDownloads_WithAdminRole_QueuesOneRunWithOneJobPerArtifact"/> proves
	/// for the legacy path), distinct from that legacy <c>download</c> job type.
	/// </summary>
	[Fact]
	public async Task PostBinariesDownload_WithArtifactIds_QueuesOneRunWithOneJobPerArtifact()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact1 = await SeedArtifactAsync($"{tag}-1");
		Guid artifact2 = await SeedArtifactAsync($"{tag}-2");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact1.ToString(), artifact2.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] ids = document.RootElement.GetProperty("depot_artifact_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(2, ids.Length);
		string runId = document.RootElement.GetProperty("run_id").GetString()!;

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand runType = new("SELECT run_type FROM runs WHERE id = $1", connection);
		runType.Parameters.AddWithValue(Guid.Parse(runId));
		Assert.Equal("binaries-download", (string)(await runType.ExecuteScalarAsync())!);

		await using NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'binaries-download' AND state = 'queued'", connection);
		jobCount.Parameters.AddWithValue(Guid.Parse(runId));
		Assert.Equal(2L, (long)(await jobCount.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #1479 AC: "Whole-release selection resolves to its member artifacts at
	/// enqueue time" -- a release selector (product+version, the depot catalog's own
	/// release identity, since it has no separate release-id entity) fans out one job
	/// per matching artifact, and never touches a differently-versioned sibling.
	/// </summary>
	[Fact]
	public async Task PostBinariesDownload_WithRelease_ResolvesMemberArtifactsAndQueuesOneJobEach()
	{
		string tag = Guid.NewGuid().ToString("N");
		await SeedArtifactWithProductVersionAsync($"{tag}-a", "ESXi", "9.1.0");
		await SeedArtifactWithProductVersionAsync($"{tag}-b", "ESXi", "9.1.0");
		await SeedArtifactWithProductVersionAsync($"{tag}-other", "ESXi", "9.0.0");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { release = new { product = "ESXi", version = "9.1.0" } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] ids = document.RootElement.GetProperty("depot_artifact_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(2, ids.Length);

		string runId = document.RootElement.GetProperty("run_id").GetString()!;
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'binaries-download'", connection);
		jobCount.Parameters.AddWithValue(Guid.Parse(runId));
		Assert.Equal(2L, (long)(await jobCount.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #1479 AC 3, PR #1596 review finding 1: <c>PageRequest.Limit</c> hard-clamps
	/// to 200 (<c>MaxLimit</c>), so a release with more than 200 member artifacts must
	/// not silently enqueue jobs for only the first page under a 202 as if it were
	/// complete. Seeds 201 members and asserts all 201 are resolved and fanned out.
	/// </summary>
	[Fact]
	public async Task PostBinariesDownload_ReleaseWithMoreThanPageLimitMembers_ResolvesAllMembers()
	{
		string tag = Guid.NewGuid().ToString("N");
		const int MemberCount = 201;
		for (int i = 0; i < MemberCount; i++)
		{
			await SeedArtifactWithProductVersionAsync($"{tag}-{i}", "ESXi", "9.2.0");
		}

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { release = new { product = "ESXi", version = "9.2.0" } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] ids = document.RootElement.GetProperty("depot_artifact_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(MemberCount, ids.Length);
		Assert.Equal(MemberCount, ids.Distinct().Count());

		string runId = document.RootElement.GetProperty("run_id").GetString()!;
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'binaries-download' AND state = 'queued'", connection);
		jobCount.Parameters.AddWithValue(Guid.Parse(runId));
		Assert.Equal((long)MemberCount, (long)(await jobCount.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// PR #1596 review finding 2: a repeated id in <c>depot_artifact_ids</c> must not
	/// fan out two racing jobs for the same artifact. Dedupe to exactly one job.
	/// </summary>
	[Fact]
	public async Task PostBinariesDownload_DuplicateArtifactId_QueuesExactlyOneJob()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact.ToString(), artifact.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] ids = document.RootElement.GetProperty("depot_artifact_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Single(ids);
		Assert.Equal(artifact.ToString(), ids[0]);

		string runId = document.RootElement.GetProperty("run_id").GetString()!;
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND job_type = 'binaries-download'", connection);
		jobCount.Parameters.AddWithValue(Guid.Parse(runId));
		Assert.Equal(1L, (long)(await jobCount.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PostBinariesDownload_UnknownRelease_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { release = new { product = "NoSuchProduct", version = "0.0.0" } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task PostBinariesDownload_UnknownArtifactId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { Guid.NewGuid().ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>Neither selection mode supplied is an ambiguous, rejected request -- never a silent empty no-op run.</summary>
	[Fact]
	public async Task PostBinariesDownload_NeitherSelectionMode_Returns400()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	/// <summary>Both selection modes supplied together is ambiguous too -- rejected rather than silently preferring one.</summary>
	[Fact]
	public async Task PostBinariesDownload_BothSelectionModes_Returns400()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new
			{
				depot_artifact_ids = new[] { artifact.ToString() },
				release = new { product = "ESXi", version = "9.1.0" },
			}),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	public async Task PostBinariesDownload_BelowOperator_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { depot_artifact_ids = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostBinariesDownload_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.PostAsync(
			"/api/v1/downloads/binaries", JsonBody(new { depot_artifact_ids = Array.Empty<string>() }));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetDownloads_ReturnsQueuedRows_WithXTotalCount()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);
		await QueueDownloadAsync(artifact);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? totals));
		Assert.Equal(1, int.Parse(totals!.Single(), System.Globalization.CultureInfo.InvariantCulture));

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement[0];
		Assert.Equal("queued", row.GetProperty("state").GetString());
		Assert.Equal(0, row.GetProperty("retry_count").GetInt32());
	}

	/// <summary>
	/// Cancel acceptance criterion: DELETE moves the download to <c>cancelled</c> and
	/// cancels only that download's own queued job (via per-job cancel), without aborting
	/// the run.
	/// </summary>
	[Fact]
	public async Task DeleteDownload_WithAdminRole_CancelsCleanly()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);
		(string downloadId, _) = await QueueDownloadAsync(artifact);

		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/downloads/{downloadId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("cancelled", document.RootElement.GetProperty("state").GetString());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// The download's own job is cancelled...
		await using (NpgsqlCommand jobState = new("SELECT state FROM jobs WHERE id = (SELECT job_id FROM downloads WHERE id = $1)", connection))
		{
			jobState.Parameters.AddWithValue(Guid.Parse(downloadId));
			Assert.Equal("cancelled", (string)(await jobState.ExecuteScalarAsync())!);
		}

		// ...and the run itself is NOT aborted (per-job cancel, not run abort).
		await using (NpgsqlCommand runState = new("SELECT state FROM runs WHERE id = (SELECT run_id FROM downloads WHERE id = $1)", connection))
		{
			runState.Parameters.AddWithValue(Guid.Parse(downloadId));
			Assert.NotEqual("aborted", (string)(await runState.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Per-job cancel isolation: DELETE on one download in a multi-job run cancels only
	/// that download's job and leaves its sibling queued (the in-run Continue policy /
	/// per-job cancel guarantee, not run-isolation).
	/// </summary>
	[Fact]
	public async Task DeleteDownload_InMultiJobRun_LeavesSiblingQueued()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact1 = await SeedArtifactAsync($"{tag}-a");
		Guid artifact2 = await SeedArtifactAsync($"{tag}-b");

		HttpRequestMessage queue = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact1.ToString(), artifact2.ToString() } }),
		};
		queue.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage queued = await _client.SendAsync(queue);
		using JsonDocument queuedDoc = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
		string[] downloadIds = queuedDoc.RootElement.GetProperty("download_ids").EnumerateArray().Select(e => e.GetString()!).ToArray();
		string runId = queuedDoc.RootElement.GetProperty("run_id").GetString()!;

		HttpRequestMessage cancel = new(HttpMethod.Delete, $"/api/v1/downloads/{downloadIds[0]}");
		cancel.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage cancelResponse = await _client.SendAsync(cancel);
		Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// Cancelled one job; the sibling is still queued in the same, non-aborted run.
		await using (NpgsqlCommand cancelledCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND state = 'cancelled'", connection))
		{
			cancelledCount.Parameters.AddWithValue(Guid.Parse(runId));
			Assert.Equal(1L, (long)(await cancelledCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand queuedCount = new(
			"SELECT count(*) FROM jobs WHERE run_id = $1 AND state = 'queued'", connection))
		{
			queuedCount.Parameters.AddWithValue(Guid.Parse(runId));
			Assert.Equal(1L, (long)(await queuedCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand runStillActive = new("SELECT state FROM runs WHERE id = $1", connection))
		{
			runStillActive.Parameters.AddWithValue(Guid.Parse(runId));
			Assert.NotEqual("aborted", (string)(await runStillActive.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Issue #234: DELETE on a download whose job is already running no longer just
	/// discards the result at completion -- it sets the job's per-job cancel_requested
	/// flag (the signal <c>JobDispatcherHostedService</c>'s heartbeat loop observes to
	/// stop the handler cooperatively). No dispatcher runs in this test host, so this
	/// proves the request-side contract; the actual mid-run stop is
	/// <c>JobDispatcherHostedServiceTests.CancelRequested_ObservedMidRun_StopsPromptlyAndReleasesLease</c>.
	/// </summary>
	[Fact]
	public async Task DeleteDownload_JobAlreadyRunning_RequestsCancelRatherThanDiscardingSilently()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag);
		(string downloadId, _) = await QueueDownloadAsync(artifact);

		Guid jobId;
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand getJobId = new("SELECT job_id FROM downloads WHERE id = $1", connection);
			getJobId.Parameters.AddWithValue(Guid.Parse(downloadId));
			jobId = (Guid)(await getJobId.ExecuteScalarAsync())!;

			await using NpgsqlCommand claim = new(
				"UPDATE jobs SET state = 'running', claimed_by = 'test-worker', claimed_at = now(), lease_expires_at = now() + interval '5 minutes' WHERE id = $1",
				connection);
			claim.Parameters.AddWithValue(jobId);
			await claim.ExecuteNonQueryAsync();
		}

		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/downloads/{downloadId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("cancelled", document.RootElement.GetProperty("state").GetString());

		await using NpgsqlConnection assertConnection = new(_fixture.ConnectionString);
		await assertConnection.OpenAsync();

		// The job itself is left running (only the dispatcher's heartbeat loop moves it) --
		// but cancel_requested is now set, which is the actual per-job stop signal.
		await using (NpgsqlCommand jobState = new("SELECT state, cancel_requested FROM jobs WHERE id = $1", assertConnection))
		{
			jobState.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await jobState.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.Equal("running", reader.GetString(0));
			Assert.True(reader.GetBoolean(1));
		}
	}

	[Fact]
	public async Task DeleteDownload_UnknownId_Returns404()
	{
		HttpRequestMessage request = new(HttpMethod.Delete, $"/api/v1/downloads/{Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>
	/// Issue #560/#690: no Activation Code credential configured and no
	/// download-runner has ever heartbeated -- both prerequisites report missing,
	/// tool_installed stays null (unknown, not "false") because nothing has weighed
	/// in. The legacy Download Token is reported as unconfigured too, but never
	/// contributes a missing_prerequisites entry (issue #690 AC: it never gates
	/// readiness).
	/// </summary>
	[Fact]
	public async Task GetReadiness_NothingConfigured_ReportsBothPrerequisitesMissing_ToolUnknown()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads/readiness");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.False(root.GetProperty("ready").GetBoolean());
		Assert.False(root.GetProperty("activation_code_configured").GetBoolean());
		Assert.False(root.GetProperty("legacy_download_token_configured").GetBoolean());
		// WaypointJsonOptions.Default (WhenWritingNull) omits a null optional field
		// entirely rather than emitting a JSON null -- same convention rotated_at/
		// username already use -- so "unknown" reads as the property being absent.
		Assert.False(root.TryGetProperty("activation_code_health", out _));
		Assert.False(root.TryGetProperty("legacy_download_token_health", out _));
		Assert.False(root.TryGetProperty("tool_installed", out _));
		string[] missing = root.GetProperty("missing_prerequisites").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Contains("activation_code", missing);
		Assert.Contains("tool_not_installed", missing);
	}

	/// <summary>
	/// A valid Activation Code credential plus a download-runner heartbeat reporting
	/// the tool present combine into ready:true and an empty missing-prerequisites
	/// list -- with no legacy Download Token configured at all.
	/// </summary>
	[Fact]
	public async Task GetReadiness_ActivationCodeValidAndToolPresent_ReportsReady()
	{
		Guid credentialId = await SeedDepotCredentialAsync("depot-activation-code");
		await MarkCredentialHealthAsync(credentialId, "valid");
		await SeedDownloadRunnerHeartbeatAsync(toolPresent: true);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads/readiness");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;

		Assert.True(root.GetProperty("ready").GetBoolean());
		Assert.True(root.GetProperty("activation_code_configured").GetBoolean());
		Assert.Equal("valid", root.GetProperty("activation_code_health").GetString());
		Assert.False(root.GetProperty("legacy_download_token_configured").GetBoolean());
		Assert.True(root.GetProperty("tool_installed").GetBoolean());
		Assert.Empty(root.GetProperty("missing_prerequisites").EnumerateArray());
	}

	/// <summary>An auth-failing Activation Code is reported as its own distinct missing prerequisite, not conflated with "not configured".</summary>
	[Fact]
	public async Task GetReadiness_ActivationCodeAuthFailing_ReportsDistinctFromMissing()
	{
		Guid credentialId = await SeedDepotCredentialAsync("depot-activation-code");
		await MarkCredentialHealthAsync(credentialId, "auth_failing");
		await SeedDownloadRunnerHeartbeatAsync(toolPresent: true);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads/readiness");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;

		Assert.False(root.GetProperty("ready").GetBoolean());
		Assert.True(root.GetProperty("activation_code_configured").GetBoolean());
		string[] missing = root.GetProperty("missing_prerequisites").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Contains("activation_code_auth_failing", missing);
		Assert.DoesNotContain("activation_code", missing);
		Assert.DoesNotContain("tool_not_installed", missing);
	}

	/// <summary>
	/// Issue #690 AC: a healthy legacy Download Token alone (no Activation Code
	/// configured) is reported for visibility but never satisfies readiness or
	/// removes the "activation_code" missing-prerequisite entry -- the two
	/// credentials are never treated as interchangeable.
	/// </summary>
	[Fact]
	public async Task GetReadiness_OnlyLegacyTokenConfigured_NeverGatesReadiness()
	{
		Guid legacyId = await SeedDepotCredentialAsync("legacy-download-token");
		await MarkCredentialHealthAsync(legacyId, "valid");
		await SeedDownloadRunnerHeartbeatAsync(toolPresent: true);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads/readiness");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;

		Assert.False(root.GetProperty("ready").GetBoolean());
		Assert.False(root.GetProperty("activation_code_configured").GetBoolean());
		Assert.True(root.GetProperty("legacy_download_token_configured").GetBoolean());
		Assert.Equal("valid", root.GetProperty("legacy_download_token_health").GetString());
		string[] missing = root.GetProperty("missing_prerequisites").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Contains("activation_code", missing);
	}

	[Fact]
	public async Task GetReadiness_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/downloads/readiness");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private async Task<Guid> SeedDepotCredentialAsync(string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue($"{credentialType}-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);
		Guid id = (Guid)(await insert.ExecuteScalarAsync())!;

		// has_secret is derived from credential_secrets, not a credentials column --
		// seed a real (invented) ciphertext row so CredentialResponse.HasSecret is
		// true, same as CredentialsController.Create's normal path would leave it.
		await using NpgsqlCommand secret = new(
			"""
			INSERT INTO credential_secrets (credential_id, ciphertext, data_key_wrapped, master_key_id)
			VALUES ($1, $2, $3, 'invented-test-key-id')
			""", connection);
		secret.Parameters.AddWithValue(id);
		secret.Parameters.AddWithValue(Encoding.UTF8.GetBytes("invented-ciphertext-not-real"));
		secret.Parameters.AddWithValue(Encoding.UTF8.GetBytes("invented-wrapped-key-not-real"));
		await secret.ExecuteNonQueryAsync();

		return id;
	}

	private async Task MarkCredentialHealthAsync(Guid credentialId, string health)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand update = new("UPDATE credentials SET health = $2 WHERE id = $1", connection);
		update.Parameters.AddWithValue(credentialId);
		update.Parameters.AddWithValue(health);
		await update.ExecuteNonQueryAsync();
	}

	private async Task SeedDownloadRunnerHeartbeatAsync(bool toolPresent)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO worker_registry (worker_id, job_types, ready, starved_job_types, tool_present)
			VALUES ($1, '["download","catalog-index"]'::jsonb, true, '[]'::jsonb, $2)
			ON CONFLICT (worker_id) DO UPDATE SET tool_present = EXCLUDED.tool_present
			""", connection);
		insert.Parameters.AddWithValue($"download-runner-readiness-test");
		insert.Parameters.AddWithValue(toolPresent);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedArtifactAsync(string externalIdTag)
	{
		return await _artifacts.UpsertAsync(
			new DepotArtifactUpsert(externalIdTag, "0000000000000000000000000000000000000000000000000000000000000000", "indexed", "{}"),
			CancellationToken.None);
	}

	/// <summary>Seeds an artifact whose <c>product</c>/<c>version</c> generated columns (migration 0007) are populated, for the release-selector fanout tests.</summary>
	private async Task<Guid> SeedArtifactWithProductVersionAsync(string externalIdTag, string product, string version)
	{
		string metadata = JsonSerializer.Serialize(new { product, version });
		return await _artifacts.UpsertAsync(
			new DepotArtifactUpsert(externalIdTag, "0000000000000000000000000000000000000000000000000000000000000000", "indexed", metadata),
			CancellationToken.None);
	}

	private async Task<(string DownloadId, string RunId)> QueueDownloadAsync(Guid artifactId)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifactId.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string downloadId = document.RootElement.GetProperty("download_ids")[0].GetString()!;
		string runId = document.RootElement.GetProperty("run_id").GetString()!;
		return (downloadId, runId);
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
