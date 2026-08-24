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
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #585 (epic #582, ADR-0021 §§4-6), end to end against real Postgres:
/// <c>POST /api/v1/runs</c> for a scan run resolves each selected target's required
/// credential purposes from its <c>target_credential_bindings</c> (plus validated
/// per-target/per-purpose <c>credential_overrides</c>) and snapshots them as immutable
/// <c>job_credential_bindings</c> rows -- or rejects the whole plan with one
/// <c>credential_binding_gaps</c> 400 enumerating every (target, purpose, reason)
/// before any run/job row exists. Execution-side consumption of the snapshot is
/// covered by <see cref="ScanJobHandlerEndToEndTests"/>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class CredentialBindingResolutionTests : IAsyncLifetime
{
	private sealed class BindingResolutionApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public BindingResolutionApiFactory(string connectionString)
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
				services.AddSingleton(new TargetCredentialBindingRepository(_connectionString));
				services.AddSingleton(new Waypoint.Infrastructure.Secrets.CredentialRepository(_connectionString));
				services.AddSingleton<IProfileRepository>(new ProfileRepository(_connectionString));

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
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private BindingResolutionApiFactory _factory = null!;
	private HttpClient _client = null!;
	private Guid _profileId;

	public CredentialBindingResolutionTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new BindingResolutionApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();

		ProfileRepository profiles = new(_fixture.ConnectionString);
		await profiles.ReplaceAllAsync(
			[new ProfileUpsert("binding-resolution-profile", "Binding Resolution Test Profile", "1.0.0", "invented-commit-bindings", ProfileStates.Current)],
			CancellationToken.None);
		_profileId = (await profiles.ListAsync(CancellationToken.None)).Single().Id;
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

#pragma warning restore CA1001

	[Fact]
	public async Task CreateScanRun_NoOverrides_SnapshotsEachTargetsOwnBindings()
	{
		Guid siteId = await CreateSiteAsync("resolution-defaults");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		Guid nsxCred = await SeedCredentialAsync("nsx", "svc-nsx@example.internal");
		Guid sshCred = await SeedCredentialAsync("ssh", "svc-srg@example.internal");
		Guid vsphereTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		Guid nsxTarget = await CreateTargetAsync(siteId, "nsx-api", "nsx-01", """{"host":"nsx-01.example.internal"}""");
		Guid sshTarget = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");
		await SeedBindingAsync(vsphereTarget, "vsphere-api", vcenterCred);
		await SeedBindingAsync(nsxTarget, "nsx-api", nsxCred);
		await SeedBindingAsync(sshTarget, "srg-ssh", sshCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// Each scan job's snapshot names its OWN target's binding -- no cross-target
		// sharing, and jobs.credential_id (the execution-purpose mirror) agrees with the
		// snapshot row for every job.
		Assert.Equal((vcenterCred, vcenterCred), await ReadScanJobBindingAsync(runId, vsphereTarget, "vsphere-api"));
		Assert.Equal((nsxCred, nsxCred), await ReadScanJobBindingAsync(runId, nsxTarget, "nsx-api"));
		Assert.Equal((sshCred, sshCred), await ReadScanJobBindingAsync(runId, sshTarget, "srg-ssh"));
	}

	[Fact]
	public async Task CreateScanRun_VsphereTargetWithVcsaSshBinding_SnapshotsBothPurposes()
	{
		Guid siteId = await CreateSiteAsync("resolution-vcsa-dual");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-vsphere@example.internal");
		Guid vcsaSshCred = await SeedCredentialAsync("ssh", "root");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		await SeedBindingAsync(targetId, "vcsa-ssh", vcsaSshCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { targetId }, profile_id = _profileId });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		IReadOnlyDictionary<string, Guid?> snapshot = await ReadScanJobSnapshotAsync(runId, targetId);
		Assert.Equal(2, snapshot.Count);
		Assert.Equal(vcenterCred, snapshot["vsphere-api"]);
		Assert.Equal(vcsaSshCred, snapshot["vcsa-ssh"]);

		// jobs.credential_id mirrors the EXECUTION purpose (vsphere-api), never the
		// component-conditional vcsa-ssh.
		(Guid? jobCredential, _) = await ReadScanJobBindingAsync(runId, targetId, "vsphere-api");
		Assert.Equal(vcenterCred, jobCredential);
	}

	[Fact]
	public async Task CreateScanRun_Override_AppliesOnlyToNamedTargetAndPurpose()
	{
		Guid siteId = await CreateSiteAsync("resolution-override-scope");
		Guid boundCredA = await SeedCredentialAsync("vcenter", "svc-a@example.internal");
		Guid boundCredB = await SeedCredentialAsync("vcenter", "svc-b@example.internal");
		Guid overrideCred = await SeedCredentialAsync("vcenter", "override@example.internal");
		Guid targetA = await CreateTargetAsync(siteId, "vsphere", "vcsa-a", """{"host":"vcsa-01.example.internal"}""");
		Guid targetB = await CreateTargetAsync(siteId, "vsphere", "vcsa-b", """{"host":"vcsa-02.example.internal"}""");
		await SeedBindingAsync(targetA, "vsphere-api", boundCredA);
		await SeedBindingAsync(targetB, "vsphere-api", boundCredB);

		HttpResponseMessage response = await PostRunAsync(
			new { site_id = siteId, profile_id = _profileId },
			credentialOverrides: [new { target_id = targetA, purpose = "vsphere-api", credential_id = overrideCred }]);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// The override lands on exactly (targetA, vsphere-api); targetB keeps its own
		// binding -- no cross-job leak (issue #585 AC).
		Assert.Equal((overrideCred, overrideCred), await ReadScanJobBindingAsync(runId, targetA, "vsphere-api"));
		Assert.Equal((boundCredB, boundCredB), await ReadScanJobBindingAsync(runId, targetB, "vsphere-api"));
	}

	[Fact]
	public async Task CreateScanRun_MissingRequiredBinding_Returns400NamingTargetAndPurpose_AndCreatesNothing()
	{
		Guid siteId = await CreateSiteAsync("resolution-missing");
		Guid boundCred = await SeedCredentialAsync("vcenter", "svc-a@example.internal");
		Guid boundTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-ok", """{"host":"vcsa-01.example.internal"}""");
		Guid unboundTarget = await CreateTargetAsync(siteId, "ssh", "photon-unbound", """{"host":"photon-01.example.internal"}""");
		await SeedBindingAsync(boundTarget, "vsphere-api", boundCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, profile_id = _profileId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement error = body.RootElement.GetProperty("error");
		Assert.Equal("credential_binding_gaps", error.GetProperty("code").GetString());
		JsonElement[] gaps = error.GetProperty("binding_gaps").EnumerateArray().ToArray();
		JsonElement gap = Assert.Single(gaps);
		Assert.Equal(unboundTarget, gap.GetProperty("target_id").GetGuid());
		Assert.StartsWith("photon-unbound", gap.GetProperty("target_name").GetString(), StringComparison.Ordinal);
		Assert.Equal("srg-ssh", gap.GetProperty("purpose").GetString());
		Assert.Equal("missing_binding", gap.GetProperty("reason").GetString());

		// Atomic: the rejection happened before ANY run/job row -- the fully-bound
		// sibling target got nothing either.
		Assert.Equal(0, await CountAsync("SELECT count(*) FROM runs"));
		Assert.Equal(0, await CountAsync("SELECT count(*) FROM jobs"));
	}

	[Fact]
	public async Task CreateScanRun_InvalidOverrides_EnumerateEveryGapInOneResponse()
	{
		Guid siteId = await CreateSiteAsync("resolution-override-gaps");
		Guid vcenterCred = await SeedCredentialAsync("vcenter", "svc-a@example.internal");
		Guid sshCred = await SeedCredentialAsync("ssh", "root");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		await SeedBindingAsync(targetId, "vsphere-api", vcenterCred);
		Guid strangerTarget = Guid.NewGuid();
		Guid unknownCredential = Guid.NewGuid();

		HttpResponseMessage response = await PostRunAsync(
			new { site_id = siteId, profile_id = _profileId },
			credentialOverrides:
			[
				new { target_id = strangerTarget, purpose = "vsphere-api", credential_id = vcenterCred },
				new { target_id = targetId, purpose = "nsx-api", credential_id = vcenterCred },
				new { target_id = targetId, purpose = "vsphere-api", credential_id = sshCred },
				new { target_id = targetId, purpose = "vcsa-ssh", credential_id = unknownCredential },
				new { target_id = targetId, purpose = "vcsa-ssh", credential_id = sshCred },
			]);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement error = body.RootElement.GetProperty("error");
		Assert.Equal("credential_binding_gaps", error.GetProperty("code").GetString());
		string[] reasons = error.GetProperty("binding_gaps").EnumerateArray()
			.Select(gap => gap.GetProperty("reason").GetString()!)
			.OrderBy(reason => reason, StringComparer.Ordinal)
			.ToArray();
		// The fourth override FAILS (unknown credential) without occupying the
		// (target, vcsa-ssh) slot, so the fifth -- a valid vcsa-ssh substitute -- is
		// accepted; the four invalid entries all surface together in one response
		// (issue #585: enumerate every gap, never first-failure-only).
		Assert.Equal(
			["credential_not_found", "incompatible_credential_type", "purpose_not_applicable", "target_not_in_scope"],
			reasons);
	}

	[Fact]
	public async Task CreateScanRun_RunLevelCredential_IsDefaultPurposeOverride_TypeCheckedPerKind()
	{
		Guid siteId = await CreateSiteAsync("resolution-legacy-run-credential");
		Guid boundVcenter = await SeedCredentialAsync("vcenter", "svc-a@example.internal");
		Guid runVcenter = await SeedCredentialAsync("vcenter", "run-level@example.internal");
		Guid sshCred = await SeedCredentialAsync("ssh", "root");
		Guid vsphereTarget = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		Guid sshTarget = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");
		await SeedBindingAsync(vsphereTarget, "vsphere-api", boundVcenter);
		await SeedBindingAsync(sshTarget, "srg-ssh", sshCred);

		// Mixed-kind site + a vcenter-type run-level credential: pre-#585 this silently
		// copied the vcenter credential onto the ssh target's job (broken at execution);
		// now the incompatible pair is a named gap at creation.
		HttpResponseMessage rejected = await PostRunAsync(new { site_id = siteId, profile_id = _profileId }, credentialId: runVcenter);
		Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
		using (JsonDocument body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
		{
			JsonElement gap = Assert.Single(body.RootElement.GetProperty("error").GetProperty("binding_gaps").EnumerateArray().ToArray());
			Assert.Equal(sshTarget, gap.GetProperty("target_id").GetGuid());
			Assert.Equal("srg-ssh", gap.GetProperty("purpose").GetString());
			Assert.Equal("incompatible_credential_type", gap.GetProperty("reason").GetString());
			Assert.Equal(runVcenter, gap.GetProperty("credential_id").GetGuid());
		}

		// Scoped to only the compatible kind, the legacy semantics hold: the run-level
		// credential overrides the target's own binding for its default purpose.
		HttpResponseMessage accepted = await PostRunAsync(
			new { site_id = siteId, target_ids = new[] { vsphereTarget }, profile_id = _profileId }, credentialId: runVcenter);
		Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
		Guid runId = await ReadRunIdAsync(accepted);
		Assert.Equal((runVcenter, runVcenter), await ReadScanJobBindingAsync(runId, vsphereTarget, "vsphere-api"));
	}

	[Fact]
	public async Task EditTargetBindingAfterRunCreation_InFlightJobSnapshotUnchanged()
	{
		Guid siteId = await CreateSiteAsync("resolution-immutability");
		Guid originalCred = await SeedCredentialAsync("vcenter", "original@example.internal");
		Guid rotatedCred = await SeedCredentialAsync("vcenter", "rotated@example.internal");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		await SeedBindingAsync(targetId, "vsphere-api", originalCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { targetId }, profile_id = _profileId });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// Re-point the target's binding AFTER run creation -- ADR-0021 §5: a later
		// target edit never changes an in-flight run.
		TargetCredentialBindingRepository bindings = new(_fixture.ConnectionString);
		Waypoint.Core.Sites.TargetCredentialBindingWriteOutcome outcome =
			await bindings.SetAsync(targetId, "vsphere-api", rotatedCred, CancellationToken.None);
		Assert.Equal(Waypoint.Core.Sites.TargetCredentialBindingWriteOutcome.Ok, outcome);

		Assert.Equal((originalCred, originalCred), await ReadScanJobBindingAsync(runId, targetId, "vsphere-api"));
	}

	[Fact]
	public async Task LeaseRecovery_RetriedJobKeepsItsOriginalSnapshot()
	{
		Guid siteId = await CreateSiteAsync("resolution-recovery");
		Guid originalCred = await SeedCredentialAsync("vcenter", "original@example.internal");
		Guid rotatedCred = await SeedCredentialAsync("vcenter", "rotated@example.internal");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");
		await SeedBindingAsync(targetId, "vsphere-api", originalCred);

		HttpResponseMessage response = await PostRunAsync(new { site_id = siteId, target_ids = new[] { targetId }, profile_id = _profileId });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Guid runId = await ReadRunIdAsync(response);

		// Claim the scan job, expire its lease in place, edit the target's binding,
		// then recover -- the requeued job must still carry its ORIGINAL snapshot:
		// recovery re-queues the same row (RecoverSql never touches credential state),
		// it never re-resolves from the target.
		JobQueueRepository repository = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobQueueRepository>.Instance);
		ClaimedJob? claimed = await repository.ClaimJobAsync("recovery-worker", TimeSpan.FromMilliseconds(1), new HashSet<string> { "scan" }, CancellationToken.None);
		Assert.NotNull(claimed);
		await ExecuteAsync("UPDATE jobs SET lease_expires_at = now() - interval '1 minute' WHERE id = $1", claimed!.Id);

		TargetCredentialBindingRepository bindings = new(_fixture.ConnectionString);
		await bindings.SetAsync(targetId, "vsphere-api", rotatedCred, CancellationToken.None);

		IReadOnlyList<RecoveredJob> recovered = await repository.RecoverExpiredLeasesAsync(10, CancellationToken.None);
		Assert.Contains(recovered, job => job.Id == claimed.Id);

		IReadOnlyList<JobCredentialBinding> snapshot = await repository.GetJobCredentialBindingsAsync(claimed.Id, CancellationToken.None);
		JobCredentialBinding binding = Assert.Single(snapshot);
		Assert.Equal(originalCred, binding.CredentialId);
	}

	private async Task<HttpResponseMessage> PostRunAsync(object scopeBody, Guid? credentialId = null, object[]? credentialOverrides = null)
	{
		Dictionary<string, object?> body = new(StringComparer.Ordinal)
		{
			["run_type"] = "scan",
			["scope"] = JsonSerializer.Serialize(scopeBody),
		};
		if (credentialId is not null)
		{
			body["credential_id"] = credentialId;
		}

		if (credentialOverrides is not null)
		{
			body["credential_overrides"] = credentialOverrides;
		}

		return await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber", body);
	}

	private static async Task<Guid> ReadRunIdAsync(HttpResponseMessage response)
	{
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return Guid.Parse(created.RootElement.GetProperty("run_id").GetString()!);
	}

	/// <summary>The scan job's (jobs.credential_id, snapshot row credential_id) pair for one (run, target, purpose).</summary>
	private async Task<(Guid? JobCredentialId, Guid? BindingCredentialId)> ReadScanJobBindingAsync(Guid runId, Guid targetId, string purpose)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			SELECT j.credential_id, b.credential_id
			FROM jobs j
			LEFT JOIN job_credential_bindings b ON b.job_id = j.id AND b.purpose = $3
			WHERE j.run_id = $1 AND j.target_id = $2 AND j.job_type = 'scan'
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(targetId);
		command.Parameters.AddWithValue(purpose);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync(), $"no scan job found for run {runId} target {targetId}");
		return (
			reader.IsDBNull(0) ? null : reader.GetGuid(0),
			reader.IsDBNull(1) ? null : reader.GetGuid(1));
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

	private async Task<Guid> CreateSiteAsync(string namePrefix)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/sites", "Admin",
			new Dictionary<string, object?> { ["name"] = $"{namePrefix}-{Guid.NewGuid():N}" });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> CreateTargetAsync(Guid siteId, string kind, string name, string connectionJson)
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
		command.Parameters.AddWithValue(connectionJson);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedCredentialAsync(string credentialType, string username)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO credentials (name, credential_type, username) VALUES ($1, $2, $3) RETURNING id", connection);
		command.Parameters.AddWithValue($"binding-resolution-{credentialType}-{Guid.NewGuid():N}");
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

	private async Task<long> CountAsync(string sql)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(sql, connection);
		return (long)(await command.ExecuteScalarAsync())!;
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
			"TRUNCATE TABLE jobs, runs, targets, sites, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
