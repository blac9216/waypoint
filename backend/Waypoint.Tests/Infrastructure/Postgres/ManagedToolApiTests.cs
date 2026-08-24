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
/// job in its own run, upload staging (artifact plus published checksum land in the
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
	public async Task PostUpload_WithOperatorRole_StagesArtifactAndQueuesChecksummedUploadJob()
	{
		using MultipartFormDataContent form = new();
		ByteArrayContent artifact = new([1, 2, 3, 4]);
		artifact.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		form.Add(artifact, "artifact", "vcf-download-tool");
		form.Add(new StringContent("9F64A747E1B97F131FABB6B447296C9B6F0201E79FB3C5356E6C77E89B6A806A"), "sha256");

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/upload") { Content = form };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid jobId = Guid.Parse(document.RootElement.GetProperty("job_id").GetString()!);

		// Only the artifact is staged; the normalized published checksum travels in the
		// immutable queued payload for the download runner to verify.
		string[] stagedFiles = Directory.GetFiles(_uploadStagingPath);
		string stagedArtifact = Assert.Single(stagedFiles);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand payloadQuery = new("SELECT payload::text FROM jobs WHERE id = $1", connection);
		payloadQuery.Parameters.AddWithValue(jobId);
		string payload = (string)(await payloadQuery.ExecuteScalarAsync())!;
		Assert.Contains("\"upload\"", payload, StringComparison.Ordinal);
		Assert.Contains(Path.GetFileName(stagedArtifact), payload, StringComparison.Ordinal);
		Assert.Contains("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PostUpload_MissingChecksum_Returns400()
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

	/// <summary>
	/// Issue #645: a permanent in-process regression proof that a real >128 MiB
	/// multipart upload reaches <c>ManagedToolController.Upload</c> and succeeds --
	/// not just that the <c>[RequestSizeLimit]</c>/<c>[RequestFormLimits]</c> attribute
	/// values agree (<see cref="Waypoint.Tests.Api.ManagedToolUploadLimitsTests"/>
	/// covers that, fast and reflection-only, but would pass even if new middleware
	/// read <c>Request.Form</c> before MVC or the binding switched away from
	/// <c>IFormFile</c> in a way <c>[RequestFormLimits]</c> does not govern). This
	/// drives the real pipeline (MVC model binding, the actual
	/// <c>[RequestSizeLimit]</c>/<c>[RequestFormLimits]</c> attributes on
	/// <c>ManagedToolController.Upload</c>, real multipart parsing) end to end and
	/// asserts 202, not the pre-fix "Multipart body length limit 134217728 exceeded"
	/// 400. The artifact content is generated on the fly by
	/// <see cref="SyntheticContentStream"/> -- a >128 MiB payload is never allocated
	/// as one in-memory buffer, so this stays fast (~1s) despite the size.
	/// </summary>
	[Fact]
	public async Task PostUpload_ArtifactOver128MiB_Returns202_NotTheMultipartBodyLengthLimitRegression()
	{
		const long ArtifactSize = (128L * 1024 * 1024) + (16L * 1024 * 1024); // 128 MiB default limit + 16 MiB headroom

		using MultipartFormDataContent form = new();
		using StreamContent artifact = new(new SyntheticContentStream(ArtifactSize));
		artifact.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		form.Add(artifact, "artifact", "vcf-download-tool-large");
		form.Add(new StringContent(new string('a', 64)), "sha256"); // shape only -- this test proves size, not checksum enforcement

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/tool/upload") { Content = form };
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await _client.SendAsync(request);
		string body = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.DoesNotContain("Multipart body length limit", body, StringComparison.Ordinal);

		string[] stagedFiles = Directory.GetFiles(_uploadStagingPath);
		string stagedArtifact = Assert.Single(stagedFiles);
		Assert.Equal(ArtifactSize, new FileInfo(stagedArtifact).Length);
	}

	/// <summary>
	/// A read-only <see cref="Stream"/> that reports and yields exactly
	/// <paramref name="length"/> deterministic bytes without ever allocating them all
	/// at once -- issue #645's "streamed, not allocated or checked into the repo"
	/// acceptance criterion. Each <see cref="ReadAsync(Memory{byte},CancellationToken)"/>
	/// call fills the caller's buffer from a small repeating pattern.
	/// </summary>
	private sealed class SyntheticContentStream : Stream
	{
		private readonly long _length;
		private long _remaining;

		public SyntheticContentStream(long length)
		{
			_length = length;
			_remaining = length;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => _length;

		public override long Position
		{
			get => _length - _remaining;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_remaining <= 0)
			{
				return ValueTask.FromResult(0);
			}

			int toWrite = (int)Math.Min(buffer.Length, _remaining);
			for (int i = 0; i < toWrite; i++)
			{
				buffer.Span[i] = (byte)(i % 251);
			}

			_remaining -= toWrite;
			return ValueTask.FromResult(toWrite);
		}

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
