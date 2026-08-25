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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #691's assisted enrollment API end to end against real Postgres: state
/// machine transitions, structurally valid codes accepted regardless of the disposable
/// Depot ID (owner decision 2026-08-25: identity follows the code), the code value
/// never appearing in any response, and role floors -- role coverage uses
/// <see cref="TestAuthHandler"/>'s <c>X-Test-Role</c> header exactly like
/// <c>DownloadsApiTests</c>, rather than the local-auth login path (which always
/// issues an Admin token and cannot exercise a lower role's 403). No job dispatcher
/// runs here (mirrors <see cref="DownloadsApiTests"/>'s own scope note) -- the
/// <c>depot-enrollment</c> job handler's own execution (tool invocation, validation
/// classification) is covered separately by <c>DepotEnrollmentJobHandlerTests</c>
/// (fake tool, generate-depot-id) and <c>DepotEnrollmentValidateEndToEndTests</c>
/// (real credential decrypt against Postgres, validate-code).
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory; Dispose removes the key dir.
public sealed class DepotEnrollmentApiTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private const string InventedCode = "eyJhc3NldF9pZCI6IldQVC0wMDAxLURFUE9ULUlEIn0="; // gitleaks:allow — invented fixture, base64 JSON {"asset_id":"WPT-0001-DEPOT-ID"}, not a real Broadcom code

	private sealed class DepotEnrollmentApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;
		private readonly InPlaySecretRedactor _redactor;

		public DepotEnrollmentApiFactory(string connectionString, string keyPath, InPlaySecretRedactor redactor)
		{
			_connectionString = connectionString;
			_keyPath = keyPath;
			_redactor = redactor;
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
				services.AddSingleton<IDepotEnrollmentRepository>(new DepotEnrollmentRepository(_connectionString));

				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-enrollment-api-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();
	private DepotEnrollmentApiFactory _factory = null!;
	private HttpClient _client = null!;

	public DepotEnrollmentApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetEnrollmentAsync();

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		_factory = new DepotEnrollmentApiFactory(_fixture.ConnectionString, keyPath, _redactor);
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
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private async Task ResetEnrollmentAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'tool_unavailable', depot_id = NULL, depot_id_generated_at = NULL,
			    paired_asset_id = NULL, paired_at = NULL, last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
		await using NpgsqlCommand deleteCredentials = new("DELETE FROM credentials WHERE credential_type = 'depot-activation-code'", connection);
		await deleteCredentials.ExecuteNonQueryAsync();
	}

	private async Task SetDepotIdAsync(string depotId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"UPDATE depot_enrollment SET depot_id = $1, depot_id_generated_at = now(), state = 'awaiting_portal_registration' WHERE id = 1",
			connection);
		command.Parameters.AddWithValue(depotId);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task Get_NoAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/downloads/enrollment");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Get_InitialState_ReportsToolUnavailableAndCorrectedRegistrationUrl()
	{
		HttpResponseMessage response = await SendAsync("Viewer", HttpMethod.Get, "/api/v1/downloads/enrollment", null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("tool_unavailable", document.RootElement.GetProperty("state").GetString());
		Assert.False(document.RootElement.GetProperty("activation_code_configured").GetBoolean());
		// The .com URL, never the documented VCFDT 9.1 .net typo (issue #691).
		Assert.Equal("https://vcf.broadcom.com", document.RootElement.GetProperty("registration_url").GetString());
	}

	[Fact]
	public async Task GenerateDepotId_AsViewer_Returns403()
	{
		HttpResponseMessage response = await SendAsync("Viewer", HttpMethod.Post, "/api/v1/downloads/enrollment/depot-id", null);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task GenerateDepotId_AsOperator_Returns403_OnlyAdminMayMutateEnrollment()
	{
		HttpResponseMessage response = await SendAsync("Operator", HttpMethod.Post, "/api/v1/downloads/enrollment/depot-id", null);
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task GenerateDepotId_AsAdmin_QueuesADepotEnrollmentRun()
	{
		HttpResponseMessage response = await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/depot-id", null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(Guid.TryParse(document.RootElement.GetProperty("run_id").GetString(), out _));
		Assert.True(Guid.TryParse(document.RootElement.GetProperty("job_id").GetString(), out _));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT job_type, run_type FROM jobs j JOIN runs r ON r.id = j.run_id", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("depot-enrollment", reader.GetString(0));
		Assert.Equal("depot-enrollment", reader.GetString(1));
	}

	[Fact]
	public async Task AcceptActivationCode_BeforeDepotIdGenerated_IsAccepted()
	{
		// Owner decision 2026-08-25: identity follows the code. A structurally valid code
		// may be stored with NO prior Depot ID -- the Depot ID is the disposable
		// portal-registration assist, not a precondition for storing a code.
		HttpResponseMessage response = await SendAsync(
			"Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("activation_code_stored", document.RootElement.GetProperty("state").GetString());
		Assert.True(document.RootElement.GetProperty("activation_code_configured").GetBoolean());
	}

	[Fact]
	public async Task AcceptActivationCode_StructurallyInvalidCode_Returns400_AndNeverEchoesTheInput()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		const string garbage = "not-a-valid-base64-activation-code!!";
		HttpResponseMessage response = await SendAsync(
			"Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = garbage });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("invalid_activation_code", body, StringComparison.Ordinal);
		Assert.DoesNotContain(garbage, body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AcceptActivationCode_AssetIdDiffersFromDepotId_IsStillAccepted_IdentityFollowsTheCode()
	{
		// Owner decision 2026-08-25: the accept-time asset_id-vs-Depot-ID mismatch rejection
		// is removed. A code whose asset_id differs from a previously generated (disposable)
		// Depot ID is stored as-is -- swapping in a different working code just works.
		await SetDepotIdAsync("A-DIFFERENT-DEPOT-ID");

		HttpResponseMessage response = await SendAsync(
			"Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("activation_code_stored", document.RootElement.GetProperty("state").GetString());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM credentials WHERE credential_type = 'depot-activation-code'", connection);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task AcceptActivationCode_StoresEncryptedAndAdvancesState_AndNeverReturnsTheCodeValue()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		HttpResponseMessage response = await SendAsync(
			"Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(InventedCode, body, StringComparison.Ordinal);

		using JsonDocument document = JsonDocument.Parse(body);
		Assert.Equal("activation_code_stored", document.RootElement.GetProperty("state").GetString());
		Assert.True(document.RootElement.GetProperty("activation_code_configured").GetBoolean());

		// The stored credential row carries metadata only -- no wire shape here has a
		// secret field to leak, matching CredentialResponse's own contract.
		HttpResponseMessage credentialsResponse = await SendAsync("Viewer", HttpMethod.Get, "/api/v1/credentials", null);
		string credentialsBody = await credentialsResponse.Content.ReadAsStringAsync();
		Assert.Contains("depot-activation-code", credentialsBody, StringComparison.Ordinal);
		Assert.DoesNotContain(InventedCode, credentialsBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AcceptActivationCode_ReplacingAnExistingCredential_RotatesInPlace_NotASecondRow()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });
		HttpResponseMessage second = await SendAsync(
			"Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM credentials WHERE credential_type = 'depot-activation-code'", connection);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task Validate_BeforeACodeIsStored_Returns409()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		HttpResponseMessage response = await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/validate", null);
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task Reset_WithoutConfirmTrue_Returns400_AndDoesNotResetState()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		HttpResponseMessage response = await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/reset", new { confirm = false });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

		HttpResponseMessage getResponse = await SendAsync("Viewer", HttpMethod.Get, "/api/v1/downloads/enrollment", null);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("WPT-0001-DEPOT-ID", document.RootElement.GetProperty("depot_id").GetString());
	}

	[Fact]
	public async Task Reset_WithConfirmTrue_ClearsDepotIdAndPairing_ButNeverDeletesTheStoredCredential()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");
		await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/activation-code", new { activation_code = InventedCode });

		HttpResponseMessage response = await SendAsync("Admin", HttpMethod.Post, "/api/v1/downloads/enrollment/reset", new { confirm = true });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("depot_id_unavailable", document.RootElement.GetProperty("state").GetString());
		Assert.False(
			document.RootElement.TryGetProperty("depot_id", out JsonElement depotIdElement) && depotIdElement.ValueKind != JsonValueKind.Null);

		// The Activation Code credential row itself is untouched -- reset only clears
		// this enrollment record's own pairing state (issue #691 AC).
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM credentials WHERE credential_type = 'depot-activation-code'", connection);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task Reset_AsOperator_Returns403()
	{
		await SetDepotIdAsync("WPT-0001-DEPOT-ID");

		HttpResponseMessage response = await SendAsync("Operator", HttpMethod.Post, "/api/v1/downloads/enrollment/reset", new { confirm = true });
		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private async Task<HttpResponseMessage> SendAsync(string role, HttpMethod method, string path, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await _client.SendAsync(request);
	}
}
