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

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ContentLibraries;
using Waypoint.Infrastructure.ContentLibraries;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Infrastructure.ContentLibraries;

/// <summary>
/// Issue #1396 (epic #1185): the item CRUD operation layer proven end to end against
/// real Postgres (the identity table), a real temp filesystem (the library directory
/// and item files), and the real <see cref="VcspContentLibraryWriter"/> (#1393) --
/// exactly the chain the validation run-2 comment on #1704/#1180 step 10 named as
/// missing ("no <c>lib.json</c> until #1396"). A fresh library producing
/// <c>lib.json</c>/<c>items.json</c> on its first add is asserted directly, not
/// mocked, because that IS the property this issue exists to deliver.
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryItemServiceTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ContentLibraryItemService _service = null!;
	private ContentLibraryRepository _libraries = null!;
	private ContentLibraryItemRepository _items = null!;
	private string _rootPath = null!;

	public ContentLibraryItemServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_rootPath = Directory.CreateTempSubdirectory("wp-content-library-item-service-test").FullName;
		_libraries = new ContentLibraryRepository(_fixture.ConnectionString, _rootPath);
		_items = new ContentLibraryItemRepository(_fixture.ConnectionString);
		_service = new ContentLibraryItemService(_libraries, _items, new VcspContentLibraryWriter());
	}

	public Task DisposeAsync()
	{
		try
		{
			Directory.Delete(_rootPath, recursive: true);
		}
		catch (IOException)
		{
		}

		return Task.CompletedTask;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"TRUNCATE TABLE content_library_item_folders, content_library_items, content_library_folders, content_libraries RESTART IDENTITY CASCADE",
			connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<ContentLibrary> SeedLibraryAsync(string name)
	{
		(_, ContentLibrary? library) = await _libraries.CreateAsync(name, CancellationToken.None);
		return library!;
	}

	private static MemoryStream ContentStream(string text = "iso-bytes") => new(Encoding.UTF8.GetBytes(text));

	[Fact]
	public async Task AddAsync_ToAFreshLibrary_WritesLibJsonAndItemsJson()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-fresh");

		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? item) =
			await _service.AddAsync(library.Id, "disk.iso", ContentStream(), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.NotNull(item);
		Assert.Equal(ContentLibraryItemTypes.Iso, item!.Type);
		Assert.Equal(1, item.Version);

		string libJsonPath = Path.Combine(library.DiskPath, "lib.json");
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");
		Assert.True(File.Exists(libJsonPath));
		Assert.True(File.Exists(itemsJsonPath));
		Assert.True(File.Exists(Path.Combine(library.DiskPath, item.DirectoryName, item.Name)));
		Assert.True(File.Exists(Path.Combine(library.DiskPath, item.DirectoryName, "item.json")));

		using JsonDocument lib = JsonDocument.Parse(await File.ReadAllTextAsync(libJsonPath));
		Assert.Equal("1", lib.RootElement.GetProperty("version").GetString());
	}

	[Fact]
	public async Task AddAsync_InfersTypeFromExtension_WithoutOperatorInput()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-infer");

		(_, ContentLibraryItem? ovf) = await _service.AddAsync(library.Id, "template.ovf", ContentStream(), null, CancellationToken.None);
		(_, ContentLibraryItem? other) = await _service.AddAsync(library.Id, "readme.txt", ContentStream(), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemTypes.Ovf, ovf!.Type);
		Assert.Equal(ContentLibraryItemTypes.Other, other!.Type);
	}

	[Fact]
	public async Task UpdateAsync_ReusesTheItemIdAndBumpsVersion_InsteadOfCreatingANewIdentity()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-update");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream("v1"), "first", CancellationToken.None);

		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? updated) =
			await _service.UpdateAsync(library.Id, added!.Id, "disk.iso", ContentStream("v2 different bytes"), "second", CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.Equal(added.Id, updated!.Id);
		Assert.Equal(added.DirectoryName, updated.DirectoryName);
		Assert.Equal(2, updated.Version);
		Assert.Equal("second", updated.Description);

		string itemJsonPath = Path.Combine(library.DiskPath, updated.DirectoryName, "item.json");
		using JsonDocument itemJson = JsonDocument.Parse(await File.ReadAllTextAsync(itemJsonPath));
		Assert.Equal($"urn:uuid:{added.Id}", itemJson.RootElement.GetProperty("id").GetString());
		Assert.Equal("2", itemJson.RootElement.GetProperty("version").GetString());
	}

	[Fact]
	public async Task RemoveAsync_DeletesTheFileAndTheIndexEntry_AndBumpsLibVersion()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-remove");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream(), null, CancellationToken.None);
		string libJsonPath = Path.Combine(library.DiskPath, "lib.json");
		string versionAfterAdd = JsonDocument.Parse(await File.ReadAllTextAsync(libJsonPath)).RootElement.GetProperty("version").GetString()!;
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);

		ContentLibraryItemOperationOutcome outcome = await _service.RemoveAsync(library.Id, added.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.False(Directory.Exists(itemDirectory));
		Assert.Null(await _items.GetAsync(library.Id, added.Id, CancellationToken.None));

		using JsonDocument itemsJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(library.DiskPath, "items.json")));
		Assert.Empty(itemsJson.RootElement.GetProperty("items").EnumerateArray());

		using JsonDocument libJson = JsonDocument.Parse(await File.ReadAllTextAsync(libJsonPath));
		Assert.NotEqual(versionAfterAdd, libJson.RootElement.GetProperty("version").GetString());
	}

	[Fact]
	public async Task AddAsync_ReturnsLibraryNotFound_ForAnUnknownLibrary()
	{
		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? item) =
			await _service.AddAsync(Guid.NewGuid(), "disk.iso", ContentStream(), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.LibraryNotFound, outcome);
		Assert.Null(item);
	}

	[Fact]
	public async Task UpdateAsync_ReturnsItemNotFound_ForAnUnknownItem()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-unknown-item");

		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? item) =
			await _service.UpdateAsync(library.Id, Guid.NewGuid(), "disk.iso", ContentStream(), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.ItemNotFound, outcome);
		Assert.Null(item);
	}

	[Fact]
	public async Task RemoveAsync_ReturnsItemNotFound_ForAnUnknownItem()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-remove-unknown");

		Assert.Equal(
			ContentLibraryItemOperationOutcome.ItemNotFound,
			await _service.RemoveAsync(library.Id, Guid.NewGuid(), CancellationToken.None));
	}
}
