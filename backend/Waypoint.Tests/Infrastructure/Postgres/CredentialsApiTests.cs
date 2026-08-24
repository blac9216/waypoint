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
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Epic #8 slice 3 (#180): the write-only credentials API end to end against real
/// Postgres -- metadata round trips, secret material in-only (the stored value never
/// appears in ANY response), rotation stamping, deletion cascade, and the epic's
/// canary: an API-stored secret decrypts for a job (audited) while remaining absent
/// from every observable surface.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory; Dispose removes the key dir.
public sealed class CredentialsApiTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private sealed class SecretsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;
		private readonly InPlaySecretRedactor _redactor;

		public SecretsApiFactory(string connectionString, string keyPath, InPlaySecretRedactor redactor)
		{
			_connectionString = connectionString;
			_keyPath = keyPath;
			_redactor = redactor;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			// Container-level overrides (config-level ones do not stick for
			// minimal-hosting factories -- see EventStreamEndpointTests).
			builder.ConfigureTestServices(services =>
			{
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();
				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton<ISecretTracker>(_redactor);
				services.AddSingleton<ICredentialSecretStore>(provider => new CredentialSecretStore(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					provider.GetRequiredService<ISecretTracker>(),
					NullLogger<CredentialSecretStore>.Instance));
				services.AddSingleton<ICredentialCreationCoordinator>(provider => new CredentialCreationCoordinator(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					NullLogger<CredentialCreationCoordinator>.Instance));

				// Issue #245: /credentials/{id}/test now fans out a job, so the
				// controller needs IJobControlRepository -- JobEngine:Enabled is false
				// in the Testing environment (appsettings.Testing.json), so the real
				// registration in AddWaypointInfrastructure never runs; register it
				// directly here, the same pattern CatalogApiTests already uses for
				// DownloadsController's job fan-out. One instance backs both focused
				// interfaces (issue #415).
				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-api-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();
	private SecretsApiFactory _factory = null!;
	private HttpClient _client = null!;
	private string _token = null!;

	public CredentialsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		_factory = new SecretsApiFactory(_fixture.ConnectionString, keyPath, _redactor);
		_client = _factory.CreateClient();
		_token = await LoginAsAdminAsync();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	[Fact]
	public async Task CreateWithSecret_ReturnsMetadataOnly_AndTheValueAppearsInNoResponse()
	{
		const string canary = "invented-api-canary-77aa";
		HttpResponseMessage created = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"depot-{Guid.NewGuid():N}", credential_type = "token", secret = canary });

		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		string createdBody = await created.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canary, createdBody, StringComparison.Ordinal);

		using JsonDocument document = JsonDocument.Parse(createdBody);
		Guid id = document.RootElement.GetProperty("id").GetGuid();
		Assert.True(document.RootElement.GetProperty("has_secret").GetBoolean());
		Assert.False(document.RootElement.TryGetProperty("secret", out _));

		foreach (string path in new[] { "/api/v1/credentials", $"/api/v1/credentials/{id}" })
		{
			HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, body: null);
			Assert.DoesNotContain(canary, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RotatingViaTheApi_StampsRotatedAt_AndTheNewValueDecrypts()
	{
		Guid id = await CreateCredentialAsync("rotate-target");
		Assert.Null(await GetFieldAsync(id, "rotated_at"));

		HttpResponseMessage updated = await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}", new { secret = "invented-rotated-value" });
		Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
		Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("rotated_at").ValueKind);

		ICredentialSecretStore store = _factory.Services.GetRequiredService<ICredentialSecretStore>();
		using DecryptedSecret handle = await store.DecryptAsync(id, "test", null, null, CancellationToken.None);
		Assert.Equal("invented-rotated-value", handle.Value);
	}

	/// <summary>The epic's acceptance canary: API-stored secret -> job-attributed decrypt (audited) -> value absent from audit rows and metadata surfaces.</summary>
	[Fact]
	public async Task AnApiStoredSecret_DecryptsForAJob_Audited_AndLeaksNowhere()
	{
		const string canary = "invented-depot-token-canary-3355";
		Guid id = await CreateCredentialAsync("canary-credential", canary);
		Guid jobId = await SeedJobAsync(id);

		ICredentialSecretStore store = _factory.Services.GetRequiredService<ICredentialSecretStore>();
		using (DecryptedSecret handle = await store.DecryptAsync(id, "engine", jobId, null, CancellationToken.None))
		{
			Assert.Equal(canary, handle.Value);
			Assert.Equal("[REDACTED]", _redactor.Redact(canary));
		}

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id = $2", connection))
		{
			audited.Parameters.AddWithValue(id);
			audited.Parameters.AddWithValue(jobId);
			Assert.Equal(1L, (long)(await audited.ExecuteScalarAsync())!);
		}

		// The value appears NOWHERE observable: audit detail, credentials metadata,
		// or the ciphertext itself (it is sealed, not stored).
		await using (NpgsqlCommand leaked = new(
			"""
			SELECT
				(SELECT count(*) FROM audit_log WHERE detail::text LIKE '%' || $1 || '%') +
				(SELECT count(*) FROM credentials WHERE (name || owner || health) LIKE '%' || $1 || '%') +
				(SELECT count(*) FROM credential_secrets WHERE position(convert_to($1, 'UTF8') IN ciphertext) > 0)
			""", connection))
		{
			leaked.Parameters.AddWithValue(canary);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}
	}

	[Fact]
	public async Task DuplicateName_Is409_AndMissingFields_Are400()
	{
		string takenName = $"taken-{Guid.NewGuid():N}";
		HttpResponseMessage first = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = takenName, credential_type = "token" });
		Assert.Equal(HttpStatusCode.Created, first.StatusCode);
		HttpResponseMessage duplicate = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = takenName, credential_type = "token" });
		Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

		HttpResponseMessage invalid = await SendAsync(HttpMethod.Post, "/api/v1/credentials", new { name = "no-type" });
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
	}

	[Fact]
	public async Task Delete_CascadesTheSecretBlob()
	{
		Guid id = await CreateCredentialAsync("delete-me", "invented-blob");
		HttpResponseMessage deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand orphaned = new("SELECT count(*) FROM credential_secrets WHERE credential_id = $1", connection);
		orphaned.Parameters.AddWithValue(id);
		Assert.Equal(0L, (long)(await orphaned.ExecuteScalarAsync())!);

		HttpResponseMessage gone = await SendAsync(HttpMethod.Get, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
	}

	/// <summary>
	/// Issue #189: migration 0006 exists so the audit trail outlives its subject --
	/// <c>Delete_CascadesTheSecretBlob</c> exercises this implicitly (the deleted
	/// credential has a prior <c>secret.decrypted</c> audit row, so without
	/// <c>ON DELETE SET NULL</c> the delete itself would 500 on the FK), but nothing
	/// asserts the behavior directly. This does: a decrypt before delete produces a
	/// prior audit row pinned by id; after delete that row still exists with
	/// <c>credential_id</c> nulled -- not deleted, not an orphaned-FK error -- and the
	/// <c>credential.deleted</c> row itself (also nulled) carries the credential's
	/// id/name attribution in <c>detail</c> even though its own FK is nulled by the
	/// same constraint.
	/// </summary>
	[Fact]
	public async Task Delete_AuditRowsSurviveWithCredentialIdNulled_AndDeletedRowCarriesAttribution()
	{
		Guid id = await CreateCredentialAsync("audit-survives", "invented-audit-survival-blob");
		string fullName;
		await using (NpgsqlConnection lookup = new(_fixture.ConnectionString))
		{
			await lookup.OpenAsync();
			await using NpgsqlCommand read = new("SELECT name FROM credentials WHERE id = $1", lookup);
			read.Parameters.AddWithValue(id);
			fullName = (string)(await read.ExecuteScalarAsync())!;
		}

		// Produce a prior audit row attributed to this credential before it's
		// deleted, pinned by its own id so the assertions below aren't guessing
		// which row is "the" survivor.
		ICredentialSecretStore store = _factory.Services.GetRequiredService<ICredentialSecretStore>();
		using (await store.DecryptAsync(id, "test", null, null, CancellationToken.None))
		{
			// value not needed -- only the resulting audit row matters here.
		}

		Guid decryptAuditRowId;
		await using (NpgsqlConnection preDelete = new(_fixture.ConnectionString))
		{
			await preDelete.OpenAsync();
			await using NpgsqlCommand read = new(
				"SELECT id FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1", preDelete);
			read.Parameters.AddWithValue(id);
			decryptAuditRowId = (Guid)(await read.ExecuteScalarAsync())!;
		}

		HttpResponseMessage deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		// The prior secret.decrypted row survives the delete with credential_id
		// nulled -- migration 0006's ON DELETE SET NULL, not a cascade delete and
		// not a foreign-key violation.
		await using (NpgsqlCommand survivor = new(
			"SELECT count(*) FROM audit_log WHERE id = $1 AND credential_id IS NULL", connection))
		{
			survivor.Parameters.AddWithValue(decryptAuditRowId);
			Assert.Equal(1L, (long)(await survivor.ExecuteScalarAsync())!);
		}

		// No audit row anywhere still points at the deleted credential's id -- the
		// delete nulled every reference, none were orphaned or removed.
		await using (NpgsqlCommand orphaned = new(
			"SELECT count(*) FROM audit_log WHERE credential_id = $1", connection))
		{
			orphaned.Parameters.AddWithValue(id);
			Assert.Equal(0L, (long)(await orphaned.ExecuteScalarAsync())!);
		}

		// The credential.deleted row itself: credential_id nulled, but detail JSON
		// still carries the credential's id/name attribution -- the audit trail
		// records WHAT was deleted even though the FK no longer points at it.
		await using (NpgsqlCommand deletedRow = new(
			"""
			SELECT detail->>'credential_id', detail->>'name'
			FROM audit_log
			WHERE event_type = 'credential.deleted' AND credential_id IS NULL AND detail->>'credential_id' = $1
			""", connection))
		{
			deletedRow.Parameters.AddWithValue(id.ToString());
			await using NpgsqlDataReader reader = await deletedRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync(), "expected exactly one credential.deleted audit row attributing this credential's id/name");
			Assert.Equal(id.ToString(), reader.GetString(0));
			Assert.Equal(fullName, reader.GetString(1));
			Assert.False(await reader.ReadAsync(), "expected exactly one credential.deleted audit row for this credential");
		}
	}

	/// <summary>PR #187 round 1, finding 1: a credential still referenced by a
	/// non-terminal job is 409, not 500 -- job history keeps its attribution (the
	/// auth-halt window depends on it), and the pre-written deletion audit rolls
	/// back with it. Issue #593: the 409 body now enumerates the machine-readable
	/// <c>active_jobs</c> blocking category and its count.</summary>
	[Fact]
	public async Task DeletingAnInUseCredential_Is409_WithActiveJobsBlocker_AndNothingIsAudited()
	{
		Guid id = await CreateCredentialAsync("in-use");
		await SeedJobAsync(id);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("credential_in_use", body, StringComparison.Ordinal);

		using JsonDocument document = JsonDocument.Parse(body);
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal(1, blockers.GetArrayLength());
		Assert.Equal("active_jobs", blockers[0].GetProperty("category").GetString());
		Assert.Equal(1, blockers[0].GetProperty("count").GetInt32());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'credential.deleted' AND credential_id = $1", connection);
		audited.Parameters.AddWithValue(id);
		Assert.Equal(0L, (long)(await audited.ExecuteScalarAsync())!);
	}

	/// <summary>
	/// Issue #593 (epic #577): the core new behavior -- a credential referenced ONLY
	/// by a terminal job/run is deletable. The job's non-secret attribution
	/// (name/type/username) survives on the jobs row, credential_id is nulled, and
	/// the credential + its secret blob are actually gone.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedOnlyByTerminalHistory_Succeeds_AndSnapshotsAttribution()
	{
		Guid id = await CreateCredentialAsync("terminal-only", "invented-terminal-blob", credentialType: "ssh");
		await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}", new { username = "svc-terminal@example.internal" });
		string fullName = (await GetFieldAsync(id, "name"))!;

		(Guid runId, Guid jobId) = await SeedTerminalRunAndJobAsync(id, jobState: "done", runState: "completed");

		HttpResponseMessage deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand credentialGone = new("SELECT count(*) FROM credentials WHERE id = $1", connection))
		{
			credentialGone.Parameters.AddWithValue(id);
			Assert.Equal(0L, (long)(await credentialGone.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand secretGone = new("SELECT count(*) FROM credential_secrets WHERE credential_id = $1", connection))
		{
			secretGone.Parameters.AddWithValue(id);
			Assert.Equal(0L, (long)(await secretGone.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand jobRow = new(
			"SELECT credential_id, credential_name, credential_type, credential_username FROM jobs WHERE id = $1", connection))
		{
			jobRow.Parameters.AddWithValue(jobId);
			await using NpgsqlDataReader reader = await jobRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.IsDBNull(0), "expected jobs.credential_id nulled after detach");
			Assert.Equal(fullName, reader.GetString(1));
			Assert.Equal("ssh", reader.GetString(2));
			Assert.Equal("svc-terminal@example.internal", reader.GetString(3));
		}

		await using (NpgsqlCommand runRow = new(
			"SELECT credential_id, credential_name, credential_type, credential_username FROM runs WHERE id = $1", connection))
		{
			runRow.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await runRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.IsDBNull(0), "expected runs.credential_id nulled after detach");
			Assert.Equal("ssh", reader.GetString(2));
			Assert.Equal("svc-terminal@example.internal", reader.GetString(3));
		}

		// The wire-facing GET /runs/{id}/jobs and GET /runs/{id} surfaces still show
		// the snapshot after the credential is gone -- proving "historical displays
		// retain a non-secret credential name/identifier snapshot" (epic #577 AC),
		// not just that the DB columns happen to hold a value.
		HttpResponseMessage runResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}", body: null);
		using JsonDocument runDocument = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
		Assert.Equal("ssh", runDocument.RootElement.GetProperty("credential_type").GetString());
		Assert.Equal("svc-terminal@example.internal", runDocument.RootElement.GetProperty("credential_username").GetString());
		// credential_id is a nullable field the serializer omits when null (same
		// DefaultIgnoreCondition.WhenWritingNull convention CredentialResponse uses) --
		// absent, not present-and-null, is what "detached" looks like on the wire.
		Assert.False(runDocument.RootElement.TryGetProperty("credential_id", out _));

		HttpResponseMessage jobsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/jobs", body: null);
		using JsonDocument jobsDocument = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
		JsonElement jobElement = jobsDocument.RootElement[0];
		Assert.Equal("ssh", jobElement.GetProperty("credential_type").GetString());
		Assert.Equal("svc-terminal@example.internal", jobElement.GetProperty("credential_username").GetString());
	}

	/// <summary>
	/// Issue #593: a live <c>targets.credential_id</c> reference blocks deletion even
	/// when every job/run reference is terminal -- config/connection wiring is not
	/// history, and never becomes deletable just because past scans succeeded.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByATarget_Is409_WithTargetsBlocker()
	{
		Guid id = await CreateCredentialAsync("target-bound");
		await SeedTargetReferencingAsync(id);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal("targets", blockers[0].GetProperty("category").GetString());
		Assert.Equal(1, blockers[0].GetProperty("count").GetInt32());
	}

	/// <summary>
	/// Issue #584 (epic #582): a live <c>target_credential_bindings</c> row is its OWN
	/// blocking category, distinct from <c>targets</c> -- a binding can name a
	/// credential for a non-default purpose (here, <c>vcsa-ssh</c> on a vsphere
	/// target) that the target's legacy <c>credential_id</c> column never carried, so
	/// deleting that credential must still be blocked and reported, not silently
	/// allowed because the legacy column points elsewhere (or nowhere).
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByATargetCredentialBinding_Is409_WithTargetCredentialBindingsBlocker()
	{
		Guid sshCredentialId = await CreateCredentialAsync("vcsa-ssh-bound", credentialType: "ssh");
		Guid targetId = await SeedVSphereTargetAsync();
		await SeedTargetCredentialBindingAsync(targetId, "vcsa-ssh", sshCredentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{sshCredentialId}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal("target_credential_bindings", blockers[0].GetProperty("category").GetString());
		Assert.Equal(1, blockers[0].GetProperty("count").GetInt32());
	}

	/// <summary>
	/// Issue #585 (epic #582, migration 0044): a NON-terminal job can reference a
	/// credential purely through its per-purpose snapshot
	/// (<c>job_credential_bindings</c>) for a purpose <c>jobs.credential_id</c> never
	/// carried -- e.g. a vsphere scan job's <c>vcsa-ssh</c> row while the column names
	/// the vsphere-api credential. Deleting the binding-only-referenced credential must
	/// still be blocked as <c>active_jobs</c> (the blocker IS the active job).
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedOnlyByAnActiveJobsBindingRow_Is409_WithActiveJobsBlocker()
	{
		Guid executionCredentialId = await CreateCredentialAsync("job-column-cred");
		Guid bindingOnlyCredentialId = await CreateCredentialAsync("job-binding-only-cred", credentialType: "ssh");
		Guid jobId = await SeedJobAsync(executionCredentialId);
		await SeedJobCredentialBindingAsync(jobId, "vcsa-ssh", bindingOnlyCredentialId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{bindingOnlyCredentialId}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal(1, blockers.GetArrayLength());
		Assert.Equal("active_jobs", blockers[0].GetProperty("category").GetString());
		Assert.Equal(1, blockers[0].GetProperty("count").GetInt32());
	}

	/// <summary>
	/// Issue #585: the #593 terminal-history detach extends to per-purpose snapshot
	/// rows -- deleting a credential referenced only by a TERMINAL job's binding row
	/// succeeds, and the row keeps non-secret attribution (name/type/username, plus its
	/// purpose) with <c>credential_id</c> nulled.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedOnlyByATerminalJobsBindingRow_Succeeds_AndSnapshotsBindingAttribution()
	{
		Guid executionCredentialId = await CreateCredentialAsync("terminal-column-cred");
		Guid bindingCredentialId = await CreateCredentialAsync("terminal-binding-cred", credentialType: "ssh");
		await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{bindingCredentialId}", new { username = "root" });
		string bindingCredentialName = (await GetFieldAsync(bindingCredentialId, "name"))!;
		(_, Guid jobId) = await SeedTerminalRunAndJobAsync(executionCredentialId, jobState: "done", runState: "completed");
		await SeedJobCredentialBindingAsync(jobId, "vcsa-ssh", bindingCredentialId);

		HttpResponseMessage deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{bindingCredentialId}", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand bindingRow = new(
			"SELECT credential_id, credential_name, credential_type, credential_username, purpose FROM job_credential_bindings WHERE job_id = $1", connection);
		bindingRow.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await bindingRow.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.True(reader.IsDBNull(0));
		Assert.Equal(bindingCredentialName, reader.GetString(1));
		Assert.Equal("ssh", reader.GetString(2));
		Assert.Equal("root", reader.GetString(3));
		Assert.Equal("vcsa-ssh", reader.GetString(4));
	}

	private async Task SeedJobCredentialBindingAsync(Guid jobId, string purpose, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO job_credential_bindings (job_id, purpose, credential_id) VALUES ($1, $2, $3)", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(purpose);
		command.Parameters.AddWithValue(credentialId);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>Issue #593: a live <c>schedules.credential_id</c> reference blocks deletion -- same "config, not history" reasoning as the targets case.</summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByASchedule_Is409_WithSchedulesBlocker()
	{
		Guid id = await CreateCredentialAsync("schedule-bound");
		await SeedScheduleReferencingAsync(id);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal("schedules", blockers[0].GetProperty("category").GetString());
	}

	/// <summary>Issue #593: the singleton STIG Manager global connection is "configuration" -- distinct from targets/schedules, still a live binding, not history.</summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByStigManagerConfig_Is409_WithConfigurationBlocker()
	{
		Guid id = await CreateCredentialAsync("stigman-bound", credentialType: "token");
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand insert = new(
				"""
				INSERT INTO stigman_connections (id, endpoint, authority, collection, client_id, credential_id)
				VALUES (1, 'https://stigman.example.internal/api', 'https://idp.example.internal', 'demo', 'waypoint', $1)
				ON CONFLICT (id) DO UPDATE SET credential_id = EXCLUDED.credential_id
				""", connection);
			insert.Parameters.AddWithValue(id);
			await insert.ExecuteNonQueryAsync();
		}

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal("configuration", blockers[0].GetProperty("category").GetString());
	}

	/// <summary>
	/// Issue #593: every category can be reported at once, in the fixed
	/// targets/schedules/configuration/active_jobs order -- an operator sees the
	/// FULL picture, not just the first blocker the repository happens to check.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_WithMultipleBlockers_ReportsEveryCategory()
	{
		Guid id = await CreateCredentialAsync("multi-blocked");
		await SeedTargetReferencingAsync(id);
		await SeedScheduleReferencingAsync(id);
		await SeedJobAsync(id);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		string[] categories = [.. blockers.EnumerateArray().Select(e => e.GetProperty("category").GetString()!)];
		Assert.Equal(["targets", "schedules", "active_jobs"], categories);
	}

	/// <summary>
	/// Issue #593 round 2 (reviewer Finding 1): a live (<c>pending</c>/<c>running</c>)
	/// run referencing the credential blocks deletion even with NO co-referencing
	/// non-terminal job -- the create-&gt;fan-out window / stuck-pending case. Without
	/// the <c>active_runs</c> blocker the relaxed <c>ON DELETE SET NULL</c> FK would
	/// null the live run's credential_id and destroy the secret out from under
	/// in-flight work. Asserts 409 with the <c>active_runs</c> category and that the
	/// credential + secret survive.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByALiveRunWithNoJob_Is409_WithActiveRunsBlocker()
	{
		Guid id = await CreateCredentialAsync("live-run-no-job", "invented-live-blob");
		Guid runId = await SeedRunAsync(id, runState: "pending");

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		Assert.Equal(1, blockers.GetArrayLength());
		Assert.Equal("active_runs", blockers[0].GetProperty("category").GetString());
		Assert.Equal(1, blockers[0].GetProperty("count").GetInt32());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand credentialSurvives = new("SELECT count(*) FROM credentials WHERE id = $1", connection))
		{
			credentialSurvives.Parameters.AddWithValue(id);
			Assert.Equal(1L, (long)(await credentialSurvives.ExecuteScalarAsync())!);
		}

		// The live run keeps its credential_id -- it was NOT nulled by a SET NULL that
		// should never have fired.
		await using (NpgsqlCommand runStillLinked = new("SELECT credential_id FROM runs WHERE id = $1", connection))
		{
			runStillLinked.Parameters.AddWithValue(runId);
			Assert.Equal(id, (Guid)(await runStillLinked.ExecuteScalarAsync())!);
		}
	}

	/// <summary>
	/// Issue #593 round 2: the run-secret ("my credentials", ADR-0011) shape -- the
	/// live run carries <c>credential_id</c> but its fanned-out jobs deliberately carry
	/// NONE (<c>RunCreationService</c> keeps the secret in <c>run_secrets</c>, keyed by
	/// run id). A jobs-only blocker count would miss this entirely; the
	/// <c>active_runs</c> category is what stops the delete.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_ReferencedByALiveRunSecretRun_Is409_WithActiveRunsBlocker()
	{
		Guid id = await CreateCredentialAsync("live-run-secret", "invented-run-secret-blob");
		Guid runId = await SeedRunAsync(id, runState: "running");

		// A job on the run with NO credential_id, mirroring the run-secret fan-out --
		// proves the block comes from the run, not a co-referencing job.
		await using (NpgsqlConnection seed = new(_fixture.ConnectionString))
		{
			await seed.OpenAsync();
			await using NpgsqlCommand insertJob = new(
				"INSERT INTO jobs (run_id, job_type, priority, state, has_run_secret) VALUES ($1, 'scan', 1, 'queued', true)", seed);
			insertJob.Parameters.AddWithValue(runId);
			await insertJob.ExecuteNonQueryAsync();
		}

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement blockers = document.RootElement.GetProperty("error").GetProperty("blockers");
		string[] categories = [.. blockers.EnumerateArray().Select(e => e.GetProperty("category").GetString()!)];
		Assert.Contains("active_runs", categories);
		Assert.DoesNotContain("active_jobs", categories);
	}

	/// <summary>
	/// Issue #593 round 2: once the previously-live run reaches a terminal state, the
	/// same credential becomes deletable -- the <c>active_runs</c> blocker clears, the
	/// run is snapshotted and detached, and the credential + secret are gone. Closes
	/// the loop on the live-run guard: it blocks in-flight work, not history.
	/// </summary>
	[Fact]
	public async Task DeletingACredential_AfterItsLiveRunReachesTerminalState_Succeeds_AndDetaches()
	{
		Guid id = await CreateCredentialAsync("run-then-terminal", "invented-terminal-run-blob", credentialType: "ssh");
		await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}", new { username = "svc-run@example.internal" });
		string fullName = (await GetFieldAsync(id, "name"))!;

		Guid runId = await SeedRunAsync(id, runState: "pending");

		// While live, the delete is blocked.
		HttpResponseMessage blocked = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

		// Drive the run to a terminal state.
		await using (NpgsqlConnection advance = new(_fixture.ConnectionString))
		{
			await advance.OpenAsync();
			await using NpgsqlCommand update = new("UPDATE runs SET state = 'completed' WHERE id = $1", advance);
			update.Parameters.AddWithValue(runId);
			await update.ExecuteNonQueryAsync();
		}

		HttpResponseMessage deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand credentialGone = new("SELECT count(*) FROM credentials WHERE id = $1", connection))
		{
			credentialGone.Parameters.AddWithValue(id);
			Assert.Equal(0L, (long)(await credentialGone.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand runRow = new(
			"SELECT credential_id, credential_name, credential_type, credential_username FROM runs WHERE id = $1", connection))
		{
			runRow.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await runRow.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.True(reader.IsDBNull(0), "expected runs.credential_id nulled after detach");
			Assert.Equal(fullName, reader.GetString(1));
			Assert.Equal("ssh", reader.GetString(2));
			Assert.Equal("svc-run@example.internal", reader.GetString(3));
		}
	}

	/// <summary>Issue #20: credential_type is validated against the closed
	/// <c>CredentialTypes</c> set at the API layer (docs/domain-model.md's four types).</summary>
	[Fact]
	public async Task InvalidCredentialType_Is400()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"bad-type-{Guid.NewGuid():N}", credential_type = "not-a-real-type" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("invalid_credential_type", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	/// <summary>Issue #20 / ADR-0011: SHARED ONLY -- any other owner value is rejected, not silently coerced.</summary>
	[Fact]
	public async Task NonSharedOwner_Is400()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"personal-{Guid.NewGuid():N}", credential_type = "vcenter", owner = "personal" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("invalid_owner", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	/// <summary>Issue #20: sudo_enabled is SSH-only; setting it on any other type is rejected at create and update.</summary>
	[Fact]
	public async Task SudoEnabled_OnNonSshType_Is400_OnCreateAndUpdate()
	{
		HttpResponseMessage createResponse = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"sudo-vcenter-{Guid.NewGuid():N}", credential_type = "vcenter", sudo_enabled = true });
		Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
		Assert.Contains("sudo_requires_ssh", await createResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

		Guid id = await CreateCredentialAsync("no-sudo-token", credentialType: "token");
		HttpResponseMessage updateResponse = await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}", new { sudo_enabled = true });
		Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
		Assert.Contains("sudo_requires_ssh", await updateResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	/// <summary>Issue #20: an SSH credential may set sudo_enabled, and it round-trips in the response.</summary>
	[Fact]
	public async Task SudoEnabled_OnSshType_RoundTrips()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"sudo-ssh-{Guid.NewGuid():N}", credential_type = "ssh", sudo_enabled = true });
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(document.RootElement.GetProperty("sudo_enabled").GetBoolean());
	}

	/// <summary>
	/// Issue #409: a secret-bearing write with no master key configured is a distinct,
	/// operator-actionable <c>503 master_key_unavailable</c> -- not the generic <c>500
	/// internal_error</c> pre-#409 behavior mapped it to. Uses a real
	/// <see cref="FileMasterKeyProvider"/> with no path (exactly how production
	/// resolves an unset <c>WAYPOINT_MASTER_KEY_FILE</c>), so the exception thrown here
	/// is the real <c>MasterKeyUnavailableException</c> from production code, not a
	/// fault-injected stand-in -- <see cref="Api.MasterKeyUnavailableTests"/> covers the
	/// middleware mapping itself via injection; this proves the real credential-write
	/// path actually reaches it.
	/// </summary>
	[Fact]
	public async Task CreateWithSecret_NoMasterKeyConfigured_Is503MasterKeyUnavailable()
	{
		using SecretsApiFactory noKeyFactory = new(_fixture.ConnectionString, keyPath: null!, _redactor);
		using HttpClient noKeyClient = noKeyFactory.CreateClient();
		string token = await LoginAsAdminAsync(noKeyClient);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/credentials");
		request.Headers.Add("Authorization", $"Bearer {token}");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { name = $"no-key-{Guid.NewGuid():N}", credential_type = "token", secret = "invented-no-key-secret" }),
			Encoding.UTF8, "application/json");

		HttpResponseMessage response = await noKeyClient.SendAsync(request);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("master_key_unavailable", body, StringComparison.Ordinal);

		// The detailed exception message (env var name, key path) never reaches the
		// wire -- only the generic, operator-actionable text does.
		Assert.DoesNotContain("WAYPOINT_MASTER_KEY_FILE", body, StringComparison.Ordinal);
	}

	/// <summary>Issue #262: username is a dedicated, non-secret field -- set at create, round-trips
	/// in every response, changeable via PUT, and clearable back to null with an empty string.</summary>
	[Fact]
	public async Task Username_SetAtCreate_RoundTrips_IsChangeableAndClearable()
	{
		HttpResponseMessage created = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"vc-{Guid.NewGuid():N}", credential_type = "vcenter", username = "administrator@example.internal" });
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		using JsonDocument createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		Guid id = createdDocument.RootElement.GetProperty("id").GetGuid();
		Assert.Equal("administrator@example.internal", createdDocument.RootElement.GetProperty("username").GetString());

		HttpResponseMessage updated = await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}",
			new { username = "svc-account@example.internal" });
		Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
		using JsonDocument updatedDocument = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
		Assert.Equal("svc-account@example.internal", updatedDocument.RootElement.GetProperty("username").GetString());

		Assert.Equal("svc-account@example.internal", await GetFieldAsync(id, "username"));

		HttpResponseMessage cleared = await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{id}", new { username = "" });
		Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
		Assert.Null(await GetFieldAsync(id, "username"));
	}

	/// <summary>
	/// Issue #245: /test now queues a real connectivity job instead of running a
	/// synchronous decrypt-only check (issue #20's old behavior) -- 202 with the
	/// queued run/job ids, never the secret value. The job's terminal outcome
	/// flipping <c>credentials.health</c> is proven end to end (dispatcher included)
	/// by <c>CredentialTestJobHandlerEndToEndTests</c>; this API-level test host runs
	/// with <c>JobEngine:Enabled = false</c> (appsettings.Testing.json), so no
	/// dispatcher claims the job here -- only the fan-out contract is asserted.
	/// </summary>
	[Fact]
	public async Task Test_QueuesAConnectivityJob_Returns202_AndLeaksNothing()
	{
		const string canary = "invented-test-endpoint-canary-9f2c";
		Guid id = await CreateCredentialAsync("test-me", canary);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/credentials/{id}/test", body: null);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canary, body, StringComparison.Ordinal);

		using JsonDocument document = JsonDocument.Parse(body);
		Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("run_id").GetGuid());
		Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("job_id").GetGuid());

		// Health is untouched until the job actually runs -- no dispatcher in this
		// API test host, so it stays at its initial 'unknown'.
		Assert.Equal("unknown", await GetFieldAsync(id, "health"));
	}

	/// <summary>A missing credential 404s before any job is queued.</summary>
	[Fact]
	public async Task Test_OnMissingCredential_Is404()
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/credentials/{Guid.NewGuid()}/test", body: null);
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	/// <summary>PR #187 round 1, finding 2: renaming onto a taken name is the same 409 Create maps.</summary>
	[Fact]
	public async Task RenamingOntoATakenName_Is409()
	{
		string takenName = $"rename-taken-{Guid.NewGuid():N}";
		await SendAsync(HttpMethod.Post, "/api/v1/credentials", new { name = takenName, credential_type = "token" });
		Guid other = await CreateCredentialAsync("rename-source");

		HttpResponseMessage response = await SendAsync(HttpMethod.Put, $"/api/v1/credentials/{other}", new { name = takenName });
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Contains("name_taken", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	private async Task<Guid> CreateCredentialAsync(string namePrefix, string? secret = null, string credentialType = "token")
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/credentials",
			new { name = $"{namePrefix}-{Guid.NewGuid():N}", credential_type = credentialType, secret });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add("Authorization", $"Bearer {_token}");
		if (body is not null)
		{
			request.Content = new StringContent(
				JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await _client.SendAsync(request);
	}

	private async Task<string?> GetFieldAsync(Guid id, string field)
	{
		HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"/api/v1/credentials/{id}", body: null);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		// Null-valued fields may be omitted by the serializer -- absent and null both mean "no value".
		return document.RootElement.TryGetProperty(field, out JsonElement element) && element.ValueKind != JsonValueKind.Null
			? element.ToString()
			: null;
	}

	private async Task<Guid> SeedJobAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state, credential_id) VALUES ('download', 1, 'queued', $1) RETURNING id", connection);
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	/// <summary>Issue #593 round 2: a bare run in the given state referencing the credential, with NO job -- exercises the live-run blocker (create-&gt;fan-out window, run-secret runs) where jobs carry no credential_id.</summary>
	private async Task<Guid> SeedRunAsync(Guid credentialId, string runState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insertRun = new(
			"INSERT INTO runs (run_type, scope, state, credential_id) VALUES ('scan', '{}', $1, $2) RETURNING id", connection);
		insertRun.Parameters.AddWithValue(runState);
		insertRun.Parameters.AddWithValue(credentialId);
		return (Guid)(await insertRun.ExecuteScalarAsync())!;
	}

	/// <summary>A run plus its one job, both landed directly on the given terminal states -- used to exercise the terminal-only detach path without running a real job through the dispatcher.</summary>
	private async Task<(Guid RunId, Guid JobId)> SeedTerminalRunAndJobAsync(Guid credentialId, string jobState, string runState)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid runId;
		await using (NpgsqlCommand insertRun = new(
			"INSERT INTO runs (run_type, scope, state, credential_id) VALUES ('scan', '{}', $1, $2) RETURNING id", connection))
		{
			insertRun.Parameters.AddWithValue(runState);
			insertRun.Parameters.AddWithValue(credentialId);
			runId = (Guid)(await insertRun.ExecuteScalarAsync())!;
		}

		Guid jobId;
		await using (NpgsqlCommand insertJob = new(
			"INSERT INTO jobs (run_id, job_type, priority, state, credential_id) VALUES ($1, 'scan', 1, $2, $3) RETURNING id", connection))
		{
			insertJob.Parameters.AddWithValue(runId);
			insertJob.Parameters.AddWithValue(jobState);
			insertJob.Parameters.AddWithValue(credentialId);
			jobId = (Guid)(await insertJob.ExecuteScalarAsync())!;
		}

		return (runId, jobId);
	}

	private async Task SeedTargetReferencingAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid siteId;
		await using (NpgsqlCommand insertSite = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection))
		{
			insertSite.Parameters.AddWithValue($"site-{Guid.NewGuid():N}");
			siteId = (Guid)(await insertSite.ExecuteScalarAsync())!;
		}

		await using NpgsqlCommand insertTarget = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, credential_id)
			VALUES ($1, 'vsphere', $2, '{"host":"vcsa-01.example.internal"}'::jsonb, $3)
			""", connection);
		insertTarget.Parameters.AddWithValue(siteId);
		insertTarget.Parameters.AddWithValue($"target-{Guid.NewGuid():N}");
		insertTarget.Parameters.AddWithValue(credentialId);
		await insertTarget.ExecuteNonQueryAsync();
	}

	/// <summary>Issue #584: a bare vsphere target with no credential binding -- the caller adds bindings separately via <see cref="SeedTargetCredentialBindingAsync"/>.</summary>
	private async Task<Guid> SeedVSphereTargetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid siteId;
		await using (NpgsqlCommand insertSite = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection))
		{
			insertSite.Parameters.AddWithValue($"site-{Guid.NewGuid():N}");
			siteId = (Guid)(await insertSite.ExecuteScalarAsync())!;
		}

		await using NpgsqlCommand insertTarget = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection)
			VALUES ($1, 'vsphere', $2, '{"host":"vcsa-01.example.internal"}'::jsonb)
			RETURNING id
			""", connection);
		insertTarget.Parameters.AddWithValue(siteId);
		insertTarget.Parameters.AddWithValue($"target-{Guid.NewGuid():N}");
		return (Guid)(await insertTarget.ExecuteScalarAsync())!;
	}

	/// <summary>Issue #584: a direct <c>target_credential_bindings</c> row, bypassing the API's compatibility validation -- used only to seed the blocker test's fixture state.</summary>
	private async Task SeedTargetCredentialBindingAsync(Guid targetId, string purpose, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO target_credential_bindings (target_id, purpose, credential_id) VALUES ($1, $2, $3)", connection);
		insert.Parameters.AddWithValue(targetId);
		insert.Parameters.AddWithValue(purpose);
		insert.Parameters.AddWithValue(credentialId);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task SeedScheduleReferencingAsync(Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO schedules (name, job_type, cron_expression, credential_id, created_by)
			VALUES ($1, 'scan', '0 0 * * *', $2, 'tester')
			""", connection);
		insert.Parameters.AddWithValue($"schedule-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialId);
		await insert.ExecuteNonQueryAsync();
	}

	private Task<string> LoginAsAdminAsync() => LoginAsAdminAsync(_client);

	private static async Task<string> LoginAsAdminAsync(HttpClient client)
	{
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/login", new { username = "admin", password = WaypointApiFactory.TestAdminPassword });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("token").GetString()!;
	}
}
