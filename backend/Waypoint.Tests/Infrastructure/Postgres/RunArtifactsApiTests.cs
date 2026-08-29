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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #299 end to end against real Postgres and a real (temp-directory) artifact
/// store: <c>GET /runs/{id}/artifacts</c>, <c>GET /jobs/{id}/artifacts/{kind}</c>, and
/// <c>GET /runs/{id}/attestations-applied</c>. Artifact files are seeded directly on disk
/// under the test's <see cref="ScanOptions.ArtifactStorePath"/> override rather than run
/// through the full InSpec/SAF PowerShell pipeline (<c>ScanJobHandlerEndToEndTests</c>
/// already proves that pipeline writes files at exactly this same naming convention) --
/// this class's own focus is the REST surface reading them back, not re-proving the
/// pipeline that produces them.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory and removes the temp dir.
public sealed class RunArtifactsApiTests : IAsyncLifetime, IDisposable
{
	private sealed class ArtifactsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _artifactStorePath;

		public ArtifactsApiFactory(string connectionString, string artifactStorePath)
		{
			_connectionString = connectionString;
			_artifactStorePath = artifactStorePath;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			builder.ConfigureAppConfiguration((_, configBuilder) =>
			{
				configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Scans:ArtifactStorePath"] = _artifactStorePath,
					["Scans:AttestationProfile"] = "invented-vsphere-stig",
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

				services.AddSingleton(new SiteRepository(_connectionString));
				services.AddSingleton(new TargetRepository(_connectionString));
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));
				services.AddSingleton(new ConfigDocRepository(_connectionString));
				services.AddSingleton(new AttestationSnapshotRepository(_connectionString));

				// Issue #415: swap both focused-interface registrations so every
				// consumer resolved through this host (control-plane and runner alike)
				// sees the same real JobQueueRepository against the test database.
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
	private readonly string _artifactStorePath = Directory.CreateTempSubdirectory("wp-artifacts-api").FullName;
	private ArtifactsApiFactory _factory = null!;
	private HttpClient _client = null!;

	public RunArtifactsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new ArtifactsApiFactory(_fixture.ConnectionString, _artifactStorePath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		Directory.Delete(_artifactStorePath, recursive: true);
	}

#pragma warning restore CA1001

	[Fact]
	public async Task GetArtifacts_ReturnsRowPerScanJob_WithKindsAndCounts()
	{
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync("artifacts-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");

		WriteHdf(jobId, catI: 2, catII: 1, catIII: 0);
		WriteCkl(jobId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/artifacts", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.Equal(jobId.ToString(), row.GetProperty("job_id").GetString());
		Assert.True(row.GetProperty("counts_available").GetBoolean());
		Assert.Equal(2, row.GetProperty("cat_i_open").GetInt32());
		Assert.Equal(1, row.GetProperty("cat_ii_open").GetInt32());
		Assert.Equal(0, row.GetProperty("cat_iii_open").GetInt32());
		// Issue #1132: every WriteHdf-generated control is "failed" (open), so all 3 are evaluated.
		Assert.Equal(3, row.GetProperty("controls_total").GetInt32());
		Assert.Equal(3, row.GetProperty("controls_evaluated").GetInt32());
		string[] kinds = row.GetProperty("artifact_kinds").EnumerateArray().Select(k => k.GetString()!).ToArray();
		Assert.Contains("hdf", kinds);
		Assert.Contains("ckl", kinds);
		Assert.Equal("uploaded", row.GetProperty("upload_status").GetString());
		_ = siteId;
	}

	[Fact]
	public async Task GetArtifacts_AllControlsSkipped_ReportsZeroEvaluatedDenominatorNotClean()
	{
		// Issue #1132: the round-12 shape -- every control skipped because the target
		// could not be reached. CAT counts alone (all zero) render identically to a
		// genuinely clean scan; the evaluated denominator must expose the difference.
		(_, Guid targetId) = await CreateSiteAndTargetAsync("all-skipped-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");

		WriteAllSkippedHdf(jobId, controlCount: 69);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/artifacts", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.True(row.GetProperty("counts_available").GetBoolean());
		Assert.Equal(0, row.GetProperty("cat_i_open").GetInt32());
		Assert.Equal(0, row.GetProperty("cat_ii_open").GetInt32());
		Assert.Equal(0, row.GetProperty("cat_iii_open").GetInt32());
		Assert.Equal(69, row.GetProperty("controls_total").GetInt32());
		Assert.Equal(0, row.GetProperty("controls_evaluated").GetInt32());
	}

	[Fact]
	public async Task GetArtifacts_JobWithNoArtifactsYet_ReportsUncountableAndNoKinds()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("no-artifacts-target");
		Guid runId = await CreateRunAsync();
		await FanOutScanJobAsync(runId, targetId, state: "running");

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/artifacts", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		// No HDF on disk yet -> uncountable, NOT a fabricated zero. Null counts are omitted
		// entirely (WaypointJsonOptions: WhenWritingNull); counts_available is the signal.
		Assert.False(row.GetProperty("counts_available").GetBoolean());
		Assert.False(row.TryGetProperty("cat_i_open", out _));
		Assert.False(row.TryGetProperty("cat_ii_open", out _));
		Assert.False(row.TryGetProperty("cat_iii_open", out _));
		Assert.False(row.TryGetProperty("controls_total", out _));
		Assert.False(row.TryGetProperty("controls_evaluated", out _));
		Assert.Empty(row.GetProperty("artifact_kinds").EnumerateArray());
		Assert.Equal("pending", row.GetProperty("upload_status").GetString());
	}

	[Fact]
	public async Task GetArtifacts_PresentButCorruptHdf_ReportsUncountableNotZero()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("corrupt-hdf-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");

		// A present HDF whose bytes are not valid HDF JSON: the count must come back
		// "uncountable" (counts_available:false, null counts), never a clean-looking zero
		// (issue #299 round-1 blocker -- corrupt must not read as compliant).
		WriteCorruptHdf(jobId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/artifacts", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.False(row.GetProperty("counts_available").GetBoolean());
		Assert.False(row.TryGetProperty("cat_i_open", out _));
		Assert.False(row.TryGetProperty("cat_ii_open", out _));
		Assert.False(row.TryGetProperty("cat_iii_open", out _));
		// The HDF file IS present on disk, so the download route still lists it.
		string[] kinds = row.GetProperty("artifact_kinds").EnumerateArray().Select(k => k.GetString()!).ToArray();
		Assert.Contains("hdf", kinds);
	}

	[Fact]
	public async Task GetArtifacts_FailedJob_ReportsNotUploaded()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("failed-target");
		Guid runId = await CreateRunAsync();
		await FanOutScanJobAsync(runId, targetId, state: "failed");

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/artifacts", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.Equal("not-uploaded", row.GetProperty("upload_status").GetString());
	}

	[Fact]
	public async Task GetArtifacts_UnknownRun_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/artifacts", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetJobArtifact_Hdf_StreamsBytesWithJsonContentType()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("download-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		WriteHdf(jobId, catI: 0, catII: 0, catIII: 0);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/artifacts/hdf", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("invented-stub-profile", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetJobArtifact_Ckl_StreamsBytesWithXmlContentType()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("ckl-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		WriteCkl(jobId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/artifacts/ckl", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("<CHECKLIST", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetJobArtifact_UnknownJob_Returns404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{Guid.NewGuid()}/artifacts/hdf", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetJobArtifact_JobExistsButKindNotProducedYet_Returns404()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("missing-kind-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "attesting");
		// No HDF/CKL written for this job.

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{jobId}/artifacts/ckl", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task GetJobArtifact_UnknownKind_Returns400_NeverTouchesFilesystem()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("bad-kind-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");

		// Path-traversal-shaped kind: proves the value is validated against the closed
		// set and never interpolated into a filesystem path (it would 404 or worse if it
		// reached the filesystem layer as a raw segment; the route/validation rejects it
		// as 400 before any path is built).
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/jobs/{jobId}/artifacts/{Uri.EscapeDataString("../../etc/passwd")}", "Viewer", body: null);

		Assert.True(
			response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
			$"expected 400 or 404, got {(int)response.StatusCode}");
	}

	[Fact]
	public async Task GetJobArtifact_Unauthenticated_Returns401()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("unauth-target");
		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		WriteHdf(jobId, 0, 0, 0);

		HttpResponseMessage response = await _client.GetAsync($"/api/v1/jobs/{jobId}/artifacts/hdf");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetAttestationsApplied_TargetWithAttestationDoc_ReturnsRow()
	{
		(Guid siteId, Guid targetId) = await CreateSiteAndTargetAsync("attest-target");
		string profile = "invented-vsphere-stig";
		JsonElement docResponse = await PutConfigDocAsync(
			targetId, profile, "status: Not_A_Finding\njustification: invented waiver\nexpires: 2099-01-01\n");
		Guid docId = docResponse.GetProperty("id").GetGuid();

		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		await SeedAttestationSnapshotAsync(
			runId, jobId, targetId, profile, $"target:{targetId}", docId, version: 1, author: "Admin",
			applied: true, expired: false);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/attestations-applied", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.Equal(profile, row.GetProperty("control").GetString());
		Assert.Equal($"target:{targetId}", row.GetProperty("scope").GetString());
		Assert.False(row.GetProperty("expired").GetBoolean());

		// Issue #306: the wire now carries a genuine scan-time applied_at (recorded
		// history), not a live-resolution marker -- the old derivation/resolved_at pair
		// (issue #299 round-1 blocker) is gone.
		Assert.False(row.TryGetProperty("derivation", out _), "derivation must not appear -- this is recorded history, not live resolution.");
		Assert.False(row.TryGetProperty("resolved_at", out _), "resolved_at must not appear -- see applied_at.");
		Assert.True(DateTimeOffset.TryParse(row.GetProperty("applied_at").GetString(), out _));
		Assert.True(row.TryGetProperty("attestation_updated_at", out _));
		_ = siteId;
	}

	/// <summary>
	/// The crux integrity test for issue #306: a config-doc edited AFTER the run must
	/// NOT change what this endpoint reports for that historical run. This is exactly
	/// the gap PR #305 could only disclose (`derivation: "live-resolution"`) -- proving
	/// it closed means proving the endpoint's answer survives a later edit unchanged.
	/// </summary>
	[Fact]
	public async Task GetAttestationsApplied_ConfigDocEditedAfterRun_DoesNotChangeHistoricalResult()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("post-run-edit-target");
		string profile = "invented-vsphere-stig";
		JsonElement docResponse = await PutConfigDocAsync(
			targetId, profile, "status: Not_A_Finding\njustification: original waiver\nexpires: 2099-01-01\n");
		Guid docId = docResponse.GetProperty("id").GetGuid();

		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		await SeedAttestationSnapshotAsync(
			runId, jobId, targetId, profile, $"target:{targetId}", docId, version: 1, author: "Admin",
			applied: true, expired: false);

		HttpResponseMessage before = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/attestations-applied", "Viewer", body: null);
		using JsonDocument beforeDocument = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
		JsonElement beforeRow = beforeDocument.RootElement.EnumerateArray().Single();
		string beforeJustification = beforeRow.GetProperty("justification").GetString()!;
		string beforeAppliedAt = beforeRow.GetProperty("applied_at").GetString()!;
		string? beforeUpdatedAt = beforeRow.GetProperty("attestation_updated_at").GetString();

		// Edit the SAME config-doc slot after the run -- a new version, a lapsed expiry,
		// different justification text. If the endpoint were still live-resolving (the
		// #306 bug), this would flip the historical run's row to expired:true and a
		// different justification/attestation_updated_at.
		await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{docId}", "Admin",
			new { kind = "attestation", profile, layer = $"target:{targetId}", body = "status: Not_A_Finding\njustification: EDITED AFTER THE RUN\nexpires: 2020-01-01\n" });

		HttpResponseMessage after = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/attestations-applied", "Viewer", body: null);
		using JsonDocument afterDocument = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
		JsonElement afterRow = afterDocument.RootElement.EnumerateArray().Single();

		Assert.False(afterRow.GetProperty("expired").GetBoolean(), "the post-run edit's lapsed expiry must not retroactively mark the historical run's row expired.");
		Assert.Equal(beforeJustification, afterRow.GetProperty("justification").GetString());
		Assert.Equal(beforeAppliedAt, afterRow.GetProperty("applied_at").GetString());
		Assert.Equal(beforeUpdatedAt, afterRow.GetProperty("attestation_updated_at").GetString());
	}

	[Fact]
	public async Task GetAttestationsApplied_ExpiredAtScanTime_ReportsExpiredTrue()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("expired-attest-target");
		string profile = "invented-vsphere-stig";
		JsonElement docResponse = await PutConfigDocAsync(
			targetId, profile, "status: Not_A_Finding\njustification: lapsed\nexpires: 2020-01-01\n");
		Guid docId = docResponse.GetProperty("id").GetGuid();

		Guid runId = await CreateRunAsync();
		Guid jobId = await FanOutScanJobAsync(runId, targetId, state: "uploaded");
		// Recorded as it was resolved at attest-stage time: the waiver had already
		// lapsed, so applied is false and expired is true -- exactly what
		// ScanJobHandler.ExecuteAttestStageAsync would have written.
		await SeedAttestationSnapshotAsync(
			runId, jobId, targetId, profile, $"target:{targetId}", docId, version: 1, author: "Admin",
			applied: false, expired: true);

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/attestations-applied", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement row = document.RootElement.EnumerateArray().Single();
		Assert.True(row.GetProperty("expired").GetBoolean());
	}

	[Fact]
	public async Task GetAttestationsApplied_TargetWithNoRecordedSnapshot_ContributesNoRow()
	{
		(_, Guid targetId) = await CreateSiteAndTargetAsync("no-attest-target");
		Guid runId = await CreateRunAsync();
		await FanOutScanJobAsync(runId, targetId, state: "uploaded");

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/attestations-applied", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Empty(document.RootElement.EnumerateArray());
	}

	[Fact]
	public async Task GetAttestationsApplied_UnknownRun_Returns404()
	{
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/attestations-applied", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("/artifacts")]
	[InlineData("/attestations-applied")]
	public async Task RunSubResources_Unauthenticated_Return401(string suffix)
	{
		HttpResponseMessage response = await _client.GetAsync($"/api/v1/runs/{Guid.NewGuid()}{suffix}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private void WriteHdf(Guid jobId, int catI, int catII, int catIII)
	{
		List<object> controls = [];
		for (int i = 0; i < catI; i++)
		{
			controls.Add(new { tags = new { severity = "high" }, results = new[] { new { status = "failed" } } });
		}

		for (int i = 0; i < catII; i++)
		{
			controls.Add(new { tags = new { severity = "medium" }, results = new[] { new { status = "failed" } } });
		}

		for (int i = 0; i < catIII; i++)
		{
			controls.Add(new { tags = new { severity = "low" }, results = new[] { new { status = "failed" } } });
		}

		object hdf = new
		{
			platform = new { name = "vmware_vsphere", release = "invented" },
			profiles = new[] { new { name = "invented-stub-profile", version = "0.0.0", controls = controls.ToArray() } },
		};

		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.json");
		File.WriteAllText(path, JsonSerializer.Serialize(hdf));
	}

	private void WriteAllSkippedHdf(Guid jobId, int controlCount)
	{
		// Issue #1132: an applicable control that could not execute -- every result
		// row "skipped", carrying a skip_message, same InSpec shape #1124 fixed the
		// finding-status mapping for. This helper does not model impact/genuine-NA at
		// all (HdfSeverityCounter is impact-blind by design; that distinction lives
		// only in HdfFindingsParser's persisted findings).
		List<object> controls = [];
		for (int i = 0; i < controlCount; i++)
		{
			controls.Add(new
			{
				id = $"invented-control-{i:000}",
				tags = new { severity = "medium" },
				results = new[] { new { status = "skipped", skip_message = "invented: target unreachable, skipping test." } },
			});
		}

		object hdf = new
		{
			platform = new { name = "vmware_vsphere", release = "invented" },
			profiles = new[] { new { name = "invented-stub-profile", version = "0.0.0", controls = controls.ToArray() } },
		};

		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.json");
		File.WriteAllText(path, JsonSerializer.Serialize(hdf));
	}

	private void WriteCorruptHdf(Guid jobId)
	{
		// Present at the HDF path, but not parseable as HDF JSON (truncated/garbage bytes).
		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.json");
		File.WriteAllText(path, "{ this is not valid json at all <<<");
	}

	private void WriteCkl(Guid jobId)
	{
		string path = Path.Combine(_artifactStorePath, $"{jobId:N}.ckl");
		File.WriteAllText(path, "<CHECKLIST><STIGS/></CHECKLIST>");
	}

	private async Task<(Guid SiteId, Guid TargetId)> CreateSiteAndTargetAsync(string namePrefix)
	{
		SiteRepository sites = new(_fixture.ConnectionString);
		TargetRepository targets = new(_fixture.ConnectionString);

		Guid siteId = (await sites.CreateAsync($"{namePrefix}-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;
		(Waypoint.Core.Sites.TargetWriteOutcome outcome, Guid? targetId) = await targets.CreateAsync(
			siteId, "vsphere", $"{namePrefix}-{Guid.NewGuid():N}", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);
		Assert.Equal(Waypoint.Core.Sites.TargetWriteOutcome.Ok, outcome);
		return (siteId, targetId!.Value);
	}

	private async Task<Guid> CreateRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, initiated_by) VALUES ('scan', '{}', 'tester') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Seeds a <c>scan</c> job directly at <paramref name="state"/> (bypassing the real
	/// claim/advance machinery, which this test class does not need to exercise). The
	/// active states (<c>running</c>/<c>attesting</c>/<c>converting</c>) carry a CHECK
	/// constraint requiring a non-null lease (<c>jobs_running_requires_lease_check</c> and
	/// its 0015 sibling) -- stamp an invented lease for those so the insert satisfies it,
	/// same as any other test seeding a mid-pipeline row directly.
	/// </summary>
	private async Task<Guid> FanOutScanJobAsync(Guid runId, Guid targetId, string state)
	{
		bool needsLease = state is "running" or "attesting" or "converting";
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			$"""
			INSERT INTO jobs (run_id, job_type, target_id, target_name, priority, state, payload, claimed_by, claimed_at, lease_expires_at)
			VALUES ($1, 'scan', $2, $3, 3, $4, $5::jsonb, {(needsLease ? "'test-worker'" : "NULL")}, {(needsLease ? "now()" : "NULL")}, {(needsLease ? "now() + interval '5 minutes'" : "NULL")})
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(targetId);
		insert.Parameters.AddWithValue($"target-{targetId:N}");
		insert.Parameters.AddWithValue(state);
		insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { target_id = targetId }));
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>PUTs a fresh @v1 attestation config-doc at the target layer and returns the parsed <see cref="ConfigDocResponse"/> JSON.</summary>
	private async Task<JsonElement> PutConfigDocAsync(Guid targetId, string profile, string body)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/config-docs/{Guid.NewGuid()}", "Admin",
			new { kind = "attestation", profile, layer = $"target:{targetId}", body });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.Clone();
	}

	/// <summary>
	/// Seeds an <c>attestation_snapshots</c> row directly, standing in for what
	/// <c>ScanJobHandler.ExecuteAttestStageAsync</c> (via <see cref="AttestationSnapshotRepository.RecordAsync"/>)
	/// would have written at real attest-stage execution time -- this test class seeds
	/// jobs directly (see <see cref="FanOutScanJobAsync"/>'s doc comment) rather than
	/// running the full pipeline, which <c>ScanJobHandlerEndToEndTests</c> already
	/// covers for the write path itself.
	/// </summary>
	private async Task SeedAttestationSnapshotAsync(
		Guid runId, Guid jobId, Guid targetId, string profile, string scope, Guid? docId, int? version, string? author, bool applied, bool expired)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO attestation_snapshots
			    (run_id, job_id, target_id, profile, scope, doc_id, doc_version, doc_author, doc_version_created_at, applied, expired)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
			""", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(jobId);
		insert.Parameters.AddWithValue(targetId);
		insert.Parameters.AddWithValue(profile);
		insert.Parameters.AddWithValue(scope);
		insert.Parameters.AddWithValue((object?)docId ?? DBNull.Value);
		insert.Parameters.AddWithValue((object?)version ?? DBNull.Value);
		insert.Parameters.AddWithValue((object?)author ?? DBNull.Value);
		insert.Parameters.AddWithValue(docId is null ? DBNull.Value : DateTimeOffset.UtcNow);
		insert.Parameters.AddWithValue(applied);
		insert.Parameters.AddWithValue(expired);
		await insert.ExecuteNonQueryAsync();
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
			"TRUNCATE TABLE attestation_snapshots, config_versions, config_docs, jobs, runs, targets, sites RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
