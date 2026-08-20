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
using Waypoint.Core.Authorization;
using Waypoint.Core.Users;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for /users (issue #512): Admin-only role guard on every verb, and
/// the "role is a read-only mirror" design interpretation -- PUT accepts only
/// <c>site_scope</c>, never a role field, against a fake <see cref="IUserDirectory"/>
/// (same "swap the fake repository through TestAuthHandler" shape
/// <see cref="SchedulesEndpointTests"/> uses).
/// </summary>
public sealed class UsersEndpointTests : IClassFixture<UsersTestApiFactory>
{
	private readonly UsersTestApiFactory _factory;

	public UsersEndpointTests(UsersTestApiFactory factory)
	{
		_factory = factory;
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task List_BelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/users", role, null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task List_AsAdmin_Returns200()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/users", "Admin", null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Create_AsAdmin_Returns201()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "Viewer" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task Create_WithUnrecognizedRole_Returns400()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "SuperAdmin" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	/// <summary>
	/// Issue #530: a malformed site_scope used to reach the repository's <c>::jsonb</c>
	/// cast unvalidated, so Postgres raised <c>invalid_text_representation</c> and
	/// <c>ErrorHandlingMiddleware</c> shaped that as an unmapped 500. It must be a 400
	/// validation_error instead, matching the envelope every other 400 in this
	/// controller returns.
	/// </summary>
	[Fact]
	public async Task Create_WithMalformedSiteScope_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "Viewer", site_scope = "not-json" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("validation_error", body.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	/// <summary>Issue #530: well-formed JSON that is not an array (the documented shape) is also a 400, not silently accepted.</summary>
	[Fact]
	public async Task Create_WithNonArraySiteScope_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "Viewer", site_scope = "{\"not\":\"an-array\"}" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("validation_error", body.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	/// <summary>Issue #530: valid JSON array site_scope still round-trips through Create.</summary>
	[Fact]
	public async Task Create_WithValidSiteScope_RoundTrips()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "Viewer", site_scope = "[\"site-a\",\"site-b\"]" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("[\"site-a\",\"site-b\"]", body.RootElement.GetProperty("site_scope").GetString());
	}

	/// <summary>Issue #530: an absent/blank site_scope on Create still defaults to "[]" -- empty semantics preserved.</summary>
	[Fact]
	public async Task Create_WithoutSiteScope_DefaultsToEmptyArray()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "new-hire", role = "Viewer" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("[]", body.RootElement.GetProperty("site_scope").GetString());
	}

	/// <summary>Issue #530: same malformed-JSON guard applies to PUT's UpdateSiteScopeAsync path, not just Create.</summary>
	[Fact]
	public async Task Update_WithMalformedSiteScope_Returns400ValidationError()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage created = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "scoped-user", role = "Viewer" });
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		string id = createdBody.RootElement.GetProperty("id").GetString()!;

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{id}", "Admin",
			new { site_scope = "not-json" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("validation_error", body.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task Create_DuplicateOidcSub_Returns409()
	{
		HttpClient client = _factory.CreateClient();
		string sub = $"sub-{Guid.NewGuid():N}";
		await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = sub, username = "first", role = "Viewer" });

		HttpResponseMessage second = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = sub, username = "second", role = "Viewer" });

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
	}

	/// <summary>
	/// The core design interpretation this issue records: PUT only ever changes
	/// site_scope. A body that also carries a "role" field is simply not read by
	/// <c>UserUpdateRequest</c> -- the stored role is left exactly as it was, proving the
	/// mirror is not editable through this endpoint even when a caller tries.
	/// </summary>
	[Fact]
	public async Task Update_ChangesSiteScopeButNeverRole()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage created = await SendAsync(client, HttpMethod.Post, "/api/v1/users", "Admin",
			new { oidc_sub = $"sub-{Guid.NewGuid():N}", username = "scoped-user", role = "Viewer" });
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		string id = createdBody.RootElement.GetProperty("id").GetString()!;

		HttpResponseMessage updateResponse = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{id}", "Admin",
			new { site_scope = "[\"site-a\"]", role = "Admin" });

		Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
		using JsonDocument updatedBody = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
		Assert.Equal("[\"site-a\"]", updatedBody.RootElement.GetProperty("site_scope").GetString());
		Assert.Equal("Viewer", updatedBody.RootElement.GetProperty("role").GetString());
	}

	[Fact]
	public async Task Update_UnknownId_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{Guid.NewGuid()}", "Admin",
			new { site_scope = "[]" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Get_UnknownId_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, $"/api/v1/users/{Guid.NewGuid()}", "Admin", null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await client.SendAsync(request);
	}
}

/// <summary>Test host wiring a fake in-memory <see cref="IUserDirectory"/> through <see cref="TestAuthHandler"/>, mirroring <see cref="SchedulesTestApiFactory"/>.</summary>
public sealed class UsersTestApiFactory : WaypointApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			// Same TestAuthHandler-as-default-scheme wiring RoleGuardedApiFactory
			// provides, inlined here rather than switching base classes -- mirrors
			// SchedulesTestApiFactory's own shape exactly.
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

			ServiceDescriptor? descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserDirectory));
			if (descriptor is not null)
			{
				services.Remove(descriptor);
			}

			services.AddSingleton<IUserDirectory, FakeUserDirectory>();
		});
	}
}

/// <summary>Minimal in-memory fake for UsersController tests -- only the surface the controller calls.</summary>
public sealed class FakeUserDirectory : IUserDirectory
{
	private readonly Dictionary<Guid, UserRecord> _users = [];

	public Task RecordSeenAsync(
		string oidcSub, string username, WaypointRole role, string authMethod,
		TimeSpan minimumSeenInterval, CancellationToken cancellationToken)
	{
		KeyValuePair<Guid, UserRecord> existing = _users.FirstOrDefault(kvp => string.Equals(kvp.Value.OidcSub, oidcSub, StringComparison.Ordinal));
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (existing.Value is null)
		{
			Guid id = Guid.NewGuid();
			_users[id] = new UserRecord(id, oidcSub, username, role, "[]", authMethod, now, now, now);
		}
		else
		{
			_users[existing.Key] = existing.Value with { Username = username, Role = role, AuthMethod = authMethod, LastSeenAt = now };
		}

		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<UserRecord>>(_users.Values.ToList());

	public Task<UserRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
		Task.FromResult(_users.GetValueOrDefault(id));

	public Task<Guid?> CreateAsync(
		string oidcSub, string username, WaypointRole role, string siteScopeJson, string authMethod,
		CancellationToken cancellationToken)
	{
		if (_users.Values.Any(u => string.Equals(u.OidcSub, oidcSub, StringComparison.Ordinal)))
		{
			return Task.FromResult<Guid?>(null);
		}

		Guid id = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		_users[id] = new UserRecord(id, oidcSub, username, role, siteScopeJson, authMethod, now, now, now);
		return Task.FromResult<Guid?>(id);
	}

	public Task<bool> UpdateSiteScopeAsync(Guid id, string siteScopeJson, CancellationToken cancellationToken)
	{
		if (!_users.TryGetValue(id, out UserRecord? existing))
		{
			return Task.FromResult(false);
		}

		_users[id] = existing with { SiteScopeJson = siteScopeJson, UpdatedAt = DateTimeOffset.UtcNow };
		return Task.FromResult(true);
	}
}
