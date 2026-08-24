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
using System.Net.Http.Headers;
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
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #39 end to end against real Postgres: the <c>/downloads/tool</c> REST surface
/// -- role gates, that POST install/upload each queue exactly one <c>tool-install</c>
/// job in its own run, upload staging (artifact + detached signature land in the
/// staging directory, never directly in the tool-state path), and the install-history
/// read. Handler dispatch is not started here (same pattern as
/// <see cref="DownloadsApiTests"/>); the claim-through-activate loop is covered by
/// <c>ManagedToolInstallJobHandlerTests</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ManagedToolApiTests : IAsyncLifetime
{
	private sealed class ManagedToolApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _uploadStagingPath;

		public ManagedToolApiFactory(string connectionString, string uploadStagingPath)
		{
			_connectionString = connectionString;
			_uploadStagingPath = uploadStagingPath;
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

				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
				services.AddSingleton<IManagedToolInstallRepository>(new ManagedToolInstallRepository(_connectionString));
				services.AddSingleton<IApplianceStateRepository>(new ApplianceStateRepository(_connectionString));
				services.Configure<ManagedToolOptions>(options => options.UploadStagingPath = _uploadStagingPath);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _uploadStagingPath = Directory.CreateTempSubdirectory("waypoint-tool-api-upload-").FullName;
	private ManagedToolApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ManagedToolApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetLedgerAsync();

		_factory = new ManagedToolApiFactory(_fixture.ConnectionString, _uploadStagingPath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		if (Directory.Exists(_uploadStagingPath))
		{
			Directory.Delete(_uploadStagingPath, recursive: true);
		}

		return Task.CompletedTask;
	}

	private async Task ResetLedgerAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE managed_tool_installs", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task PostInstall_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.PostAsync("/api/v1/downloads/tool/install", JsonBody(new { source_path = "x" }));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task PostInstall_BelowOperator_Returns403()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/install")
		{
			Content = JsonBody(new { source_path = "vcf-download-tool-1.0" }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostInstall_WithOperatorRole_QueuesOneToolInstallJob()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/install")
		{
			Content = JsonBody(new { source_path = "vcf-download-tool-1.0", version = "1.0" }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid runId = Guid.Parse(document.RootElement.GetProperty("run_id").GetString()!);
		Guid jobId = Guid.Parse(document.RootElement.GetProperty("job_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand jobQuery = new(
			"SELECT job_type, state, payload::text FROM jobs WHERE id = $1 AND run_id = $2", connection);
		jobQuery.Parameters.AddWithValue(jobId);
		jobQuery.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await jobQuery.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal("tool-install", reader.GetString(0));
		Assert.Equal("queued", reader.GetString(1));
		string payload = reader.GetString(2);
		Assert.Contains("\"local-repository\"", payload, StringComparison.Ordinal);
		Assert.Contains("vcf-download-tool-1.0", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PostUpload_WithOperatorRole_StagesBothFilesAndQueuesAnUploadSourcedJob()
	{
		using MultipartFormDataContent form = new();
		ByteArrayContent artifact = new([1, 2, 3, 4]);
		artifact.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		form.Add(artifact, "artifact", "vcf-download-tool");
		ByteArrayContent signature = new([9, 9]);
		signature.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		form.Add(signature, "signature", "vcf-download-tool.sig");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/upload") { Content = form };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid jobId = Guid.Parse(document.RootElement.GetProperty("job_id").GetString()!);

		// Both the artifact and its detached signature were staged (as a generated name
		// plus that name + ".sig"), and the queued payload points at the staged name
		// with source "upload".
		string[] stagedFiles = Directory.GetFiles(_uploadStagingPath);
		Assert.Equal(2, stagedFiles.Length);
		string stagedArtifact = stagedFiles.Single(file => !file.EndsWith(".sig", StringComparison.Ordinal));
		Assert.Equal(stagedArtifact + ".sig", stagedFiles.Single(file => file.EndsWith(".sig", StringComparison.Ordinal)));

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand payloadQuery = new("SELECT payload::text FROM jobs WHERE id = $1", connection);
		payloadQuery.Parameters.AddWithValue(jobId);
		string payload = (string)(await payloadQuery.ExecuteScalarAsync())!;
		Assert.Contains("\"upload\"", payload, StringComparison.Ordinal);
		Assert.Contains(Path.GetFileName(stagedArtifact), payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PostUpload_MissingSignature_Returns400()
	{
		using MultipartFormDataContent form = new();
		ByteArrayContent artifact = new([1, 2, 3, 4]);
		artifact.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		form.Add(artifact, "artifact", "vcf-download-tool");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/upload") { Content = form };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(Directory.GetFiles(_uploadStagingPath));
	}

	[Fact]
	public async Task PostFetch_ConnectedWithOperatorRole_QueuesOneDepotSourcedJob()
	{
		await SetModeAsync("connected");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/fetch")
		{
			Content = JsonBody(new { version = "1.4.2" }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid jobId = Guid.Parse(document.RootElement.GetProperty("job_id").GetString()!);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand payloadQuery = new("SELECT payload::text FROM jobs WHERE id = $1", connection);
		payloadQuery.Parameters.AddWithValue(jobId);
		string payload = (string)(await payloadQuery.ExecuteScalarAsync())!;
		Assert.Contains("\"depot\"", payload, StringComparison.Ordinal);
		Assert.Contains("1.4.2", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PostFetch_Disconnected_Returns409_NoJobQueued()
	{
		await SetModeAsync("disconnected");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/fetch") { Content = JsonBody(new { }) };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("mode_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand count = new("SELECT count(*) FROM jobs WHERE job_type = 'tool-install'", connection);
		Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PostFetch_BelowOperator_Returns403()
	{
		await SetModeAsync("connected");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/fetch") { Content = JsonBody(new { }) };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private async Task SetModeAsync(string mode)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE appliance_state SET mode = $1 WHERE id = 1", connection);
		command.Parameters.AddWithValue(mode);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task GetInstalls_ReturnsLedgerNewestFirst_IncludingRejectedAttempts()
	{
		ManagedToolInstallRepository ledger = new(_fixture.ConnectionString);
		await ledger.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "good-1.0", "1.0", "sha-good",
				ManagedToolInstallOutcomes.Installed, null, "tester", null),
			CancellationToken.None);
		await Task.Delay(10);
		await ledger.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.Upload, "bad-upload", null, "sha-bad",
				ManagedToolInstallOutcomes.Rejected, "Signature does not match the Broadcom release public key.", "tester", null),
			CancellationToken.None);

		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/downloads/tool/installs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(2, document.RootElement.GetArrayLength());
		JsonElement newest = document.RootElement[0];
		Assert.Equal("rejected", newest.GetProperty("outcome").GetString());
		Assert.Equal("Signature does not match the Broadcom release public key.", newest.GetProperty("rejected_reason").GetString());
		Assert.Equal("installed", document.RootElement[1].GetProperty("outcome").GetString());
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
