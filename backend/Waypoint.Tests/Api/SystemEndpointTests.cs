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
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.SystemState;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for <c>GET /system</c> (issue #226): role gate and response shape,
/// exercised against fakes -- the real Postgres-backed <c>appliance_state</c> read is
/// covered separately in <c>Infrastructure/Postgres/SystemApiTests.cs</c>, and the
/// real filesystem disk-usage computation in
/// <c>ArtifactStoreDiskUsageProviderTests</c>.
/// </summary>
public sealed class SystemEndpointTests : IClassFixture<SystemTestApiFactory>
{
	private readonly SystemTestApiFactory _factory;

	public SystemEndpointTests(SystemTestApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task Get_WithoutAuth_Returns401()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await client.GetAsync("/api/v1/system");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Get_WithViewerRole_ReturnsOkWithVersionModeAndStores()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", "abc123", "connected", "idle", null));
		_factory.DiskUsage.Reset(new ArtifactStoreUsage("Artifact store", "/var/lib/waypoint/artifacts", 1000, 400, 600));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;

		Assert.Equal("1.2.3", root.GetProperty("version").GetString());
		Assert.Equal("connected", root.GetProperty("mode").GetString());
		// WaypointJsonOptions omits null properties entirely (WhenWritingNull) --
		// "no update available" is the absence of the field, not a JSON null.
		Assert.False(root.TryGetProperty("update_available", out _));

		JsonElement store = root.GetProperty("stores")[0];
		Assert.Equal("Artifact store", store.GetProperty("name").GetString());
		Assert.Equal("/var/lib/waypoint/artifacts", store.GetProperty("path").GetString());
		Assert.Equal(1000, store.GetProperty("total_bytes").GetInt64());
		Assert.Equal(400, store.GetProperty("used_bytes").GetInt64());
		Assert.Equal(600, store.GetProperty("free_bytes").GetInt64());
	}

	/// <summary>
	/// Issue #241: api-contract.md's "uptime" field, deferred out of #226/#240 until a
	/// consumer existed. Pinned through the fake provider rather than asserting against
	/// a moving wall clock.
	/// </summary>
	[Fact]
	public async Task Get_ReturnsUptimeSecondsFromProvider()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.Uptime.Reset(TimeSpan.FromSeconds(12345));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal(12345, doc.RootElement.GetProperty("uptime_seconds").GetInt64());
	}

	/// <summary>
	/// Issue #241: api-contract.md's "depot sync" field, derived from the most recent
	/// completed <c>catalog-index</c> run. A fresh appliance with no such run reports
	/// the field absent, not an error -- same "graceful empty state" convention as the
	/// no-stores/no-runners cases below.
	/// </summary>
	[Fact]
	public async Task Get_NoCatalogIndexRunEverCompleted_OmitsDepotSyncField()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.DepotSync.Reset(null);

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.False(doc.RootElement.TryGetProperty("depot_sync", out _));
	}

	/// <summary>Issue #241: a completed catalog-index run reports its completion time and outcome.</summary>
	[Fact]
	public async Task Get_WithCompletedCatalogIndexRun_ReturnsDepotSync()
	{
		DateTimeOffset completedAt = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.DepotSync.Reset(new DepotSyncStatus(completedAt, Succeeded: true));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement depotSync = doc.RootElement.GetProperty("depot_sync");
		Assert.Equal(completedAt, depotSync.GetProperty("last_sync_at").GetDateTimeOffset());
		Assert.True(depotSync.GetProperty("succeeded").GetBoolean());
	}

	[Fact]
	public async Task Get_ResponseBody_UsesSnakeCaseFieldNames()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", "abc123", "connected", "idle", "1.3.0"));
		_factory.DiskUsage.Reset(new ArtifactStoreUsage("Artifact store", "/var/lib/waypoint/artifacts", 1000, 400, 600));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);
		string body = await response.Content.ReadAsStringAsync();

		Assert.Contains("\"update_available\"", body);
		Assert.Contains("\"total_bytes\"", body);
		Assert.DoesNotContain("\"UpdateAvailable\"", body);
		Assert.DoesNotContain("\"TotalBytes\"", body);
	}

	[Fact]
	public async Task Get_NoStoresConfigured_ReturnsEmptyStoresArray_NotAnError()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "disconnected", "idle", null));
		_factory.DiskUsage.Reset();

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal(0, doc.RootElement.GetProperty("stores").GetArrayLength());
	}

	/// <summary>
	/// Issue #443: no worker has ever heartbeated -- an empty, not missing, "runners"
	/// array, same "graceful empty state, not an error" shape as the no-stores case
	/// above.
	/// </summary>
	[Fact]
	public async Task Get_NoRunnersRegistered_ReturnsEmptyRunnersArray_NotAnError()
	{
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.WorkerRegistry.Reset();

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal(0, doc.RootElement.GetProperty("runners").GetArrayLength());
	}

	/// <summary>
	/// Issue #443 AC: "GET /system distinguishes API health from compliance/download
	/// runner availability". A fresh heartbeat reports available; ready=false reports
	/// unavailable even though the row is fresh -- these are independent signals.
	/// </summary>
	[Fact]
	public async Task Get_WithFreshHeartbeats_ReportsAvailabilityFromReadyFlag()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.WorkerRegistry.Reset(
			new WorkerHeartbeat("compliance-runner-1", ["discover", "credential-test", "scan"], Ready: true, LastSeenAt: now, StarvedJobTypes: []),
			new WorkerHeartbeat("download-runner-1", ["catalog-index", "download"], Ready: false, LastSeenAt: now, StarvedJobTypes: []));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement[] runners = [.. doc.RootElement.GetProperty("runners").EnumerateArray()];

		Assert.Equal(2, runners.Length);
		JsonElement compliance = Assert.Single(runners, r => r.GetProperty("worker_id").GetString() == "compliance-runner-1");
		Assert.True(compliance.GetProperty("available").GetBoolean());
		JsonElement download = Assert.Single(runners, r => r.GetProperty("worker_id").GetString() == "download-runner-1");
		Assert.False(download.GetProperty("available").GetBoolean());
	}

	/// <summary>
	/// Issue #443 AC: "Stopping one runner affects only its domain and is visible via
	/// GET /system" -- a stale heartbeat (older than WorkerRegistryOptions.StaleAfter)
	/// reports unavailable even though the row's own Ready flag says true, because a
	/// stopped process can no longer update that flag.
	/// </summary>
	[Fact]
	public async Task Get_WithStaleHeartbeat_ReportsUnavailableRegardlessOfReadyFlag()
	{
		DateTimeOffset longAgo = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
		_factory.ApplianceState.Reset(new ApplianceState("1.2.3", null, "connected", "idle", null));
		_factory.DiskUsage.Reset();
		_factory.WorkerRegistry.Reset(
			new WorkerHeartbeat("compliance-runner-1", ["discover", "credential-test", "scan"], Ready: true, LastSeenAt: longAgo, StarvedJobTypes: []));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/system");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement runner = Assert.Single(doc.RootElement.GetProperty("runners").EnumerateArray());
		Assert.False(runner.GetProperty("available").GetBoolean());
	}
}

/// <summary>Test host wiring fakes for all three /system dependencies behind role-header auth.</summary>
public sealed class SystemTestApiFactory : WaypointApiFactory
{
	public FakeApplianceStateRepository ApplianceState { get; } = new();
	public FakeArtifactStoreDiskUsageProvider DiskUsage { get; } = new();
	public FakeWorkerRegistryReader WorkerRegistry { get; } = new();
	public FakeApplianceUptimeProvider Uptime { get; } = new();
	public FakeDepotSyncStatusRepository DepotSync { get; } = new();

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

			ReplaceSingleton<IApplianceStateRepository>(services, ApplianceState);
			ReplaceSingleton<IArtifactStoreDiskUsageProvider>(services, DiskUsage);
			ReplaceSingleton<IWorkerRegistryReader>(services, WorkerRegistry);
			ReplaceSingleton<IApplianceUptimeProvider>(services, Uptime);
			ReplaceSingleton<IDepotSyncStatusRepository>(services, DepotSync);
		});
	}

	private static void ReplaceSingleton<TService>(IServiceCollection services, TService instance)
		where TService : class
	{
		ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
		if (descriptor is not null)
		{
			services.Remove(descriptor);
		}

		services.AddSingleton(instance);
	}
}

/// <summary>Minimal in-memory fake for controller-level tests.</summary>
public sealed class FakeApplianceStateRepository : IApplianceStateRepository
{
	private ApplianceState? _state = new("0.0.0-dev", null, "connected", "idle", null);

	public void Reset(ApplianceState? state) => _state = state;

	public Task<ApplianceState?> GetAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_state);
	}
}

/// <summary>Minimal in-memory fake for controller-level tests.</summary>
public sealed class FakeArtifactStoreDiskUsageProvider : IArtifactStoreDiskUsageProvider
{
	private ArtifactStoreUsage[] _stores = [];

	public void Reset(params ArtifactStoreUsage[] stores) => _stores = stores;

	public IReadOnlyList<ArtifactStoreUsage> GetUsage() => _stores;
}

/// <summary>Minimal in-memory fake for controller-level tests (issue #443).</summary>
public sealed class FakeWorkerRegistryReader : IWorkerRegistryReader
{
	private WorkerHeartbeat[] _heartbeats = [];

	public void Reset(params WorkerHeartbeat[] heartbeats) => _heartbeats = heartbeats;

	public Task<IReadOnlyList<WorkerHeartbeat>> ListAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult<IReadOnlyList<WorkerHeartbeat>>(_heartbeats);
	}
}

/// <summary>Minimal in-memory fake for controller-level tests (issue #241).</summary>
public sealed class FakeApplianceUptimeProvider : IApplianceUptimeProvider
{
	private TimeSpan _uptime = TimeSpan.Zero;

	public void Reset(TimeSpan uptime) => _uptime = uptime;

	public TimeSpan GetUptime() => _uptime;
}

/// <summary>Minimal in-memory fake for controller-level tests (issue #241).</summary>
public sealed class FakeDepotSyncStatusRepository : IDepotSyncStatusRepository
{
	private DepotSyncStatus? _status;

	public void Reset(DepotSyncStatus? status) => _status = status;

	public Task<DepotSyncStatus?> GetLastSyncAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_status);
	}
}
