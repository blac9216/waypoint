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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1464 end to end against real Postgres: the <c>/consumer-views</c> surface --
/// EVERY verb (including read) is Admin-only per decision R2-10, an unknown platform
/// key is a 400, and exactly one default view is enforced with a 409, provable through
/// the real HTTP surface.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ConsumerViewsApiTests : IAsyncLifetime
{
	private sealed class ConsumerViewsApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public ConsumerViewsApiFactory(string connectionString)
		{
			_connectionString = connectionString;
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

				services.AddSingleton<IConsumerViewRepository>(new ConsumerViewRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private ConsumerViewsApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ConsumerViewsApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();

		_factory = new ConsumerViewsApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM consumer_views", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task ListViews_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/consumer-views");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	[InlineData("Cyber")]
	public async Task ListViews_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/consumer-views");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task PostView_BelowAdmin_Returns403(string role)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Baseline", platforms = SingleValidPlatform }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostView_UnknownPlatformKey_Returns400()
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Baseline", platforms = SingleUnknownPlatform }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PostView_ThenGet_RoundTripsPlatformsAsAdmin()
	{
		(HttpResponseMessage createResponse, string id) = await CreateViewAsync(
			"7.0 only", ["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], isDefault: false);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage getResponse = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("7.0 only", document.RootElement.GetProperty("name").GetString());
		Assert.False(document.RootElement.GetProperty("is_default").GetBoolean());
		string[] platforms = document.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["embeddedEsx-7.0-INTL", "esxio-8.0-INTL"], platforms);
	}

	[Fact]
	public async Task GetView_AsViewer_Returns403()
	{
		(_, string id) = await CreateViewAsync("Baseline", [], isDefault: false);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/consumer-views/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage response = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostView_DuplicateName_Returns409()
	{
		await CreateViewAsync("Duplicate", [], isDefault: false);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Duplicate", platforms = Array.Empty<string>() }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	/// <summary>Issue #1464 AC: exactly one default view, provable via the API's own 409.</summary>
	[Fact]
	public async Task PostView_SecondDefault_Returns409()
	{
		await CreateViewAsync("First default", [], isDefault: true);

		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name = "Second default", platforms = Array.Empty<string>(), is_default = true }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task PutView_SettingDefaultWhenAnotherIsDefault_Returns409()
	{
		await CreateViewAsync("First default", [], isDefault: true);
		(_, string secondId) = await CreateViewAsync("Not default", [], isDefault: false);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{secondId}")
		{
			Content = JsonBody(new { is_default = true }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task PutView_NameOnly_LeavesPlatformsUntouched()
	{
		(_, string id) = await CreateViewAsync("Original", ["embeddedEsx-7.0-INTL"], isDefault: false);

		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{id}")
		{
			Content = JsonBody(new { name = "Renamed" }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("Renamed", document.RootElement.GetProperty("name").GetString());
		string[] platforms = document.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["embeddedEsx-7.0-INTL"], platforms);
	}

	[Fact]
	public async Task PutView_UnknownId_Returns404()
	{
		HttpRequestMessage put = new(HttpMethod.Put, $"/api/v1/consumer-views/{Guid.NewGuid()}")
		{
			Content = JsonBody(new { name = "New name" }),
		};
		put.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(put);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DeleteView_Existing_Returns204AndRemovesFromList()
	{
		(_, string id) = await CreateViewAsync("To delete", [], isDefault: false);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{id}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage deleteResponse = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

		HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/consumer-views");
		list.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage listResponse = await _client.SendAsync(list);
		using JsonDocument listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.DoesNotContain(listDocument.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
	}

	[Fact]
	public async Task DeleteView_BelowAdmin_Returns403()
	{
		(_, string id) = await CreateViewAsync("To delete", [], isDefault: false);

		HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/consumer-views/{id}");
		delete.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");
		HttpResponseMessage response = await _client.SendAsync(delete);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	private static readonly string[] SingleValidPlatform = ["embeddedEsx-7.0-INTL"];
	private static readonly string[] SingleUnknownPlatform = ["not-a-real-platform"];

	private async Task<(HttpResponseMessage Response, string Id)> CreateViewAsync(string name, string[] platforms, bool isDefault)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/consumer-views")
		{
			Content = JsonBody(new { name, platforms, is_default = isDefault }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string id = document.RootElement.GetProperty("id").GetString()!;
		return (response, id);
	}

	private static StringContent JsonBody(object value) =>
		new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
