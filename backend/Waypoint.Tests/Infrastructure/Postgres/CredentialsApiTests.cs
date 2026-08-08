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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
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

	/// <summary>PR #187 round 1, finding 1: a credential still referenced by jobs/runs
	/// is 409, not 500 -- job history keeps its attribution (the auth-halt window
	/// depends on it), and the pre-written deletion audit rolls back with it.</summary>
	[Fact]
	public async Task DeletingAnInUseCredential_Is409_AndNothingIsAudited()
	{
		Guid id = await CreateCredentialAsync("in-use");
		await SeedJobAsync(id);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/credentials/{id}", body: null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Contains("credential_in_use", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'credential.deleted' AND credential_id = $1", connection);
		audited.Parameters.AddWithValue(id);
		Assert.Equal(0L, (long)(await audited.ExecuteScalarAsync())!);
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

	/// <summary>Issue #20: a successful /test decrypts the stored secret (audited like any other decrypt),
	/// flips health to 'valid', and never returns the secret value itself.</summary>
	[Fact]
	public async Task Test_WithAStoredSecret_Succeeds_AndFlipsHealthToValid_AndLeaksNothing()
	{
		const string canary = "invented-test-endpoint-canary-9f2c";
		Guid id = await CreateCredentialAsync("test-me", canary);

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/credentials/{id}/test", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(canary, body, StringComparison.Ordinal);

		using JsonDocument document = JsonDocument.Parse(body);
		Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
		Assert.Equal("valid", document.RootElement.GetProperty("health").GetString());

		Assert.Equal("valid", await GetFieldAsync(id, "health"));
	}

	/// <summary>Issue #20: testing a credential with no stored secret fails cleanly and flips health to auth_failing.</summary>
	[Fact]
	public async Task Test_WithNoStoredSecret_Fails_AndFlipsHealthToAuthFailing()
	{
		Guid id = await CreateCredentialAsync("test-no-secret");

		HttpResponseMessage response = await SendAsync(HttpMethod.Post, $"/api/v1/credentials/{id}/test", body: null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
		Assert.Equal("auth_failing", document.RootElement.GetProperty("health").GetString());

		Assert.Equal("auth_failing", await GetFieldAsync(id, "health"));
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

	private async Task<string> LoginAsAdminAsync()
	{
		HttpResponseMessage response = await _client.PostAsJsonAsync(
			"/api/v1/auth/login", new { username = "admin", password = WaypointApiFactory.TestAdminPassword });
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("token").GetString()!;
	}
}
