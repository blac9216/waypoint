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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Capacity;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Capacity;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Infrastructure.Postgres;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api.Controllers;

/// <summary>
/// Issue #1531 (epic #1180, split from #1042): <c>POST /downloads/binaries</c>'s
/// disk-admission wiring end to end -- admits a request that fits under free bytes
/// minus the configured reserve, refuses one that would breach it (409
/// <c>disk_admission_denied</c>, naming projected/free/reserve/shortfall bytes, no
/// run created), and a zero-byte reserve still admits exactly up to free bytes. Uses
/// a fake <see cref="IArtifactStoreDiskUsageProvider"/> for a deterministic free-bytes
/// figure -- no dependency on the real disk's actual free space -- against the real
/// #1529/#1531 repository and service wired the same way production DI wires them.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class DownloadsControllerAdmissionTests : IAsyncLifetime
{
	private const string StoreName = ArtifactStoreNames.Default;
	private const long FreeBytes = 1_000L;

	private sealed class FakeDiskUsageProvider : IArtifactStoreDiskUsageProvider
	{
		public IReadOnlyList<ArtifactStoreUsage> GetUsage() =>
			[new ArtifactStoreUsage(StoreName, "/fake-store", TotalBytes: FreeBytes * 2, UsedBytes: FreeBytes, FreeBytes)];
	}

	private sealed class DownloadsAdmissionApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public DownloadsAdmissionApiFactory(string connectionString) => _connectionString = connectionString;

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
				JobQueueRepository jobs = new(_connectionString, NullLogger<JobQueueRepository>.Instance);
				services.AddSingleton<IJobControlRepository>(jobs);
				services.AddSingleton<IJobRunnerRepository>(jobs);
				services.AddSingleton(new CredentialRepository(_connectionString));
				services.AddSingleton<IWorkerRegistryReader>(new WorkerRegistryRepository(_connectionString));

				// The fake replaces the unconditionally-registered real provider so
				// FreeBytes is deterministic; the policy repository is the real #1529
				// implementation against this fixture's connection string, so PUT
				// /disk-admission-policy's effect on the very next admission call is
				// exercised for real, not faked away.
				services.AddSingleton<IArtifactStoreDiskUsageProvider>(new FakeDiskUsageProvider());
				services.AddSingleton<IDiskAdmissionPolicyRepository>(new DiskAdmissionPolicyRepository(_connectionString));
				services.AddSingleton<IDiskAdmissionService>(serviceProvider => new DiskAdmissionService(
					serviceProvider.GetRequiredService<IArtifactStoreDiskUsageProvider>(),
					serviceProvider.GetRequiredService<IDiskAdmissionPolicyRepository>()));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private DownloadsAdmissionApiFactory _factory = null!;
	private HttpClient _client = null!;
	private DepotArtifactRepository _artifacts = null!;
#pragma warning restore CA1001

	public DownloadsControllerAdmissionTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		await SetReserveBytesAsync(0);

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_factory = new DownloadsAdmissionApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task PostBinariesDownload_ProjectedWithinFreeMinusReserve_Admits()
	{
		await SetReserveBytesAsync(100);
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag, sizeBytes: FreeBytes - 100);

		HttpResponseMessage response = await PostBinariesAsync(artifact);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
	}

	[Fact]
	public async Task PostBinariesDownload_ProjectedWouldBreachReserve_Returns409AndCreatesNoRun()
	{
		await SetReserveBytesAsync(100);
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag, sizeBytes: FreeBytes - 50);

		HttpResponseMessage response = await PostBinariesAsync(artifact);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement error = document.RootElement.GetProperty("error");
		Assert.Equal("disk_admission_denied", error.GetProperty("code").GetString());
		Assert.Contains("Shortfall bytes: 50", error.GetProperty("detail").GetString());
		Assert.Equal(0L, await GetRunCountAsync());
	}

	/// <summary>Issue #1531 AC: a zero reserve still admits up to exactly free bytes.</summary>
	[Fact]
	public async Task PostBinariesDownload_ZeroReservePolicy_AdmitsUpToFreeBytesExactly()
	{
		await SetReserveBytesAsync(0);
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag, sizeBytes: FreeBytes);

		HttpResponseMessage response = await PostBinariesAsync(artifact);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
	}

	[Fact]
	public async Task PostBinariesDownload_ZeroReservePolicy_DeniesOneByteOverFree()
	{
		await SetReserveBytesAsync(0);
		string tag = Guid.NewGuid().ToString("N");
		Guid artifact = await SeedArtifactAsync(tag, sizeBytes: FreeBytes + 1);

		HttpResponseMessage response = await PostBinariesAsync(artifact);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	private async Task<HttpResponseMessage> PostBinariesAsync(Guid artifact)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/binaries")
		{
			Content = JsonBody(new { depot_artifact_ids = new[] { artifact.ToString() } }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");
		return await _client.SendAsync(request);
	}

	private async Task<Guid> SeedArtifactAsync(string externalIdTag, long sizeBytes) => await _artifacts.UpsertAsync(
		new DepotArtifactUpsert(
			externalIdTag, "0000000000000000000000000000000000000000000000000000000000000000", "indexed", "{}",
			SizeBytes: sizeBytes, BundleId: $"bundle-{externalIdTag}"),
		CancellationToken.None);

	private async Task SetReserveBytesAsync(long reserveBytes)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"UPDATE disk_admission_policy SET reserve_bytes = $1, updated_by = NULL WHERE id = 1", connection);
		command.Parameters.AddWithValue(reserveBytes);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<long> GetRunCountAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("SELECT count(*) FROM runs", connection);
		return (long)(await command.ExecuteScalarAsync())!;
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
