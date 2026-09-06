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
using Npgsql;
using Waypoint.Infrastructure.ContentLibraries;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1389 (migration 0113, epic #1185): <c>ContentLibraryFoldersController</c>
/// end to end against real Postgres -- the tree/CRUD/assignment endpoints and the
/// role gate (Admin writes, Viewer+ reads, matching <c>ContentLibrariesController</c>'s
/// own shape). Inlines the same <c>TestAuthHandler</c> role-gate idiom
/// <see cref="RepoCredentialsApiTests"/> uses, rather than subclassing the sealed
/// <c>RoleGuardedApiFactory</c>.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync tears down client/factory.
public sealed class ContentLibraryFoldersApiTests : IAsyncLifetime
{
	private sealed class FoldersApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;

		public FoldersApiFactory(string connectionString)
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

				services.AddSingleton<Waypoint.Core.ContentLibraries.IContentLibraryFolderRepository>(new ContentLibraryFolderRepository(_connectionString));
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private readonly string _rootPath = Directory.CreateTempSubdirectory("wp-content-library-folder-api-test").FullName;
	private FoldersApiFactory _factory = null!;
	private HttpClient _client = null!;

#pragma warning restore CA1001

	public ContentLibraryFoldersApiTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_factory = new FoldersApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		try
		{
			Directory.Delete(_rootPath, recursive: true);
		}
		catch (IOException)
		{
		}

		return Task.CompletedTask;
	}

	[Fact]
	public async Task CreateThenGetTree_RoundTripsANestedFolder()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-tree");

		HttpResponseMessage createRoot = await SendAsync(HttpMethod.Post, $"/api/v1/content-libraries/{libraryId}/folders", "Admin", new { name = "Root" });
		Assert.Equal(HttpStatusCode.Created, createRoot.StatusCode);
		Guid rootId = (await JsonDocument.ParseAsync(await createRoot.Content.ReadAsStreamAsync())).RootElement.GetProperty("id").GetGuid();

		HttpResponseMessage createChild = await SendAsync(
			HttpMethod.Post, $"/api/v1/content-libraries/{libraryId}/folders", "Admin", new { name = "Child", parent_folder_id = rootId });
		Assert.Equal(HttpStatusCode.Created, createChild.StatusCode);

		HttpResponseMessage tree = await SendAsync(HttpMethod.Get, $"/api/v1/content-libraries/{libraryId}/folders", "Viewer", body: null);
		Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await tree.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement[0];
		Assert.Equal("Root", root.GetProperty("name").GetString());
		Assert.Equal(1, root.GetProperty("children").GetArrayLength());
		Assert.Equal("Child", root.GetProperty("children")[0].GetProperty("name").GetString());
	}

	[Fact]
	public async Task Patch_MovingAFolderUnderItsOwnDescendant_Is400()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-cycle");
		Guid parentId = await CreateFolderAsync(libraryId, "Parent", null);
		Guid childId = await CreateFolderAsync(libraryId, "Child", parentId);

		HttpResponseMessage response = await SendAsync(
			HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/folders/{parentId}", "Admin",
			new { name = "Parent", parent_folder_id = childId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Delete_ANonEmptyFolder_Is409()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-nonempty");
		Guid parentId = await CreateFolderAsync(libraryId, "Parent", null);
		await CreateFolderAsync(libraryId, "Child", parentId);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/content-libraries/{libraryId}/folders/{parentId}", "Admin", body: null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task Delete_AnEmptyFolder_Is204()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-empty");
		Guid folderId = await CreateFolderAsync(libraryId, "Empty", null);

		HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"/api/v1/content-libraries/{libraryId}/folders/{folderId}", "Admin", body: null);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	[Fact]
	public async Task AssignItem_ThenGetTree_ShowsTheItemUnderItsFolder()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-assign");
		Guid folderId = await CreateFolderAsync(libraryId, "Holds-item", null);
		Guid itemId = Guid.NewGuid();

		HttpResponseMessage assign = await SendAsync(
			HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/items/{itemId}/folder", "Admin", new { folder_id = folderId });
		Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

		HttpResponseMessage tree = await SendAsync(HttpMethod.Get, $"/api/v1/content-libraries/{libraryId}/folders", "Viewer", body: null);
		using JsonDocument document = JsonDocument.Parse(await tree.Content.ReadAsStringAsync());
		JsonElement itemIds = document.RootElement[0].GetProperty("item_ids");
		Assert.Equal(1, itemIds.GetArrayLength());
		Assert.Equal(itemId, itemIds[0].GetGuid());
	}

	/// <summary>Issue #1389 AC: every mutating endpoint is Admin-only; Viewer/Operator are read-only, matching <c>ContentLibrariesController</c>'s own gate.</summary>
	[Theory]
	[InlineData("Viewer")]
	[InlineData("Operator")]
	public async Task EveryMutatingEndpoint_BelowAdmin_Returns403(string role)
	{
		Guid libraryId = await SeedLibraryAsync($"vcsp-api-rbac-{role.ToLowerInvariant()}");
		Guid folderId = await CreateFolderAsync(libraryId, "Gate", null);

		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Post, $"/api/v1/content-libraries/{libraryId}/folders", role, new { name = "X" })).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/folders/{folderId}", role, new { name = "X" })).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Delete, $"/api/v1/content-libraries/{libraryId}/folders/{folderId}", role, body: null)).StatusCode);
		Assert.Equal(
			HttpStatusCode.Forbidden,
			(await SendAsync(HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/items/{Guid.NewGuid()}/folder", role, new { folder_id = folderId })).StatusCode);
		Assert.Equal(
			HttpStatusCode.OK,
			(await SendAsync(HttpMethod.Get, $"/api/v1/content-libraries/{libraryId}/folders", role, body: null)).StatusCode);
	}

	[Fact]
	public async Task EveryMutatingEndpoint_AsAdmin_Returns2xx()
	{
		Guid libraryId = await SeedLibraryAsync("vcsp-api-admin-ok");
		Guid folderId = await CreateFolderAsync(libraryId, "Ok", null);

		Assert.Equal(
			HttpStatusCode.OK,
			(await SendAsync(HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/folders/{folderId}", "Admin", new { name = "Renamed" })).StatusCode);
		Assert.Equal(
			HttpStatusCode.NoContent,
			(await SendAsync(HttpMethod.Patch, $"/api/v1/content-libraries/{libraryId}/items/{Guid.NewGuid()}/folder", "Admin", new { folder_id = folderId })).StatusCode);
		Assert.Equal(
			HttpStatusCode.Conflict,
			(await SendAsync(HttpMethod.Delete, $"/api/v1/content-libraries/{libraryId}/folders/{folderId}", "Admin", body: null)).StatusCode);
	}

	private async Task<Guid> CreateFolderAsync(Guid libraryId, string name, Guid? parentFolderId)
	{
		HttpResponseMessage response = await SendAsync(
			HttpMethod.Post, $"/api/v1/content-libraries/{libraryId}/folders", "Admin", new { name, parent_folder_id = parentFolderId });
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetGuid();
	}

	private async Task<Guid> SeedLibraryAsync(string name)
	{
		string diskPath = Path.Combine(_rootPath, name);
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO content_libraries (name, disk_path) VALUES ($1, $2) RETURNING id", connection);
		insert.Parameters.AddWithValue(name);
		insert.Parameters.AddWithValue(diskPath);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await _client.SendAsync(request);
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE content_library_item_folders, content_library_folders, content_libraries RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
