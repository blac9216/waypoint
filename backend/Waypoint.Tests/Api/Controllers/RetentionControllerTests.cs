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
				services.AddSingleton<IOutOfScopeContentEraser>(serviceProvider =>
					(IOutOfScopeContentEraser)serviceProvider.GetRequiredService<IReviewListService>());
				services.AddSingleton(catalogOptions);
				services.AddSingleton<ILogger<RetentionSweepService>>(SweepLogger);
				services.AddSingleton<ILogger<ReviewListDeletionService>>(DeletionLogger);
				services.AddSingleton<IRetentionSweepService>(serviceProvider => new RetentionSweepService(
					serviceProvider.GetRequiredService<IRetainedContentStateRepository>(),
					serviceProvider.GetRequiredService<IRetentionPolicyRepository>(),
					serviceProvider.GetRequiredService<IDepotArtifactRepository>(),
					serviceProvider.GetRequiredService<IReviewListService>(),
					serviceProvider.GetRequiredService<IOutOfScopeContentEraser>(),
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

	/// <summary>
	/// F1 (round 1 review): pinning content must not make it vanish from the only
	/// listing endpoint -- ADR-0034 names "pinned, sweep will skip" as one of three
	/// states an operator must be able to tell apart, and this endpoint is the sole
	/// place #1048's UI can enumerate an id to pass back to <c>POST .../unpin</c>.
	/// </summary>
	[Fact]
	public async Task ListState_IncludesPinnedRowsWithPinMetadata()
	{
		Guid artifactId = await InsertDepotArtifactAsync("pinned-listed.iso");
		Guid stateId = await TrackAsync(artifactId);

		HttpResponseMessage pin = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/pin", "Admin", new { note = "keep for audit" });
		Assert.Equal(HttpStatusCode.OK, pin.StatusCode);

		HttpResponseMessage listAsViewer = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, listAsViewer.StatusCode);
		using JsonDocument listBody = JsonDocument.Parse(await listAsViewer.Content.ReadAsStringAsync());
		JsonElement row = listBody.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == stateId);
		Assert.Equal(RetainedContentStates.Pinned, row.GetProperty("state").GetString());
		Assert.Equal("test-user", row.GetProperty("pinned_by").GetString());
		Assert.False(string.IsNullOrEmpty(row.GetProperty("pinned_at").GetString()));
		Assert.Equal("keep for audit", row.GetProperty("pin_note").GetString());
	}

	/// <summary>F1: the optional <c>state</c> query filter narrows to exactly one of the three ADR-0034 states.</summary>
	[Fact]
	public async Task ListState_FilterByState_ReturnsOnlyThatState()
	{
		Guid graceArtifact = await InsertDepotArtifactAsync("filter-grace.iso");
		Guid graceId = await TrackAsync(graceArtifact);
		RetainedContentStateRepository states = new(_fixture.ConnectionString);
		await states.TransitionAsync(graceId, RetainedContentStates.Grace, CancellationToken.None);

		Guid pinnedArtifact = await InsertDepotArtifactAsync("filter-pinned.iso");
		Guid pinnedId = await TrackAsync(pinnedArtifact);
		await states.PinAsync(pinnedId, "someone", null, CancellationToken.None);

		HttpResponseMessage pinnedOnly = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?state=pinned", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, pinnedOnly.StatusCode);
		using JsonDocument pinnedBody = JsonDocument.Parse(await pinnedOnly.Content.ReadAsStringAsync());
		JsonElement[] pinnedRows = [.. pinnedBody.RootElement.EnumerateArray()];
		Assert.Contains(pinnedRows, e => e.GetProperty("id").GetGuid() == pinnedId);
		Assert.DoesNotContain(pinnedRows, e => e.GetProperty("id").GetGuid() == graceId);

		HttpResponseMessage invalid = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?state=tracked", "Viewer", null);
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
	}

	/// <summary>N1 (round 1 review): the already-purged branch of purge-now is a genuine 200, never turned into an error response, but the wire flag says so.</summary>
	[Fact]
	public async Task PurgeNow_AlreadyPurged_ReturnsTwoHundredWithPurgedFalseAndError()
	{
		Guid artifactId = await InsertDepotArtifactAsync("purge-now/already-purged.iso");
		Guid stateId = await TrackAsync(artifactId);
		WriteDepotFile("purge-now/already-purged.iso");

		HttpResponseMessage first = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", "Admin", new { });
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);

		HttpResponseMessage second = await SendAsync(HttpMethod.Post, $"/api/v1/download-retention/{stateId}/purge-now", "Admin", new { });
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		using JsonDocument secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
		Assert.False(secondBody.RootElement.GetProperty("purged").GetBoolean());
		Assert.Contains("already purged", secondBody.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	/// <summary>N1: an out-of-scope review-list delete whose purge leg fails (content pinned after being reported) is a 200 with <c>deleted:false</c>, not an error response, and the entry stays on the list.</summary>
	[Fact]
	public async Task ReviewList_DeleteOutOfScopeEntry_PurgeFails_ReturnsTwoHundredWithDeletedFalseAndError()
	{
		Guid artifactId = await InsertDepotArtifactAsync("review-list/pinned-out-of-scope.iso");
		WriteDepotFile("review-list/pinned-out-of-scope.iso");
		IReviewListService reviewList = _factory.Services.GetRequiredService<IReviewListService>();
		await reviewList.ReportOutOfScopeAsync(artifactId, "no subscription references this", CancellationToken.None);

		RetainedContentStateRepository states = new(_fixture.ConnectionString);
		Guid stateId = await states.EnsureTrackedAsync(artifactId, policyId: null, CancellationToken.None);
		await states.PinAsync(stateId, "someone", null, CancellationToken.None);

		HttpResponseMessage delete = await SendAsync(
			HttpMethod.Delete, "/api/v1/download-retention/review-list", "Admin",
			new { kind = "OutOfScope", depot_artifact_id = artifactId, reason = "attempted cleanup" });

		Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
		Assert.False(body.RootElement.GetProperty("deleted").GetBoolean());
		Assert.Contains("pinned", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(_depotRoot, "review-list/pinned-out-of-scope.iso")));

		IReadOnlyList<ReviewListEntry> stillListed = await reviewList.ListAsync(CancellationToken.None);
		Assert.Contains(stillListed, e => e.DepotArtifactId == artifactId);
	}

	/// <summary>N1: an orphan review-list delete whose file confinement check fails is a 200 with <c>deleted:false</c>, not an error response, and the entry stays on the list.</summary>
	[Fact]
	public async Task ReviewList_DeleteOrphanEntry_ConfinementFails_ReturnsTwoHundredWithDeletedFalseAndError()
	{
		IUnknownCatalogFileRepository unknownFiles = _factory.Services.GetRequiredService<IUnknownCatalogFileRepository>();
		const string escapingPath = "../escapes-depot-root.iso";
		await unknownFiles.RecordSeenAsync(escapingPath, 4, CancellationToken.None);

		HttpResponseMessage delete = await SendAsync(
			HttpMethod.Delete, "/api/v1/download-retention/review-list", "Admin",
			new { kind = "Orphan", relative_path = escapingPath });

		Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
		Assert.False(body.RootElement.GetProperty("deleted").GetBoolean());
		Assert.Contains("outside", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

		IReviewListService reviewList = _factory.Services.GetRequiredService<IReviewListService>();
		IReadOnlyList<ReviewListEntry> stillListed = await reviewList.ListAsync(CancellationToken.None);
		Assert.Contains(stillListed, e => e.RelativePath == escapingPath);
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

	/// <summary>Issue #1786: the default (no ?state=) listing derives from the same closed set ListableStates validates against, not a second literal that can silently drift from it.</summary>
	[Fact]
	public async Task ListState_DefaultListing_CoversExactlyGraceAndPendingPurgeAndPinned()
	{
		RetainedContentStateRepository states = new(_fixture.ConnectionString);

		Guid graceArtifact = await InsertDepotArtifactAsync("default-listing/grace.iso");
		Guid graceId = await TrackAsync(graceArtifact);
		await states.TransitionAsync(graceId, RetainedContentStates.Grace, CancellationToken.None);

		Guid pendingPurgeArtifact = await InsertDepotArtifactAsync("default-listing/pending-purge.iso");
		Guid pendingPurgeId = await TrackAsync(pendingPurgeArtifact);
		await states.TransitionAsync(pendingPurgeId, RetainedContentStates.Grace, CancellationToken.None);
		await states.TransitionAsync(pendingPurgeId, RetainedContentStates.PendingPurge, CancellationToken.None);

		Guid pinnedArtifact = await InsertDepotArtifactAsync("default-listing/pinned.iso");
		Guid pinnedId = await TrackAsync(pinnedArtifact);
		await states.PinAsync(pinnedId, "operator-1", null, CancellationToken.None);

		Guid trackedArtifact = await InsertDepotArtifactAsync("default-listing/tracked.iso");
		Guid trackedId = await TrackAsync(trackedArtifact); // NOT one of ListableStates -- must never appear

		HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state", "Viewer", null);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Guid[] ids = [.. body.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid())];

		Assert.Contains(graceId, ids);
		Assert.Contains(pendingPurgeId, ids);
		Assert.Contains(pinnedId, ids);
		Assert.DoesNotContain(trackedId, ids);
	}

	/// <summary>
	/// Issue #1787 AC: rows sharing one created_at page deterministically -- two
	/// successive pages neither repeat nor drop a row, in the exact sequence the
	/// (created_at, id) total order dictates. 20 rows, not 6: below 17 elements
	/// <c>List&lt;T&gt;.Sort</c> takes .NET's insertion-sort path, which happens to
	/// be stable, so at 6 rows all sharing one created_at the pre-#1787 code (no
	/// in-memory Id tiebreak) reproduces the SQL row order by accident and this
	/// test cannot tell it apart from the fixed code -- confirmed by probe,
	/// documented in the round-1 Fixes Applied comment. Above 16 elements the
	/// introspective-sort path is not stable for tied keys, so a comparator that
	/// ignores Id (mutation: <c>all.Sort((a, b) =&gt; a.CreatedAt.CompareTo(b.CreatedAt))</c>)
	/// measurably reorders the page split.
	/// </summary>
	[Fact]
	public async Task ListState_RowsShareOneCreatedAt_PagingNeitherRepeatsNorDropsARow()
	{
		const int totalRows = 20;
		List<Guid> stateIds = [];
		for (int i = 0; i < totalRows; i++)
		{
			Guid artifactId = await InsertDepotArtifactAsync($"tie/artifact-{i:D2}.iso");
			Guid stateId = await TrackAsync(artifactId);
			RetainedContentStateRepository states = new(_fixture.ConnectionString);
			await states.TransitionAsync(stateId, RetainedContentStates.Grace, CancellationToken.None);
			stateIds.Add(stateId);
		}

		// Force every row to share exactly one created_at -- the collision this
		// AC needs, which real concurrent inserts only produce rarely.
		DateTimeOffset sharedCreatedAt = DateTimeOffset.UtcNow;
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand update = new(
				"UPDATE download_retained_content_state SET created_at = $1 WHERE id = ANY($2)", connection);
			update.Parameters.AddWithValue(sharedCreatedAt);
			update.Parameters.AddWithValue(stateIds.ToArray());
			await update.ExecuteNonQueryAsync();
		}

		HttpResponseMessage firstPage = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?limit=10&offset=0", "Viewer", null);
		using JsonDocument firstBody = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
		Guid[] firstIds = [.. firstBody.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid())];

		HttpResponseMessage secondPage = await SendAsync(HttpMethod.Get, "/api/v1/download-retention/state?limit=10&offset=10", "Viewer", null);
		using JsonDocument secondBody = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
		Guid[] secondIds = [.. secondBody.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid())];

		Assert.Equal(10, firstIds.Length);
		Assert.Equal(10, secondIds.Length);
		Assert.Empty(firstIds.Intersect(secondIds)); // no repeat across pages

		// Assert the actual SEQUENCE the two pages concatenate to, not a
		// sorted-both-sides set: with every row sharing one created_at, the
		// controller's in-memory Id tiebreak (Comparer<Guid>.Default, the same
		// comparator RetentionController.ListState applies -- and, on this data
		// set, equivalent to Postgres's own uuid byte ordering, per the round-1
		// Fixes Applied comment's probe) is the only thing that determines this
		// order, so this goes red when that in-memory comparator is removed (the
		// SQL tiebreak is covered by the repository test, not observable here) --
		// Assert.Empty(...Intersect...) above cannot detect that because it is a
		// set operation, and comparing two independently-sorted sides never
		// examines the order either side actually produced.
		Assert.Equal(
			stateIds.OrderBy(x => x, Comparer<Guid>.Default).ToArray(),
			firstIds.Concat(secondIds).ToArray()); // no drop, in the exact page order the controller emits
	}
}
