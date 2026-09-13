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
using Waypoint.Infrastructure.Capacity;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Infrastructure.Postgres;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api.Controllers;

/// <summary>
/// <c>DiskAdmissionPolicyController</c> (issue #1531, epic #1180) end to end against
/// real Postgres and the real #1529 repository: RBAC (Admin-only for both verbs,
/// matching <c>RetentionPolicyController</c>'s floor), default-value GET, round-trip
/// PUT, and the negative-value 400.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class DiskAdmissionPolicyControllerTests : IAsyncLifetime
{
	private sealed class DiskAdmissionPolicyApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public DiskAdmissionPolicyApiFactory(string connectionString) => _connectionString = connectionString;

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

				services.AddSingleton<IDiskAdmissionPolicyRepository>(new DiskAdmissionPolicyRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private DiskAdmissionPolicyApiFactory _factory = null!;
	private HttpClient _client = null!;
#pragma warning restore CA1001

	public DiskAdmissionPolicyControllerTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();

		// disk_admission_policy is a singleton with no FK to runs, so nothing else in
		// the "Postgres" collection resets it between test classes -- reset it back to
		// the seeded default explicitly, same convention RetentionPolicyTests uses.
		await using NpgsqlConnection reset = new(_fixture.ConnectionString);
		await reset.OpenAsync();
		await using NpgsqlCommand resetCommand = new(
			"UPDATE disk_admission_policy SET reserve_bytes = DEFAULT, updated_by = NULL WHERE id = 1", reset);
		await resetCommand.ExecuteNonQueryAsync();

		_factory = new DiskAdmissionPolicyApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task GetPolicy_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/disk-admission-policy");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task GetPolicy_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/disk-admission-policy");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task GetPolicy_WithAdminRole_ReturnsSeededDefault()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/disk-admission-policy");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(document.RootElement.GetProperty("reserve_bytes").GetInt64() > 0);
		// The API's global JSON options omit a null-valued field entirely rather than
		// emitting it as `null` (see docs/reference/api-contract.md's "Null-valued
		// fields are omitted, not null" convention) -- absence of the property IS the
		// "never changed by an Admin" signal, not a JsonValueKind.Null value.
		Assert.False(document.RootElement.TryGetProperty("updated_by", out _));
	}

	[Fact]
	public async Task PutPolicy_WithAdminRole_UpdatesAndIsReadBack()
	{
		HttpRequestMessage put = new(HttpMethod.Put, "/api/v1/disk-admission-policy")
		{
			Content = JsonBody(new { reserve_bytes = 123_456_789L }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage putResponse = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
		using JsonDocument putDocument = JsonDocument.Parse(await putResponse.Content.ReadAsStringAsync());
		Assert.Equal(123_456_789L, putDocument.RootElement.GetProperty("reserve_bytes").GetInt64());
		Assert.Equal("test-user", putDocument.RootElement.GetProperty("updated_by").GetString());

		HttpRequestMessage get = new(HttpMethod.Get, "/api/v1/disk-admission-policy");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage getResponse = await _client.SendAsync(get);
		using JsonDocument getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal(123_456_789L, getDocument.RootElement.GetProperty("reserve_bytes").GetInt64());
	}

	[Fact]
	public async Task PutPolicy_NegativeReserveBytes_Returns400AndLeavesPolicyUnchanged()
	{
		HttpRequestMessage put = new(HttpMethod.Put, "/api/v1/disk-admission-policy")
		{
			Content = JsonBody(new { reserve_bytes = -1L }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
