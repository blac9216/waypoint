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

	// ---- the traversal guard (review round 1, F1) -----------------------------------
	//
	// ContentLibraryItemService.ValidateFileName/ResolveItemFilePath is the only
	// security guard this slice adds and round 1 proved it unpinned: the reviewer
	// replaced its whole condition with `if (false)` and the suite stayed green,
	// because nothing anywhere handed AddAsync/UpdateAsync a name that was not already
	// a plain single segment. These cases hand it every shape the guard exists to
	// reject, on BOTH entry points, and the traversal case additionally asserts that
	// nothing landed outside the library root -- so a regression is caught by outcome,
	// not only by exception type.

	[Theory]
	[InlineData("../escape.iso")]
	[InlineData("../../escape.iso")]
	[InlineData("sub/dir.iso")]
	[InlineData("sub\\dir.iso")]
	[InlineData("/abs/path.iso")]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("")]
	[InlineData("   ")]
	public async Task AddAsync_RejectsAFileNameThatIsNotASinglePathSegment(string fileName)
	{
		ContentLibrary library = await SeedLibraryAsync($"vcsp-add-guard-{Guid.NewGuid():N}");

		await Assert.ThrowsAsync<ArgumentException>(
			() => _service.AddAsync(library.Id, fileName, ContentStream(), null, CancellationToken.None));
	}

	[Theory]
	[InlineData("../escape.iso")]
	[InlineData("../../escape.iso")]
	[InlineData("sub/dir.iso")]
	[InlineData("sub\\dir.iso")]
	[InlineData("/abs/path.iso")]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("")]
	[InlineData("   ")]
	public async Task UpdateAsync_RejectsAFileNameThatIsNotASinglePathSegment(string fileName)
	{
		ContentLibrary library = await SeedLibraryAsync($"vcsp-update-guard-{Guid.NewGuid():N}");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream(), null, CancellationToken.None);

		await Assert.ThrowsAsync<ArgumentException>(
			() => _service.UpdateAsync(library.Id, added!.Id, fileName, ContentStream(), null, CancellationToken.None));
	}

	[Fact]
	public async Task AddAsync_WithATraversingFileName_WritesNothingOutsideTheItemDirectory()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-traversal");
		string outsideLibrary = Path.Combine(_rootPath, "escape.iso");
		string insideLibraryRoot = Path.Combine(library.DiskPath, "escape.iso");

		await Assert.ThrowsAsync<ArgumentException>(
			() => _service.AddAsync(library.Id, "../escape.iso", ContentStream("pwned"), null, CancellationToken.None));

		Assert.False(File.Exists(outsideLibrary), $"the guard let a write escape to {outsideLibrary}");
		Assert.False(File.Exists(insideLibraryRoot), $"the guard let a write escape to {insideLibraryRoot}");
	}

	[Fact]
	public async Task UpdateAsync_WithATraversingFileName_LeavesThePriorPayloadAndDocumentsUntouched()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-traversal-update");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream("v1"), null, CancellationToken.None);
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);
		byte[] payloadBefore = await File.ReadAllBytesAsync(Path.Combine(itemDirectory, "disk.iso"));
		byte[] itemsBefore = await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json"));

		await Assert.ThrowsAsync<ArgumentException>(
			() => _service.UpdateAsync(library.Id, added.Id, "../escape.iso", ContentStream("pwned"), null, CancellationToken.None));

		Assert.False(File.Exists(Path.Combine(library.DiskPath, "escape.iso")));
		Assert.False(File.Exists(Path.Combine(_rootPath, "escape.iso")));
		Assert.Equal(payloadBefore, await File.ReadAllBytesAsync(Path.Combine(itemDirectory, "disk.iso")));
		Assert.Equal(itemsBefore, await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json")));
	}

	// ---- the name-length bound (review round 2 relay, F4) ---------------------------
	//
	// The service writes each payload through a same-directory temp component whose
	// name is `.<fileName>.<32-hex guid>.tmp` -- 38 bytes of decoration around the
	// caller's own name. Linux NAME_MAX is 255 bytes per component, so the longest
	// name the service can actually store is 255 - 38 = 217 bytes; one byte more is
	// admitted by every other clause of the guard yet cannot be written, and before
	// the bound existed it failed as a PathTooLongException out of the FileStream
	// constructor (a 500 under #1826's surface) after AddAsync had already created the
	// item's directory. These four cases pin both sides of that boundary on both entry
	// points. The service derives the bound from its own temp format; the numbers are
	// restated here deliberately, so a change to that format has to be a conscious one.

	private const int LongestWritableFileNameLength = 217;

	private static string FileNameOfLength(int length) => new string('a', length - ".iso".Length) + ".iso";

	[Fact]
	public async Task AddAsync_AtTheLongestWritableFileName_StoresItAndRoundTripsThroughTheIndex()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-name-length-add");
		string fileName = FileNameOfLength(LongestWritableFileNameLength);

		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? item) =
			await _service.AddAsync(library.Id, fileName, ContentStream("bytes"), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.Equal(fileName, item!.Name);
		Assert.True(File.Exists(Path.Combine(library.DiskPath, item.DirectoryName, fileName)));
		await AssertIndexAgreesWithDiskAsync(library);
	}

	[Fact]
	public async Task AddAsync_OneByteBeyondTheLongestWritableFileName_IsRejectedWithoutLeavingADirectory()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-name-length-add-over");
		int directoriesBefore = Directory.GetDirectories(library.DiskPath).Length;

		// An ArgumentException, exactly like every other rejected name -- not the
		// PathTooLongException the write would otherwise raise -- and raised early
		// enough that no item directory is created for a request that cannot succeed.
		await Assert.ThrowsAsync<ArgumentException>(() => _service.AddAsync(
			library.Id, FileNameOfLength(LongestWritableFileNameLength + 1), ContentStream("bytes"), null, CancellationToken.None));

		Assert.Equal(directoriesBefore, Directory.GetDirectories(library.DiskPath).Length);
	}

	[Fact]
	public async Task UpdateAsync_AtTheLongestWritableFileName_StoresIt()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-name-length-update");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream("v1"), null, CancellationToken.None);
		string fileName = FileNameOfLength(LongestWritableFileNameLength);

		(ContentLibraryItemOperationOutcome outcome, ContentLibraryItem? updated) =
			await _service.UpdateAsync(library.Id, added!.Id, fileName, ContentStream("v2"), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.Equal(fileName, updated!.Name);
		Assert.True(File.Exists(Path.Combine(library.DiskPath, updated.DirectoryName, fileName)));
		Assert.False(File.Exists(Path.Combine(library.DiskPath, updated.DirectoryName, "disk.iso")));
		await AssertIndexAgreesWithDiskAsync(library);
	}

	[Fact]
	public async Task UpdateAsync_OneByteBeyondTheLongestWritableFileName_IsRejected()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-name-length-update-over");
		(_, ContentLibraryItem? maybeAdded) = await _service.AddAsync(library.Id, "disk.iso", ContentStream("v1"), null, CancellationToken.None);
		ContentLibraryItem added = maybeAdded!;

		await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(
			library.Id, added.Id, FileNameOfLength(LongestWritableFileNameLength + 1), ContentStream("v2"), null, CancellationToken.None));

		// The rejected update left the prior payload and the documents exactly as they were.
		Assert.True(File.Exists(Path.Combine(library.DiskPath, added.DirectoryName, "disk.iso")));
		await AssertIndexAgreesWithDiskAsync(library);
	}

	// ---- payload atomicity and mutation ordering (review round 1, F2 and F3) --------

	private const int LargePayloadLength = 2 * 1024 * 1024;

	private static byte[] LargePayload(byte fill) => Enumerable.Repeat(fill, LargePayloadLength).ToArray();

	/// <summary>
	/// Every read a concurrent subscriber takes of an item's payload must see one
	/// complete version of the file or the other -- never a truncated or half-rewritten
	/// one. Both versions are uniform runs of a single byte and the same length, so
	/// "complete" is checkable in one vectorised scan.
	/// </summary>
	private static void AssertPayloadIsAWholeVersion(byte[] bytes)
	{
		Assert.Equal(LargePayloadLength, bytes.Length);
		byte first = bytes[0];
		Assert.True(first is (byte)'a' or (byte)'b', $"payload started with an unexpected byte 0x{first:x2}");
		Assert.True(
			bytes.AsSpan().IndexOfAnyExcept(first) < 0,
			"payload held a mix of the old and the new bytes -- a partially written file was observable");
	}

	[Fact]
	public async Task UpdateAsync_ConcurrentReaders_NeverObserveAPartiallyWrittenPayload()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-update-atomic");
		(_, ContentLibraryItem? added) = await _service.AddAsync(
			library.Id, "disk.iso", new MemoryStream(LargePayload((byte)'a')), null, CancellationToken.None);
		string payloadPath = Path.Combine(library.DiskPath, added!.DirectoryName, "disk.iso");

		using CancellationTokenSource stop = new();
		int readCount = 0;
		Task readerTask = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				// A truncate-in-place rewrite of the final path surfaces here either as
				// a short/mixed buffer (caught by the assertion) or as a sharing
				// violation on the exclusively held file -- File.Move's rename is the
				// only thing standing between "always a whole file" and both.
				AssertPayloadIsAWholeVersion(await File.ReadAllBytesAsync(payloadPath, CancellationToken.None));
				Interlocked.Increment(ref readCount);
			}
		});

		// Paced so the write occupies a window wide enough for the reader loop to land
		// inside it, rather than relying on a full-speed 2 MiB copy being slow enough.
		await using PacedStream content = new(LargePayload((byte)'b'), TimeSpan.FromMilliseconds(5));
		(ContentLibraryItemOperationOutcome outcome, _) =
			await _service.UpdateAsync(library.Id, added.Id, "disk.iso", content, null, CancellationToken.None);

		await stop.CancelAsync();
		await readerTask;

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.True(readCount > 0, "the reader loop never got a chance to run");
		AssertPayloadIsAWholeVersion(await File.ReadAllBytesAsync(payloadPath));
		Assert.Equal((byte)'b', (await File.ReadAllBytesAsync(payloadPath))[0]);
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task UpdateAsync_CancelledMidPayloadWrite_LeavesThePriorTreeCompletelyUntouched()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-update-cancel");
		(_, ContentLibraryItem? added) = await _service.AddAsync(
			library.Id, "disk.iso", new MemoryStream(LargePayload((byte)'a')), null, CancellationToken.None);
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);
		string payloadPath = Path.Combine(itemDirectory, "disk.iso");
		byte[] payloadBefore = await File.ReadAllBytesAsync(payloadPath);
		byte[] itemsBefore = await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json"));
		byte[] libBefore = await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "lib.json"));
		byte[] itemJsonBefore = await File.ReadAllBytesAsync(Path.Combine(itemDirectory, "item.json"));

		// Deterministically mid-write: the stream itself cancels the token the instant
		// it has handed over its first chunk, so the cancellation is guaranteed to land
		// after the payload copy has started and long before it could finish. No
		// wall-clock polling is involved.
		using CancellationTokenSource cts = new();
		await using PacedStream content = new(LargePayload((byte)'b'), TimeSpan.Zero, cancelAfterFirstChunk: cts);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => _service.UpdateAsync(library.Id, added.Id, "disk.iso", content, null, cts.Token));

		Assert.Equal(payloadBefore, await File.ReadAllBytesAsync(payloadPath));
		Assert.Equal(itemsBefore, await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json")));
		Assert.Equal(libBefore, await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "lib.json")));
		Assert.Equal(itemJsonBefore, await File.ReadAllBytesAsync(Path.Combine(itemDirectory, "item.json")));
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task UpdateAsync_UnderARenamedReUpload_DeletesTheStaleFileOnlyAfterTheRepublish()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-update-order");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "old.iso", ContentStream("v1"), null, CancellationToken.None);
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);

		bool probing = false;
		bool staleFileStillPresentAtRepublish = false;
		bool newFileAlreadyPresentAtRepublish = false;
		ContentLibraryItemService probed = new(_libraries, _items, new ProbingContentLibraryWriter(
			new VcspContentLibraryWriter(),
			() =>
			{
				if (!probing)
				{
					return;
				}

				staleFileStillPresentAtRepublish = File.Exists(Path.Combine(itemDirectory, "old.iso"));
				newFileAlreadyPresentAtRepublish = File.Exists(Path.Combine(itemDirectory, "new.iso"));
			}));

		probing = true;
		(ContentLibraryItemOperationOutcome outcome, _) =
			await probed.UpdateAsync(library.Id, added.Id, "new.iso", ContentStream("v2"), null, CancellationToken.None);

		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, outcome);
		Assert.True(newFileAlreadyPresentAtRepublish, "the new payload was not in place when items.json was rewritten to advertise it");
		Assert.True(staleFileStillPresentAtRepublish, "the stale payload was unlinked while items.json still advertised it");
		Assert.False(File.Exists(Path.Combine(itemDirectory, "old.iso")), "the stale payload was never cleaned up");
		Assert.True(File.Exists(Path.Combine(itemDirectory, "new.iso")));
	}

	[Fact]
	public async Task AddAsync_CancelledAtRepublish_LeavesTheIndexAndTheDiskAgreeing()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-add-cancel");
		await _service.AddAsync(library.Id, "first.iso", ContentStream("v1"), null, CancellationToken.None);
		byte[] itemsBefore = await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json"));
		byte[] libBefore = await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "lib.json"));

		using CancellationTokenSource cts = new();
		ContentLibraryItemService probed = new(_libraries, _items, new ProbingContentLibraryWriter(
			new VcspContentLibraryWriter(), () => cts.Cancel()));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => probed.AddAsync(library.Id, "second.iso", ContentStream("v2"), null, cts.Token));

		// The republish never happened, so items.json/lib.json still describe exactly
		// the previous library -- and every href they name still resolves. The new
		// item's own directory exists but is advertised by nothing, which is the
		// leftover shape the interface already documents, not an inconsistency.
		Assert.Equal(itemsBefore, await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "items.json")));
		Assert.Equal(libBefore, await File.ReadAllBytesAsync(Path.Combine(library.DiskPath, "lib.json")));
		await AssertIndexAgreesWithDiskAsync(library);
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task RemoveAsync_RepublishesBeforeDeletingTheItemDirectory()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-remove-order");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream(), null, CancellationToken.None);
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);

		bool probing = false;
		bool directoryStillPresentAtRepublish = false;
		ContentLibraryItemService probed = new(_libraries, _items, new ProbingContentLibraryWriter(
			new VcspContentLibraryWriter(),
			() =>
			{
				if (probing)
				{
					directoryStillPresentAtRepublish = Directory.Exists(itemDirectory);
				}
			}));

		probing = true;
		Assert.Equal(ContentLibraryItemOperationOutcome.Succeeded, await probed.RemoveAsync(library.Id, added.Id, CancellationToken.None));

		Assert.True(
			directoryStillPresentAtRepublish,
			"the item directory was deleted before items.json stopped advertising it -- a reader in that window resolves an href to a missing path");
		Assert.False(Directory.Exists(itemDirectory));
		await AssertIndexAgreesWithDiskAsync(library);
	}

	[Fact]
	public async Task RemoveAsync_CancelledAtRepublish_LeavesTheIndexAndTheDiskAgreeing()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-remove-cancel");
		(_, ContentLibraryItem? added) = await _service.AddAsync(library.Id, "disk.iso", ContentStream(), null, CancellationToken.None);
		string itemDirectory = Path.Combine(library.DiskPath, added!.DirectoryName);

		using CancellationTokenSource cts = new();
		ContentLibraryItemService probed = new(_libraries, _items, new ProbingContentLibraryWriter(
			new VcspContentLibraryWriter(), () => cts.Cancel()));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probed.RemoveAsync(library.Id, added.Id, cts.Token));

		// The identity row is gone and the republish that would have dropped the entry
		// from items.json never ran -- so items.json still lists the item, and the
		// ordering under test is what keeps its directory there to back that listing.
		// Deleting the directory first would have made this window self-contradictory.
		Assert.True(Directory.Exists(itemDirectory), "the item directory was deleted even though items.json still advertises it");
		await AssertIndexAgreesWithDiskAsync(library);
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task Mutations_ConcurrentReaders_NeverObserveAnAdvertisedItemWhoseFilesAreMissing()
	{
		ContentLibrary library = await SeedLibraryAsync("vcsp-mutation-readers");
		List<ContentLibraryItem> items = [];
		for (int i = 0; i < 4; i++)
		{
			(_, ContentLibraryItem? seeded) = await _service.AddAsync(
				library.Id, $"disk-{i}.iso", ContentStream($"seed-{i}"), null, CancellationToken.None);
			items.Add(seeded!);
		}

		using CancellationTokenSource stop = new();
		int readCount = 0;
		Task readerTask = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				await AssertIndexAgreesWithDiskAsync(library);
				Interlocked.Increment(ref readCount);
			}
		});

		for (int round = 0; round < 4; round++)
		{
			await _service.UpdateAsync(
				library.Id, items[round].Id, $"renamed-{round}.iso", ContentStream($"round-{round}"), null, CancellationToken.None);
			await _service.AddAsync(library.Id, $"extra-{round}.iso", ContentStream($"extra-{round}"), null, CancellationToken.None);
			await _service.RemoveAsync(library.Id, items[round].Id, CancellationToken.None);
		}

		await stop.CancelAsync();
		await readerTask;

		Assert.True(readCount > 0, "the reader loop never got a chance to run");
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	/// <summary>
	/// The invariant every mutation must preserve at every instant a reader can look:
	/// <c>items.json</c> parses, and every href it advertises -- each item's own
	/// directory, its <c>item.json</c>, and each of its payload files -- resolves to a
	/// file that is actually there.
	/// </summary>
	private static async Task AssertIndexAgreesWithDiskAsync(ContentLibrary library)
	{
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");
		byte[] bytes = await File.ReadAllBytesAsync(itemsJsonPath, CancellationToken.None);
		List<string> missing = [];
		using (JsonDocument document = JsonDocument.Parse(bytes))
		{
			foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray())
			{
				string selfHref = item.GetProperty("selfHref").GetString()!;
				string itemJsonPath = Path.Combine(library.DiskPath, Uri.UnescapeDataString(selfHref).Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(itemJsonPath))
				{
					missing.Add(selfHref);
				}

				foreach (JsonElement file in item.GetProperty("files").EnumerateArray())
				{
					foreach (JsonElement href in file.GetProperty("hrefs").EnumerateArray())
					{
						string relative = Uri.UnescapeDataString(href.GetString()!).Replace('/', Path.DirectorySeparatorChar);
						if (!File.Exists(Path.Combine(library.DiskPath, relative)))
						{
							missing.Add(href.GetString()!);
						}
					}
				}
			}
		}

		if (missing.Count == 0)
		{
			return;
		}

		// The scan above is not one instant. A mutation that republished between this
		// reader's items.json read and its File.Exists calls makes the bytes in hand a
		// description of a library that has since moved on, and an href dropped by that
		// republish is stale reading, not an inconsistency in the tree -- exactly what a
		// real subscriber re-polls lib.json.version to discover. Re-reading tells the two
		// cases apart: if items.json is byte-for-byte the one that was just scanned, then
		// this IS the current index and it really is advertising something that is not on
		// disk, which is the F3 defect. (Under the delete-before-republish mutation the
		// bytes are unchanged in exactly that window, so this still catches it.)
		byte[] after = await File.ReadAllBytesAsync(itemsJsonPath, CancellationToken.None);
		Assert.False(
			after.AsSpan().SequenceEqual(bytes),
			$"items.json advertises {string.Join(", ", missing.Select(href => $"'{href}'"))} but nothing is there, and it is still the current index");
	}

	/// <summary>
	/// Wraps the real <see cref="VcspContentLibraryWriter"/> so a test can observe the
	/// state of the tree at the exact moment the service republishes -- the ordering
	/// findings F2 and F3 are both about what is on disk in that instant, which no
	/// before/after assertion can see.
	/// </summary>
	private sealed class ProbingContentLibraryWriter : IContentLibraryWriter
	{
		private readonly IContentLibraryWriter _inner;
		private readonly Action _atRepublish;

		public ProbingContentLibraryWriter(IContentLibraryWriter inner, Action atRepublish)
		{
			_inner = inner;
			_atRepublish = atRepublish;
		}

		public Task WriteAsync(ContentLibrary library, IReadOnlyList<ContentLibraryItemWrite> items, CancellationToken cancellationToken)
		{
			_atRepublish();
			return _inner.WriteAsync(library, items, cancellationToken);
		}
	}

	/// <summary>
	/// An upload stream that hands its bytes over in fixed chunks, optionally pausing
	/// between them so a payload write occupies a real window, and optionally
	/// cancelling a token the moment its first chunk has been consumed so a mid-write
	/// cancellation is deterministic instead of timing-dependent.
	/// </summary>
	private sealed class PacedStream : Stream
	{
		private const int ChunkSize = 64 * 1024;

		private readonly byte[] _data;
		private readonly TimeSpan _delay;
		private readonly CancellationTokenSource? _cancelAfterFirstChunk;
		private int _position;

		public PacedStream(byte[] data, TimeSpan delay, CancellationTokenSource? cancelAfterFirstChunk = null)
		{
			_data = data;
			_delay = delay;
			_cancelAfterFirstChunk = cancelAfterFirstChunk;
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => _data.Length;

		public override long Position
		{
			get => _position;
			set => throw new NotSupportedException();
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_delay > TimeSpan.Zero)
			{
				await Task.Delay(_delay, CancellationToken.None);
			}

			int count = Read(buffer.Span);
			if (count > 0 && _cancelAfterFirstChunk is not null)
			{
				await _cancelAfterFirstChunk.CancelAsync();
			}

			return count;
		}

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			int count = Math.Min(Math.Min(ChunkSize, buffer.Length), _data.Length - _position);
			if (count <= 0)
			{
				return 0;
			}

			_data.AsSpan(_position, count).CopyTo(buffer);
			_position += count;
			return count;
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
