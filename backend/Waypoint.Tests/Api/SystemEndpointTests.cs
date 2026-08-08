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
}

/// <summary>Test host wiring fakes for both /system dependencies behind role-header auth.</summary>
public sealed class SystemTestApiFactory : WaypointApiFactory
{
	public FakeApplianceStateRepository ApplianceState { get; } = new();
	public FakeArtifactStoreDiskUsageProvider DiskUsage { get; } = new();

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
