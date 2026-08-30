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
/// Issue #1470 end to end against real Postgres: the <c>/downloads/esx</c> surface --
/// role gates, that the platform vocabulary is read fresh from its on-disk source at
/// request time (never hardcoded), that subscription writes reject a platform key
/// outside the current vocabulary, and that the CRUD/disable-preserves-history
/// acceptance criteria round-trip through the real HTTP surface.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class EsxAcquisitionApiTests : IAsyncLifetime
{
	private sealed class EsxAcquisitionApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly string _vocabularyDocumentPath;

		public EsxAcquisitionApiFactory(string connectionString, string vocabularyDocumentPath)
		{
			_connectionString = connectionString;
			_vocabularyDocumentPath = vocabularyDocumentPath;
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

				services.AddSingleton<IEsxAcquisitionSubscriptionRepository>(
					new EsxAcquisitionSubscriptionRepository(_connectionString));
				services.Configure<EsxAcquisitionOptions>(options => options.VocabularyDocumentPath = _vocabularyDocumentPath);
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-esx-acquisition-api-test-").FullName;
	private string _vocabularyDocumentPath = null!;
	private EsxAcquisitionApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public EsxAcquisitionApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();

		_vocabularyDocumentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await WriteVocabularyAsync(["esx-8.0-standard", "esx-8.0-hpe"]);

		_factory = new EsxAcquisitionApiFactory(_fixture.ConnectionString, _vocabularyDocumentPath);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}

		return Task.CompletedTask;
	}

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM esx_acquisition_subscriptions", connection);
		await command.ExecuteNonQueryAsync();
	}

	private Task WriteVocabularyAsync(string[] platforms) =>
		File.WriteAllTextAsync(_vocabularyDocumentPath, JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["lcm.esx.supported.host.platforms"] = platforms,
		}));

	[Fact]
	public async Task GetPlatforms_WithoutAuth_Returns401()
	{
		HttpResponseMessage response = await _client.GetAsync("/api/v1/downloads/esx/platforms");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	/// <summary>
	/// The core "never hardcoded" acceptance criterion: mutating the on-disk
	/// vocabulary source between two requests changes the API response accordingly,
	/// with no restart / cache-bust needed.
	/// </summary>
	[Fact]
	public async Task GetPlatforms_VocabularySourceMutatedBetweenRequests_ResponseChangesAccordingly()
	{
		HttpRequestMessage first = new(HttpMethod.Get, "/api/v1/downloads/esx/platforms");
		first.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage firstResponse = await _client.SendAsync(first);
		Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
		using (JsonDocument firstDocument = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync()))
		{
			string[] platforms = firstDocument.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
			Assert.Equal(["esx-8.0-standard", "esx-8.0-hpe"], platforms);
		}

		await WriteVocabularyAsync(["esx-8.0-standard", "esx-8.0-hpe", "esx-8.0-dell"]);

		HttpRequestMessage second = new(HttpMethod.Get, "/api/v1/downloads/esx/platforms");
		second.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage secondResponse = await _client.SendAsync(second);
		using JsonDocument secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
		string[] updatedPlatforms = secondDocument.RootElement.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["esx-8.0-standard", "esx-8.0-hpe", "esx-8.0-dell"], updatedPlatforms);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task PostSubscription_BelowAdmin_Returns403(string role)
	{
		string[] selectedPlatforms = ["esx-8.0-standard"];
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/esx/subscriptions")
		{
			Content = JsonBody(new { name = "Baseline", selected_platforms = selectedPlatforms }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PostSubscription_UnknownPlatformKey_Returns400()
	{
		string[] selectedPlatforms = ["not-a-real-platform"];
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/esx/subscriptions")
		{
			Content = JsonBody(new { name = "Baseline", selected_platforms = selectedPlatforms }),
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PostSubscription_ThenGet_RoundTripsSelectedPlatforms()
	{
		(HttpResponseMessage createResponse, string id) = await CreateSubscriptionAsync("Baseline ESX 8.0", ["esx-8.0-standard"]);
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

		HttpRequestMessage get = new(HttpMethod.Get, $"/api/v1/downloads/esx/subscriptions/{id}");
		get.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage getResponse = await _client.SendAsync(get);

		Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
		Assert.Equal("Baseline ESX 8.0", document.RootElement.GetProperty("name").GetString());
		Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
		string[] selected = document.RootElement.GetProperty("selected_platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["esx-8.0-standard"], selected);
	}

	/// <summary>Issue #1470 AC: disabling a subscription doesn't delete its history -- the row and its selection survive a disable PATCH.</summary>
	[Fact]
	public async Task PatchSubscription_DisableOnly_PreservesSelectionAndStillListable()
	{
		(_, string id) = await CreateSubscriptionAsync("Baseline ESX 8.0", ["esx-8.0-standard", "esx-8.0-hpe"]);

		HttpRequestMessage patch = new(HttpMethod.Patch, $"/api/v1/downloads/esx/subscriptions/{id}")
		{
			Content = JsonBody(new { enabled = false }),
		};
		patch.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		HttpResponseMessage patchResponse = await _client.SendAsync(patch);

		Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
		using JsonDocument patchDocument = JsonDocument.Parse(await patchResponse.Content.ReadAsStringAsync());
		Assert.False(patchDocument.RootElement.GetProperty("enabled").GetBoolean());
		string[] selected = patchDocument.RootElement.GetProperty("selected_platforms").EnumerateArray().Select(e => e.GetString()!).ToArray();
		Assert.Equal(["esx-8.0-standard", "esx-8.0-hpe"], selected);

		// Still present in the list -- disabling never deletes the row.
		HttpRequestMessage list = new(HttpMethod.Get, "/api/v1/downloads/esx/subscriptions");
		list.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");
		HttpResponseMessage listResponse = await _client.SendAsync(list);
		using JsonDocument listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
		Assert.Contains(listDocument.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
	}

	[Fact]
	public async Task PatchSubscription_UnknownId_Returns404()
	{
		HttpRequestMessage patch = new(HttpMethod.Patch, $"/api/v1/downloads/esx/subscriptions/{Guid.NewGuid()}")
		{
			Content = JsonBody(new { enabled = false }),
		};
		patch.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await _client.SendAsync(patch);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	private async Task<(HttpResponseMessage Response, string Id)> CreateSubscriptionAsync(string name, string[] selectedPlatforms)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/downloads/esx/subscriptions")
		{
			Content = JsonBody(new { name, selected_platforms = selectedPlatforms }),
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
