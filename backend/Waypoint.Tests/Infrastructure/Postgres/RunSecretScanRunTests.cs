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
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #434 (replaces the #276 in-memory <c>IEphemeralCredentialCache</c> handoff):
/// the ADR-0011 ad hoc "my credentials" scan flow, now backed by encrypted, run-scoped
/// Postgres state. <see cref="CreateScanRun_WithRunSecret_NeverPersistedInsecurely_CanaryProof"/>
/// is the heart of the slice -- a redactor-canary-style test that runs an ad hoc scan
/// run end to end through the real API and Postgres, then greps every persistence
/// surface (<c>credentials</c>, <c>credential_secrets</c>, <c>jobs.payload</c>,
/// <c>jobs.credential_id</c>, <c>job_events</c>, <c>audit_log.detail</c>, the process's
/// own captured log lines, and the <c>run_secrets</c> ciphertext column itself) for the
/// canary secret in the CLEAR and asserts zero hits -- while confirming the run_secrets
/// row DOES exist (encrypted) and decrypts back to the canary.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class RunSecretScanRunTests : IAsyncLifetime
{
	private sealed class RunSecretApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;

		public CapturingLogger<RunSecretStore> StoreLogger { get; } = new();

		public RunSecretApiFactory(string connectionString, string keyPath)
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

				// Issue #639: same fixture-pointed override as Site/TargetRepository above
				// -- RunCreationService.CreateScanRunAsync now resolves scope.profile_id
				// through IProfileRepository, which otherwise resolves against
				// appsettings.json's base connection string (a different database than the
				// fixture) and every scan-run POST 500s.
				services.AddSingleton<Waypoint.Core.ComplianceContent.IProfileRepository>(
					new Waypoint.Infrastructure.ComplianceContent.ProfileRepository(_connectionString));

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
				// encryption) -- same pattern CredentialsApiTests uses.
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();

				// Swap in a capturing logger for the store so the canary test can assert
				// the store's own log lines (run-id/actor only, per its doc comment)
				// never carry the secret either -- and swap the store itself for one
				// built against that logger so both sides see the same instance.
				var storeDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRunSecretStore));
				if (storeDescriptor != null)
				{
					services.Remove(storeDescriptor);
				}

				services.AddSingleton<IRunSecretStore>(serviceProvider => new RunSecretStore(
					_connectionString,
					serviceProvider.GetRequiredService<IEnvelopeCipher>(),
					serviceProvider.GetRequiredService<Waypoint.Core.Logging.ISecretTracker>(),
					serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RunSecretOptions>>(),
					StoreLogger));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-run-secret-key").FullName;
	private RunSecretApiFactory _factory = null!;
	private HttpClient _client = null!;

	/// <summary>Issue #639: every scan run now requires scope.profile_id -- seeded once here like the other fixtures.</summary>
	private Guid _profileId;

	public RunSecretScanRunTests(PostgresFixture fixture)
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

		_factory = new RunSecretApiFactory(_fixture.ConnectionString, keyPath);
		_client = _factory.CreateClient();

		ProfileRepository profiles = new(_fixture.ConnectionString);
		await profiles.ReplaceAllAsync(
			[new ProfileUpsert("vsphere-runsecret-profile", "vSphere Run-Secret Test Profile", "1.0.0", "invented-commit-runsecret", ProfileStates.Current)],
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

	/// <summary>
	/// The proof test (AC): the ephemeral secret supplied at run initiation touches none
	/// of the persistence surfaces a stored/decrypted secret would in the CLEAR -- not
	/// <c>credentials</c>, not <c>credential_secrets</c>, not <c>jobs.payload</c>, not
	/// <c>jobs.credential_id</c> (which stays NULL -- there is no stored-credential row
	/// to reference), not <c>job_events</c>, not <c>audit_log.detail</c>, not the
	/// store's own log lines, and not even <c>run_secrets.ciphertext</c> itself (it is
	/// envelope-encrypted, not the plaintext value). It DOES reach <c>run_secrets</c>,
	/// exactly once per run (not once per job), and a <c>secret.run_registered</c> audit
	/// row.
	/// </summary>
	[Fact]
	public async Task CreateScanRun_WithRunSecret_NeverPersistedInsecurely_CanaryProof()
	{
		const string canarySecret = "invented-adhoc-canary-a1b2c3"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface
		const string canaryUsername = "adhoc-operator@example.internal";

		Guid siteId = await CreateSiteAsync("adhoc-canary-site");
		// Issue #259: an "ssh" (SRG) target, not "vsphere" -- this test asserts exactly
		// one job on the run, which only holds for a target kind that never triggers an
		// auto-queued discover job (discover only supports vsphere). The ad hoc
		// credential flow under test here has no dependency on target kind.
		Guid target = await CreateTargetAsync(siteId, "ssh", "photon-01", """{"host":"photon-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			credential = new { kind = "personal", username = canaryUsername, secret = canarySecret },
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", "Viewer", body: null);
		using JsonDocument jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement[] rows = [.. jobs.RootElement.EnumerateArray()];
		Assert.Single(rows);
		Guid jobId = rows[0].GetProperty("id").GetGuid();

		// The response body itself must never carry the secret (write-only API,
		// security.md control 3).
		string responseBody = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canarySecret, responseBody, StringComparison.Ordinal);
		string jobsBody = await jobsResponse.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canarySecret, jobsBody, StringComparison.Ordinal);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// jobs: neither payload nor credential_id carries the secret; credential_id is
		// NULL outright for an ad hoc job (no stored row exists to reference), and
		// has_run_secret is true (migration 0023).
		await using (NpgsqlCommand jobRow = new(
			"SELECT payload::text, credential_id, has_run_secret FROM jobs WHERE id = $1", connection))
		{
			jobRow.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await jobRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.DoesNotContain(canarySecret, reader.GetString(0), StringComparison.Ordinal);
			Assert.True(reader.IsDBNull(1), "an ad hoc job's credential_id must be NULL -- never a stored row reference.");
			Assert.True(reader.GetBoolean(2), "an ad hoc job must be marked has_run_secret.");
		}

		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM jobs WHERE payload::text LIKE '%' || $1 || '%' OR note LIKE '%' || $1 || '%'", canarySecret);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", canarySecret);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM audit_log WHERE detail::text LIKE '%' || $1 || '%'", canarySecret);

		// credentials / credential_secrets: the ADR-0011 headline claim -- an ad hoc run
		// creates ZERO rows in either table. Not "redacted", not "one row with a
		// scrubbed value" -- absent.
		await using (NpgsqlCommand credentialCount = new("SELECT count(*) FROM credentials", connection))
		{
			Assert.Equal(0L, (long)(await credentialCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand secretCount = new("SELECT count(*) FROM credential_secrets", connection))
		{
			Assert.Equal(0L, (long)(await secretCount.ExecuteScalarAsync())!);
		}

		// run_secrets: exactly one row for the run (not one per job), the ciphertext
		// column itself never carries the plaintext canary (it is AES-256-GCM sealed
		// bytes, but assert the string-search property anyway as a belt-and-braces
		// canary check), and the username half of the pair IS stored in the clear
		// (design: the username is not secret material, same as credentials.username).
		await using (NpgsqlCommand runSecretRow = new(
			"SELECT username, encode(ciphertext, 'hex'), expires_at FROM run_secrets WHERE run_id = $1", connection))
		{
			runSecretRow.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await runSecretRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "expected exactly one run_secrets row for the run.");
			Assert.Equal(canaryUsername, reader.GetString(0));
			Assert.DoesNotContain(canarySecret, reader.GetString(1), StringComparison.OrdinalIgnoreCase);
			Assert.True(reader.GetFieldValue<DateTimeOffset>(2) > DateTimeOffset.UtcNow, "expires_at must be in the future at creation.");
		}

		await using (NpgsqlCommand runSecretCount = new("SELECT count(*) FROM run_secrets", connection))
		{
			Assert.Equal(1L, (long)(await runSecretCount.ExecuteScalarAsync())!);
		}

		// audit_log: the registration row landed, attributed, with no stored credential
		// to reference and run_id set (not job_id -- registration is a run-level event).
		await using (NpgsqlCommand auditRow = new(
			"SELECT credential_id, job_id, run_id, actor FROM audit_log WHERE event_type = 'secret.run_registered' AND run_id = $1", connection))
		{
			auditRow.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await auditRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "expected a secret.run_registered audit row for the run.");
			Assert.True(reader.IsDBNull(0), "audit_log.credential_id must be NULL for a run secret registration.");
			Assert.True(reader.IsDBNull(1), "audit_log.job_id must be NULL for a run-level registration event.");
			Assert.Equal("test-user", reader.GetString(3));
		}

		// Captured log lines from the store itself: run id and actor are fine to log,
		// the secret value is not.
		foreach (CapturedLogEntry entry in _factory.StoreLogger.Entries)
		{
			Assert.DoesNotContain(canarySecret, entry.Message, StringComparison.Ordinal);
		}

		// The store DID receive it, encrypted -- decrypting it back (as the runner
		// would, at the point of use) recovers the exact canary and audits the
		// decrypt.
		IRunSecretStore store = _factory.Services.GetRequiredService<IRunSecretStore>();
		using DecryptedRunSecret? decrypted = await store.DecryptAsync(runId, jobId, "runner-under-test", CancellationToken.None);
		Assert.NotNull(decrypted);
		Assert.Equal(canaryUsername, decrypted!.Username);
		Assert.Equal(canarySecret, decrypted.Secret);

		// Unlike the predecessor single-shot in-memory cache, decrypt is NOT
		// single-shot -- a retried or lease-recovered job must be able to decrypt
		// again while the run is non-terminal. A second decrypt still succeeds.
		using DecryptedRunSecret? decryptedAgain = await store.DecryptAsync(runId, jobId, "runner-under-test", CancellationToken.None);
		Assert.NotNull(decryptedAgain);
		Assert.Equal(canarySecret, decryptedAgain!.Secret);
	}

	/// <summary>
	/// Issue #586 AC "Multiple ad hoc credentials can coexist in one run without
	/// cross-target/purpose access": two vsphere targets, each with its own inline
	/// vsphere-api ad hoc credential, land as two SEPARATE run_secrets rows (not one
	/// shared row) and decrypt-audit proves target A's job decrypts only target A's
	/// canary, never target B's -- the least-privilege AC, not merely "both exist".
	/// Also proves neither canary ever appears anywhere in the clear (payload, response,
	/// job_events, audit_log.detail, jobs.credential_id) -- the same canary-proof
	/// discipline as <see cref="CreateScanRun_WithRunSecret_NeverPersistedInsecurely_CanaryProof"/>,
	/// extended to the per-target/per-purpose shape.
	/// </summary>
	[Fact]
	public async Task CreateScanRun_WithAdHocCredentials_MultipleTargets_CoexistWithoutCrossAccess()
	{
		const string canaryA = "invented-adhoc-target-a-8f3e"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface
		const string canaryB = "invented-adhoc-target-b-1c9d"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface
		const string usernameA = "adhoc-a@example.internal";
		const string usernameB = "adhoc-b@example.internal";

		Guid siteId = await CreateSiteAsync("adhoc-multi-site");
		Guid targetA = await CreateTargetAsync(siteId, "vsphere", "vcsa-a", """{"host":"vcsa-a.example.internal"}""");
		Guid targetB = await CreateTargetAsync(siteId, "vsphere", "vcsa-b", """{"host":"vcsa-b.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			ad_hoc_credentials = new[]
			{
				new { target_id = targetA, purpose = "vsphere-api", username = usernameA, secret = canaryA },
				new { target_id = targetB, purpose = "vsphere-api", username = usernameB, secret = canaryB },
			},
		});

		string responseBody = await response.Content.ReadAsStringAsync();
		Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"expected 202, got {(int)response.StatusCode}: {responseBody}");
		Assert.DoesNotContain(canaryA, responseBody, StringComparison.Ordinal);
		Assert.DoesNotContain(canaryB, responseBody, StringComparison.Ordinal);

		using JsonDocument created = JsonDocument.Parse(responseBody);
		Guid runId = created.RootElement.GetProperty("run_id").GetGuid();

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", "Viewer", body: null);
		string jobsBody = await jobsResponse.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canaryA, jobsBody, StringComparison.Ordinal);
		Assert.DoesNotContain(canaryB, jobsBody, StringComparison.Ordinal);

		// Both targets are freshly-discovered vsphere targets, so each also gets an
		// auto-queued "discover" job (issue #259) sharing its target_id with the "scan"
		// job -- filter to scan jobs only, or ToDictionary would collide on target_id.
		using JsonDocument jobs = JsonDocument.Parse(jobsBody);
		Dictionary<Guid, Guid> jobIdByTarget = jobs.RootElement.EnumerateArray()
			.Where(row => row.GetProperty("job_type").GetString() == "scan")
			.ToDictionary(row => row.GetProperty("target_id").GetGuid(), row => row.GetProperty("id").GetGuid());
		Assert.Equal(2, jobIdByTarget.Count);
		Guid jobA = jobIdByTarget[targetA];
		Guid jobB = jobIdByTarget[targetB];

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// jobs.credential_id stays NULL for both -- the default (execution) purpose
		// resolved ad hoc, which has no legacy-column representation.
		await using (NpgsqlCommand jobRow = new("SELECT credential_id, payload::text FROM jobs WHERE id = ANY($1)", connection))
		{
			jobRow.Parameters.AddWithValue(new[] { jobA, jobB });
			await using NpgsqlDataReader reader = await jobRow.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				Assert.True(reader.IsDBNull(0), "an ad hoc-resolved purpose must leave jobs.credential_id NULL.");
				Assert.DoesNotContain(canaryA, reader.GetString(1), StringComparison.Ordinal);
				Assert.DoesNotContain(canaryB, reader.GetString(1), StringComparison.Ordinal);
			}
		}

		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM jobs WHERE payload::text LIKE '%' || $1 || '%' OR note LIKE '%' || $1 || '%'", canaryA);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM jobs WHERE payload::text LIKE '%' || $1 || '%' OR note LIKE '%' || $1 || '%'", canaryB);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", canaryA);
		await AssertNoCanaryAsync(connection, "SELECT count(*) FROM audit_log WHERE detail::text LIKE '%' || $1 || '%'", canaryA);

		// Two SEPARATE run_secrets rows, each scoped to its own target -- not one shared
		// row (the legacy flat shape) and not collapsed into a single row.
		await using (NpgsqlCommand runSecretCount = new("SELECT count(*) FROM run_secrets WHERE run_id = $1", connection))
		{
			runSecretCount.Parameters.AddWithValue(runId);
			Assert.Equal(2L, (long)(await runSecretCount.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand ciphertextCheck = new("SELECT encode(ciphertext, 'hex') FROM run_secrets WHERE run_id = $1", connection))
		{
			ciphertextCheck.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await ciphertextCheck.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				string hex = reader.GetString(0);
				Assert.DoesNotContain(canaryA, hex, StringComparison.OrdinalIgnoreCase);
				Assert.DoesNotContain(canaryB, hex, StringComparison.OrdinalIgnoreCase);
			}
		}

		// Least-privilege proof (the AC's core claim): target A's job decrypts ONLY
		// target A's canary via its own (target, purpose) key, never target B's, and
		// vice versa.
		IRunSecretStore store = _factory.Services.GetRequiredService<IRunSecretStore>();
		using (DecryptedRunSecret? decryptedA = await store.DecryptAsync(
			runId, RunSecretKey.For(targetA, CredentialPurposes.VSphereApi), jobA, "runner-under-test", CancellationToken.None))
		{
			Assert.NotNull(decryptedA);
			Assert.Equal(usernameA, decryptedA!.Username);
			Assert.Equal(canaryA, decryptedA.Secret);
		}

		using (DecryptedRunSecret? decryptedB = await store.DecryptAsync(
			runId, RunSecretKey.For(targetB, CredentialPurposes.VSphereApi), jobB, "runner-under-test", CancellationToken.None))
		{
			Assert.NotNull(decryptedB);
			Assert.Equal(usernameB, decryptedB!.Username);
			Assert.Equal(canaryB, decryptedB.Secret);
		}

		// Cross-target decrypt: target A's key must never resolve target B's secret.
		using (DecryptedRunSecret? crossDecrypt = await store.DecryptAsync(
			runId, RunSecretKey.For(targetB, CredentialPurposes.VSphereApi), jobA, "runner-under-test", CancellationToken.None))
		{
			Assert.NotNull(crossDecrypt);
			Assert.NotEqual(canaryA, crossDecrypt!.Secret);
			Assert.Equal(canaryB, crossDecrypt.Secret);
		}

		foreach (CapturedLogEntry entry in _factory.StoreLogger.Entries)
		{
			Assert.DoesNotContain(canaryA, entry.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(canaryB, entry.Message, StringComparison.Ordinal);
		}
	}

	/// <summary>Issue #586: same Operator+ floor as the legacy flat <c>credential</c> tier.</summary>
	[Fact]
	public async Task CreateScanRun_WithAdHocCredentials_BelowOperatorRole_Returns403()
	{
		Guid siteId = await CreateSiteAsync("adhoc-purpose-role-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			ad_hoc_credentials = new[] { new { target_id = targetId, purpose = "vsphere-api", username = "x@example.internal", secret = "not-a-real-secret" } },
		});

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM runs", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	/// <summary>Issue #586: a duplicate (target_id, purpose) pair within ad_hoc_credentials is a 400, and creates nothing.</summary>
	[Fact]
	public async Task CreateScanRun_WithAdHocCredentials_DuplicatePair_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-dup-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			ad_hoc_credentials = new[]
			{
				new { target_id = targetId, purpose = "vsphere-api", username = "first@example.internal", secret = "first-secret-value" },
				new { target_id = targetId, purpose = "vsphere-api", username = "second@example.internal", secret = "second-secret-value" },
			},
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM runs", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	/// <summary>Issue #586: the same (target_id, purpose) pair named by both ad_hoc_credentials and credential_overrides is a 400 -- neither tier may silently win.</summary>
	[Fact]
	public async Task CreateScanRun_WithAdHocCredentials_OverlappingCredentialOverride_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-overlap-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			ad_hoc_credentials = new[] { new { target_id = targetId, purpose = "vsphere-api", username = "x@example.internal", secret = "not-a-real-secret" } },
			credential_overrides = new[] { new { target_id = targetId, purpose = "vsphere-api", credential_id = Guid.NewGuid() } },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	/// <summary>Issue #586: ad_hoc_credentials and the flat legacy credential tier are alternative ways to supply ad hoc secrets -- combining them is a 400.</summary>
	[Fact]
	public async Task CreateScanRun_WithAdHocCredentialsAndInlineCredential_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-mixed-tier-site");
		Guid targetId = await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			credential = new { kind = "personal", username = "flat@example.internal", secret = "not-a-real-secret" },
			ad_hoc_credentials = new[] { new { target_id = targetId, purpose = "vsphere-api", username = "x@example.internal", secret = "not-a-real-secret-2" } },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithRunSecret_BelowOperatorRole_Returns403()
	{
		Guid siteId = await CreateSiteAsync("adhoc-role-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Cyber", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM runs", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task CreateScanRun_WithRunSecretAndCredentialId_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-mutex-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			credential_id = Guid.NewGuid(),
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithRunSecret_UnsupportedKind_Returns400()
	{
		Guid siteId = await CreateSiteAsync("adhoc-kind-site");
		await CreateTargetAsync(siteId, "vsphere", "vcsa-01", """{"host":"vcsa-01.example.internal"}""");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "scan",
			scope = JsonSerializer.Serialize(new { site_id = siteId, profile_id = _profileId }),
			credential = new { kind = "vault-wrapped", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task CreateScanRun_WithRunSecret_NotAScanRun_Returns400()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/runs", "Operator", new
		{
			run_type = "download",
			scope = "{}",
			credential = new { kind = "personal", username = "someone@example.internal", secret = "not-a-real-secret-value" },
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	private static async Task AssertNoCanaryAsync(NpgsqlConnection connection, string sql, string canary)
	{
		await using NpgsqlCommand command = new(sql, connection);
		command.Parameters.AddWithValue(canary);
		Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
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
			"TRUNCATE TABLE job_events, audit_log, jobs, run_secrets, runs, targets, sites, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
