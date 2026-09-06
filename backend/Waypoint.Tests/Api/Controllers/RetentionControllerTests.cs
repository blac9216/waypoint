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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Infrastructure.Postgres;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api.Controllers;

/// <summary>
/// <c>RetentionController</c> (issue #1453, epic #1182) end to end against real
/// Postgres and the real #1406/#1436/#1440 domain services -- RBAC matrix, pin/unpin/
/// purge-now/dial/review-list happy paths, the per-file logged deletion trail, a
/// beyond-page-size paging test (#1479's lesson), and the 409-not-500 transition-guard
/// mapping.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class RetentionControllerTests : IAsyncLifetime, IDisposable
{
	private sealed class RecordingEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class RetentionApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _depotRoot;

		public CapturingLogger<RetentionSweepService> SweepLogger { get; } = new();
		public CapturingLogger<ReviewListDeletionService> DeletionLogger { get; } = new();

		public RetentionApiFactory(string connectionString, string depotRoot)
		{
			_connectionString = connectionString;
			_depotRoot = depotRoot;
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

				IOptions<CatalogOptions> catalogOptions = Options.Create(new CatalogOptions { DepotPath = _depotRoot });

				services.AddSingleton<IJobEventPublisher>(new RecordingEventPublisher());
				services.AddSingleton<IDepotArtifactRepository>(new DepotArtifactRepository(_connectionString));
				services.AddSingleton<IUnknownCatalogFileRepository>(serviceProvider => new UnknownCatalogFileRepository(
					_connectionString, serviceProvider.GetRequiredService<IJobEventPublisher>()));
				services.AddSingleton<IRetainedContentStateRepository>(new RetainedContentStateRepository(_connectionString));
				services.AddSingleton<IRetentionPolicyRepository>(new RetentionPolicyRepository(_connectionString));
				services.AddSingleton<IReviewListService>(serviceProvider => new ReviewListService(
					_connectionString,
					serviceProvider.GetRequiredService<IUnknownCatalogFileRepository>(),
					serviceProvider.GetRequiredService<IDepotArtifactRepository>(),
					serviceProvider.GetRequiredService<IJobEventPublisher>()));
				services.AddSingleton(catalogOptions);
				services.AddSingleton<ILogger<RetentionSweepService>>(SweepLogger);
				services.AddSingleton<ILogger<ReviewListDeletionService>>(DeletionLogger);
				services.AddSingleton<IRetentionSweepService>(serviceProvider => new RetentionSweepService(
					serviceProvider.GetRequiredService<IRetainedContentStateRepository>(),
					serviceProvider.GetRequiredService<IRetentionPolicyRepository>(),
					serviceProvider.GetRequiredService<IDepotArtifactRepository>(),
					serviceProvider.GetRequiredService<IJobEventPublisher>(),
					catalogOptions,
					SweepLogger));
				services.AddSingleton<IReviewListDeletionService>(serviceProvider => new ReviewListDeletionService(
					_connectionString,
					serviceProvider.GetRequiredService<IRetainedContentStateRepository>(),
					serviceProvider.GetRequiredService<IRetentionSweepService>(),
					catalogOptions,
					DeletionLogger));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _depotRoot = Directory.CreateTempSubdirectory("waypoint-retention-api-test-").FullName;
	private RetentionApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public RetentionControllerTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();

		_factory = new RetentionApiFactory(_fixture.ConnectionString, _depotRoot);
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
		try
		{
			Directory.Delete(_depotRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup; a stray temp dir does not fail the test run.
		}
	}

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		foreach (string table in new[]
		{
			"download_out_of_scope_content", "download_retained_content_state", "unknown_catalog_files", "depot_artifacts"
		})
		{
			await using NpgsqlCommand delete = new($"DELETE FROM {table}", connection);
			await delete.ExecuteNonQueryAsync();
		}
	}

	private async Task<Guid> InsertDepotArtifactAsync(string relativePath)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO depot_artifacts (relative_path) VALUES ($1) RETURNING id", connection);
		command.Parameters.AddWithValue(relativePath);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<Guid> TrackAsync(Guid depotArtifactId)
	{
		RetainedContentStateRepository states = new(_fixture.ConnectionString);
		return await states.EnsureTrackedAsync(depotArtifactId, policyId: null, CancellationToken.None);
	}

	private void WriteDepotFile(string relativePath)
	{
		string fullPath = Path.Combine(_depotRoot, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllText(fullPath, "test");
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		if (role is not null)
		{
			request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		}
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}
		return await _client.SendAsync(request);
	}

	// ---- RBAC matrix ---------------------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task EveryMutatingEndpoint_BelowAdmin_Returns403(string role)
	{
		Guid artifactId = await InsertDepotArtifactAsync($"role-gate/{role}.iso");
		Guid stateId = await TrackAsync(artifactId);

		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/pin", role, new { })).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/unpin", role, null)).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", role, new { })).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/api/v1/download-retention/dial", role, new { dial = "keep" })).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Delete, "/api/v1/download-retention/review-list", role, new { kind = "OutOfScope", depot_artifact_id = artifactId })).StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task EveryReadEndpoint_AnyRole_Returns200(string role)
	{
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state", role, null)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/download-retention/dial", role, null)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/download-retention/review-list", role, null)).StatusCode);
	}

	// ---- Happy paths -----------------------------------------------------------

	[Fact]
	public async Task Pin_ThenUnpin_RoundTrips()
	{
		Guid artifactId = await InsertDepotArtifactAsync("pin-roundtrip.iso");
		Guid stateId = await TrackAsync(artifactId);

		HttpResponseMessage pin = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/pin", "Admin", new { note = "keep for audit" });
		Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
		using JsonDocument pinBody = JsonDocument.Parse(await pin.Content.ReadAsStringAsync());
		Assert.Equal(RetainedContentStates.Pinned, pinBody.RootElement.GetProperty("state").GetString());

		HttpResponseMessage unpin = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/unpin", "Admin", null);
		Assert.Equal(HttpStatusCode.OK, unpin.StatusCode);
		using JsonDocument unpinBody = JsonDocument.Parse(await unpin.Content.ReadAsStringAsync());
		Assert.Equal(RetainedContentStates.Tracked, unpinBody.RootElement.GetProperty("state").GetString());
	}

	[Fact]
	public async Task PurgeNow_DeletesTheFileAndLogsTheTrail()
	{
		Guid artifactId = await InsertDepotArtifactAsync("purge-now/target.iso");
		Guid stateId = await TrackAsync(artifactId);
		WriteDepotFile("purge-now/target.iso");

		HttpResponseMessage purge = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", "Admin", new { reason = "operator requested" });
		Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
		Assert.True(body.RootElement.GetProperty("purged").GetBoolean());

		Assert.False(File.Exists(Path.Combine(_depotRoot, "purge-now/target.iso")));
		CapturedLogEntry trail = _factory.SweepLogger.OnlyEntryAt(LogLevel.Information);
		Assert.Contains(artifactId.ToString(), trail.Message, StringComparison.Ordinal);
		Assert.Contains("test-user", trail.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PurgeNow_AlreadyPinned_Returns409()
	{
		Guid artifactId = await InsertDepotArtifactAsync("purge-pinned.iso");
		Guid stateId = await TrackAsync(artifactId);
		RetainedContentStateRepository states = new(_fixture.ConnectionString);
		await states.PinAsync(stateId, "someone", null, CancellationToken.None);

		HttpResponseMessage purge = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", "Admin", new { });
		Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
	}

	/// <summary>Transition-guard error mapping: pinning a row CanPin rejects is a documented 409, never an unmapped 500.</summary>
	[Fact]
	public async Task Pin_AlreadyPurged_Returns409NotAnUnmapped500()
	{
		Guid artifactId = await InsertDepotArtifactAsync("pin-after-purge.iso");
		Guid stateId = await TrackAsync(artifactId);
		WriteDepotFile("pin-after-purge.iso");
		await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", "Admin", new { });

		HttpResponseMessage pin = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/pin", "Admin", new { });

		Assert.Equal(HttpStatusCode.Conflict, pin.StatusCode);
		string body = await pin.Content.ReadAsStringAsync();
		Assert.Contains("illegal_transition", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Dial_GetThenSet_RoundTrips()
	{
		HttpResponseMessage initial = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/dial", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

		HttpResponseMessage set = await SendAsync(HttpMethod.Put, "/api/v1/download-retention/dial", "Admin", new { dial = ManualDownloadDialOptions.Review });
		Assert.Equal(HttpStatusCode.OK, set.StatusCode);
		using JsonDocument setBody = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
		Assert.Equal(ManualDownloadDialOptions.Review, setBody.RootElement.GetProperty("dial").GetString());

		HttpResponseMessage get = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/dial", "Viewer", null);
		using JsonDocument getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal(ManualDownloadDialOptions.Review, getBody.RootElement.GetProperty("dial").GetString());
	}

	[Fact]
	public async Task ReviewList_DeleteOutOfScopeEntry_PurgesFileAndRemovesFromList()
	{
		Guid artifactId = await InsertDepotArtifactAsync("review-list/out-of-scope.iso");
		WriteDepotFile("review-list/out-of-scope.iso");
		IReviewListService reviewList = _factory.Services.GetRequiredService<IReviewListService>();
		await reviewList.ReportOutOfScopeAsync(artifactId, "no subscription references this", CancellationToken.None);

		HttpResponseMessage delete = await SendAsync(
			HttpMethod.Delete, "/api/v1/download-retention/review-list", "Admin",
			new { kind = "OutOfScope", depot_artifact_id = artifactId, reason = "confirmed retired" });

		Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
		Assert.True(body.RootElement.GetProperty("deleted").GetBoolean());
		Assert.False(File.Exists(Path.Combine(_depotRoot, "review-list/out-of-scope.iso")));

		IReadOnlyList<ReviewListEntry> remaining = await reviewList.ListAsync(CancellationToken.None);
		Assert.Empty(remaining);
		CapturedLogEntry trail = _factory.DeletionLogger.OnlyEntryAt(LogLevel.Information);
		Assert.Contains("out-of-scope", trail.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReviewList_DeleteOrphanEntry_DeletesFileAndRemovesFromList()
	{
		WriteDepotFile("uploads/mystery.iso");
		IUnknownCatalogFileRepository unknownFiles = _factory.Services.GetRequiredService<IUnknownCatalogFileRepository>();
		await unknownFiles.RecordSeenAsync("uploads/mystery.iso", 4, CancellationToken.None);

		HttpResponseMessage delete = await SendAsync(
			HttpMethod.Delete, "/api/v1/download-retention/review-list", "Admin",
			new { kind = "Orphan", relative_path = "uploads/mystery.iso" });

		Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
		Assert.False(File.Exists(Path.Combine(_depotRoot, "uploads/mystery.iso")));
		CapturedLogEntry trail = _factory.DeletionLogger.OnlyEntryAt(LogLevel.Information);
		Assert.Contains("orphan", trail.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReviewList_DeleteEntryNotOnList_Returns404()
	{
		HttpResponseMessage delete = await SendAsync(
			HttpMethod.Delete, "/api/v1/download-retention/review-list", "Admin",
			new { kind = "Orphan", relative_path = "never-reported.iso" });

		Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
	}

	/// <summary>Issue #1479's lesson: any fan-out list endpoint needs a beyond-page-size test.</summary>
	[Fact]
	public async Task ListState_MoreRowsThanOnePage_PagesAndReportsTotalCount()
	{
		const int totalRows = 205;
		for (int i = 0; i < totalRows; i++)
		{
			Guid artifactId = await InsertDepotArtifactAsync($"paging/artifact-{i:D4}.iso");
			Guid stateId = await TrackAsync(artifactId);
			RetainedContentStateRepository states = new(_fixture.ConnectionString);
			await states.TransitionAsync(stateId, RetainedContentStates.Grace, CancellationToken.None);
		}

		HttpResponseMessage firstPage = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?limit=200&offset=0", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
		Assert.Equal(totalRows.ToString(System.Globalization.CultureInfo.InvariantCulture), firstPage.Headers.GetValues("X-Total-Count").Single());
		using JsonDocument firstBody = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
		Assert.Equal(200, firstBody.RootElement.GetArrayLength());

		HttpResponseMessage secondPage = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?limit=200&offset=200", "Viewer", null);
		using JsonDocument secondBody = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
		Assert.Equal(5, secondBody.RootElement.GetArrayLength());
	}
}
