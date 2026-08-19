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
/// Issue #521 (AC3 of #29): step-up re-authentication end to end against real Postgres
/// and the real <c>AddJwtBearer</c>/<c>OidcClaimsMappingOptionsSetup</c> pipeline
/// (via <see cref="OidcApiFactory"/>), covering the three cases <c>docs/security.md</c>
/// "Step-up re-authentication" commits to: a fresh <c>auth_time</c> is allowed, a
/// stale-but-otherwise-valid token is rejected with the distinct <c>step_up_required</c>
/// code, and a token missing <c>auth_time</c> entirely fails closed the same way.
/// Renaming/sudo-only updates (no <c>secret</c> in the body) are unaffected by any of
/// this -- <c>CredentialsApiTests</c> (local-auth, which always counts as fresh under
/// the documented dev carve-out) already covers the happy path for those.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory; Dispose removes the key dir.
public sealed class FreshAuthCredentialOverwriteTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private sealed class StepUpOidcApiFactory : OidcApiFactory
	{
		private readonly string _connectionString;
		private readonly string _keyPath;

		public StepUpOidcApiFactory(string connectionString, string keyPath)
		{
			_connectionString = connectionString;
			_keyPath = keyPath;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			// Same wiring CredentialsApiTests' SecretsApiFactory uses -- a real
			// Postgres-backed credential store behind a real master key, layered on
			// top of OidcApiFactory's real JwtBearer pipeline (not a fake principal).
			builder.ConfigureTestServices(services =>
			{
				services.AddSingleton<IMasterKeyProvider>(new FileMasterKeyProvider(_keyPath));
				services.AddSingleton<IEnvelopeCipher, AesGcmEnvelopeCipher>();
				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton<ISecretTracker>(new InPlaySecretRedactor());
				services.AddSingleton<ICredentialSecretStore>(provider => new CredentialSecretStore(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					provider.GetRequiredService<ISecretTracker>(),
					NullLogger<CredentialSecretStore>.Instance));
				services.AddSingleton<ICredentialCreationCoordinator>(provider => new CredentialCreationCoordinator(
					_connectionString,
					provider.GetRequiredService<IEnvelopeCipher>(),
					NullLogger<CredentialCreationCoordinator>.Instance));

				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-stepup-key").FullName;
	private StepUpOidcApiFactory _factory = null!;
	private HttpClient _client = null!;

	public FreshAuthCredentialOverwriteTests(PostgresFixture fixture)
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
		_factory = new StepUpOidcApiFactory(_fixture.ConnectionString, keyPath);
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

	[Fact]
	public async Task OverwritingSecret_WithFreshAuthTime_Succeeds()
	{
		Guid id = await CreateCredentialAsync(authTime: DateTimeOffset.UtcNow);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Put, $"/api/v1/credentials/{id}",
			new { secret = "invented-fresh-rotation" },
			OidcApiFactory.IssueToken(authTime: DateTimeOffset.UtcNow));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task OverwritingSecret_WithStaleAuthTime_IsRejected_WithStepUpRequiredCode()
	{
		Guid id = await CreateCredentialAsync(authTime: DateTimeOffset.UtcNow);
		string staleToken = OidcApiFactory.IssueToken(authTime: DateTimeOffset.UtcNow.AddMinutes(-30));

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Put, $"/api/v1/credentials/{id}", new { secret = "invented-stale-rotation" }, staleToken);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		ErrorEnvelopeAssertions.AssertEnvelope(body, "step_up_required");
	}

	[Fact]
	public async Task OverwritingSecret_WithNoAuthTimeClaim_FailsClosed_WithStepUpRequiredCode()
	{
		Guid id = await CreateCredentialAsync(authTime: DateTimeOffset.UtcNow);
		string tokenWithoutAuthTime = OidcApiFactory.IssueToken();

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Put, $"/api/v1/credentials/{id}", new { secret = "invented-no-auth-time-rotation" }, tokenWithoutAuthTime);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "step_up_required");
	}

	/// <summary>Renaming (no `secret` in the body) is never gated, even with a stale token -- only the secret-overwrite path is.</summary>
	[Fact]
	public async Task RenamingWithoutASecret_IsNeverGated_EvenWithAStaleToken()
	{
		Guid id = await CreateCredentialAsync(authTime: DateTimeOffset.UtcNow);
		string staleToken = OidcApiFactory.IssueToken(authTime: DateTimeOffset.UtcNow.AddMinutes(-30));

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Put, $"/api/v1/credentials/{id}", new { name = $"renamed-{Guid.NewGuid():N}" }, staleToken);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private async Task<Guid> CreateCredentialAsync(DateTimeOffset authTime)
	{
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, "/api/v1/credentials",
			new { name = $"stepup-{Guid.NewGuid():N}", credential_type = "token" },
			OidcApiFactory.IssueToken(authTime: authTime));
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string token)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add("Authorization", $"Bearer {token}");
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await _client.SendAsync(request);
	}
}
