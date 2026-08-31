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

using System.Text.Json;
using Waypoint.Core.ContentLibraries;
using Waypoint.Infrastructure.ContentLibraries;
using Xunit;

namespace Waypoint.Tests.Infrastructure.ContentLibraries;

/// <summary>Mutable-instant <see cref="TimeProvider"/> so timestamp-stability tests can advance the clock between writes.</summary>
internal sealed class MutableFakeTimeProvider : TimeProvider
{
	private DateTimeOffset _now;
	public MutableFakeTimeProvider(DateTimeOffset now) => _now = now;
	public override DateTimeOffset GetUtcNow() => _now;
	public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Issue #1393 (epic #1185, design record #16 section 6, research #1032): proves the
/// three VCSP-protocol behaviors the sibling repo's ported logic gets wrong (or never
/// had at all) --
/// <list type="bullet">
/// <item><description><c>lib.json.version</c> is the change counter that must
/// increment on every item-changing write, while <c>contentVersion</c> never
/// moves.</description></item>
/// <item><description>emitted <c>hrefs</c>/<c>selfHref</c> are library-root-relative
/// paths that resolve, never bare filenames.</description></item>
/// <item><description><c>items.json</c> is never observable in a partially-written
/// state.</description></item>
/// </list>
/// against a real temp filesystem -- a fake or in-memory store cannot stand in for
/// what a real rename does.
/// </summary>
public sealed class VcspContentLibraryWriterTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-vcsp-writer-test").FullName;
	private readonly MutableFakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// best-effort cleanup only
		}
	}

	private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private VcspContentLibraryWriter CreateWriter() => new(_clock);

	private ContentLibrary CreateLibrary(string name = "invented-library")
	{
		string diskPath = Path.Combine(_root, name);
		Directory.CreateDirectory(diskPath);
		return new ContentLibrary(Guid.NewGuid(), name, diskPath, _clock.GetUtcNow(), _clock.GetUtcNow());
	}

	private static ContentLibraryItemWrite MakeItem(
		Guid id,
		string directoryName = "item-one",
		string fileName = "payload.txt",
		string contentHash = "hash-v1") => new(
		id,
		directoryName,
		Name: "Invented Item",
		Type: ContentLibraryItemTypes.Other,
		Description: "",
		Files: [new ContentLibraryItemFileWrite(fileName, Size: 42, contentHash)]);

	private static LibJson ReadLib(ContentLibrary library) =>
		JsonSerializer.Deserialize<LibJson>(File.ReadAllText(Path.Combine(library.DiskPath, "lib.json")), WireOptions)!;

	private static ItemsJson ReadItems(ContentLibrary library) =>
		JsonSerializer.Deserialize<ItemsJson>(File.ReadAllText(Path.Combine(library.DiskPath, "items.json")), WireOptions)!;

	// ---- version-counter semantics (research #1032) -----------------------------

	[Fact]
	public async Task WriteAsync_FirstWrite_SetsLibVersionAndContentVersionToOne()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();

		await writer.WriteAsync(library, [MakeItem(Guid.NewGuid())], CancellationToken.None);

		LibJson lib = ReadLib(library);
		Assert.Equal("2", lib.VcspVersion);
		Assert.Equal("1", lib.Version);
		Assert.Equal("1", lib.ContentVersion);
		Assert.Equal("httpGet", Assert.Single(lib.Capabilities.TransferIn));
		Assert.Equal("httpGet", Assert.Single(lib.Capabilities.TransferOut));
		Assert.StartsWith("urn:uuid:", lib.Id, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WriteAsync_RepeatedWriteWithNoChanges_LeavesLibVersionAndContentVersionUnchanged()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid itemId = Guid.NewGuid();

		await writer.WriteAsync(library, [MakeItem(itemId)], CancellationToken.None);
		string libIdAfterFirstWrite = ReadLib(library).Id;

		_clock.Advance(TimeSpan.FromMinutes(5));
		await writer.WriteAsync(library, [MakeItem(itemId)], CancellationToken.None);

		LibJson lib = ReadLib(library);
		// Unchanged content: the library's own change counter must NOT advance, and its
		// identity/contentVersion never move regardless.
		Assert.Equal("1", lib.Version);
		Assert.Equal("1", lib.ContentVersion);
		Assert.Equal(libIdAfterFirstWrite, lib.Id);
	}

	[Theory]
	[InlineData("adding a changed item")]
	[InlineData("removing an item")]
	public async Task WriteAsync_LibraryChangingWrite_IncrementsLibVersion_ButNeverContentVersion(string scenario)
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid keptId = Guid.NewGuid();
		Guid otherId = Guid.NewGuid();

		await writer.WriteAsync(
			library,
			[MakeItem(keptId, directoryName: "kept"), MakeItem(otherId, directoryName: "other")],
			CancellationToken.None);
		Assert.Equal("1", ReadLib(library).Version);

		IReadOnlyList<ContentLibraryItemWrite> nextItems = scenario == "removing an item"
			? [MakeItem(keptId, directoryName: "kept")]
			: [MakeItem(keptId, directoryName: "kept"), MakeItem(otherId, directoryName: "other", contentHash: "hash-v2")];

		await writer.WriteAsync(library, nextItems, CancellationToken.None);

		LibJson lib = ReadLib(library);
		Assert.Equal("2", lib.Version);
		// contentVersion is the sibling's known inversion target: it must stay put no
		// matter how many times the library's real change counter advances.
		Assert.Equal("1", lib.ContentVersion);
	}

	[Fact]
	public async Task WriteAsync_ItemContentChange_BumpsOnlyThatItemsVersion_PreservingIdAndCreated()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid changedId = Guid.NewGuid();
		Guid untouchedId = Guid.NewGuid();

		await writer.WriteAsync(
			library,
			[MakeItem(changedId, directoryName: "changed"), MakeItem(untouchedId, directoryName: "untouched")],
			CancellationToken.None);
		ItemsJson before = ReadItems(library);
		ItemJson changedBefore = before.Items.Single(i => i.SelfHref.StartsWith("changed/", StringComparison.Ordinal));
		ItemJson untouchedBefore = before.Items.Single(i => i.SelfHref.StartsWith("untouched/", StringComparison.Ordinal));

		_clock.Advance(TimeSpan.FromHours(1));
		await writer.WriteAsync(
			library,
			[MakeItem(changedId, directoryName: "changed", contentHash: "hash-v2"), MakeItem(untouchedId, directoryName: "untouched")],
			CancellationToken.None);
		ItemsJson after = ReadItems(library);
		ItemJson changedAfter = after.Items.Single(i => i.SelfHref.StartsWith("changed/", StringComparison.Ordinal));
		ItemJson untouchedAfter = after.Items.Single(i => i.SelfHref.StartsWith("untouched/", StringComparison.Ordinal));

		Assert.Equal("2", changedAfter.Version);
		Assert.Equal(changedBefore.Id, changedAfter.Id);
		Assert.Equal(changedBefore.Created, changedAfter.Created);

		// The untouched item's own version/created must be completely unaffected by a
		// sibling item's content change in the SAME write.
		Assert.Equal(untouchedBefore.Version, untouchedAfter.Version);
		Assert.Equal(untouchedBefore.Created, untouchedAfter.Created);
		Assert.Equal(untouchedBefore.Id, untouchedAfter.Id);
	}

	// ---- hrefs shape (research #1032) --------------------------------------------

	[Fact]
	public async Task WriteAsync_ItemHrefsAndSelfHref_AreLibraryRootRelative_AndResolve()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid itemId = Guid.NewGuid();

		await writer.WriteAsync(
			library,
			[MakeItem(itemId, directoryName: "my-item", fileName: "disk.vmdk")],
			CancellationToken.None);

		ItemJson item = ReadItems(library).Items.Single();
		Assert.Equal("my-item/item.json", item.SelfHref);
		ItemFileJson file = item.Files.Single();
		string href = Assert.Single(file.Hrefs);
		Assert.Equal("my-item/disk.vmdk", href);

		// "Resolve from the library root" proven literally: root + selfHref is the
		// standalone item.json this same write produced. (root + href is NOT asserted
		// to exist as a real file: moving item file bytes onto disk is item CRUD's job,
		// #1396 -- this writer only emits the metadata describing where they belong.)
		Assert.True(File.Exists(Path.Combine(library.DiskPath, item.SelfHref.Replace('/', Path.DirectorySeparatorChar))));

		string standaloneItemJson = File.ReadAllText(Path.Combine(library.DiskPath, "my-item", "item.json"));
		ItemJson standalone = JsonSerializer.Deserialize<ItemJson>(standaloneItemJson, WireOptions)!;
		Assert.Equal(item.SelfHref, standalone.SelfHref);
		Assert.Equal(href, standalone.Files.Single().Hrefs.Single());
	}

	[Fact]
	public async Task WriteAsync_ItemType_IsOneOfTheClosedVcspVocabulary()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		ContentLibraryItemWrite isoItem = MakeItem(Guid.NewGuid(), directoryName: "iso-item") with { Type = ContentLibraryItemTypes.Iso };

		await writer.WriteAsync(library, [isoItem], CancellationToken.None);

		Assert.Equal("vcsp.iso", ReadItems(library).Items.Single().Type);
	}

	// ---- items.json atomicity (scope addition, #1026 triage) --------------------

	[Fact]
	public async Task WriteAsync_SuccessfulWrite_LeavesNoStrayTempFiles()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();

		await writer.WriteAsync(library, [MakeItem(Guid.NewGuid())], CancellationToken.None);

		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp"));
		Assert.Empty(Directory.GetFiles(Path.Combine(library.DiskPath, "item-one"), "*.tmp"));
	}

	[Fact]
	public async Task WriteAsync_CancelledBeforeCompletion_LeavesPriorItemsJsonCompletelyUntouched()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid itemId = Guid.NewGuid();

		await writer.WriteAsync(library, [MakeItem(itemId)], CancellationToken.None);
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");
		byte[] before = File.ReadAllBytes(itemsJsonPath);

		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		// Simulates a writer killed mid-run: a cancellation that fires before this
		// call's documents are renamed into place must never leave items.json
		// observably different from the last fully-committed write -- not truncated,
		// not half-updated, byte-for-byte the same file.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => writer.WriteAsync(library, [MakeItem(itemId, contentHash: "hash-v2")], cts.Token));

		byte[] after = File.ReadAllBytes(itemsJsonPath);
		Assert.Equal(before, after);
		Assert.Empty(Directory.GetFiles(library.DiskPath, ".*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task WriteAsync_ConcurrentReaders_NeverObserveAPartiallyWrittenItemsJson()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");

		// Seed a reasonably large item set so each rewrite's serialize-then-rename
		// window is wide enough for a concurrent reader to have a real chance at
		// catching an in-progress write if the implementation were not atomic.
		List<ContentLibraryItemWrite> items = Enumerable.Range(0, 200)
			.Select(i => MakeItem(Guid.NewGuid(), directoryName: $"item-{i:D4}"))
			.ToList();
		await writer.WriteAsync(library, items, CancellationToken.None);

		using CancellationTokenSource stop = new();
		int readCount = 0;
		Task readerTask = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				byte[] bytes = await File.ReadAllBytesAsync(itemsJsonPath, CancellationToken.None);
				// A torn/partial rename would surface here as a JSON parse failure --
				// File.Move's rename is the only thing standing between "always valid"
				// and this throwing.
				using JsonDocument document = JsonDocument.Parse(bytes);
				Interlocked.Increment(ref readCount);
			}
		});

		for (int rewrite = 0; rewrite < 25; rewrite++)
		{
			List<ContentLibraryItemWrite> mutated = items
				.Select((item, index) => index == rewrite % items.Count
					? item with { Files = [new ContentLibraryItemFileWrite("payload.txt", 42, $"hash-{rewrite}")] }
					: item)
				.ToList();
			await writer.WriteAsync(library, mutated, CancellationToken.None);
			items = mutated;
		}

		stop.Cancel();
		await readerTask;
		Assert.True(readCount > 0, "the reader loop never got a chance to run");
	}

	// ---- input validation ---------------------------------------------------------

	[Fact]
	public async Task WriteAsync_DuplicateDirectoryNameAcrossItems_Throws()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();

		await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(
			library,
			[MakeItem(Guid.NewGuid(), directoryName: "same"), MakeItem(Guid.NewGuid(), directoryName: "same")],
			CancellationToken.None));
	}

	[Fact]
	public async Task WriteAsync_ItemWithNoFiles_Throws()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		ContentLibraryItemWrite empty = MakeItem(Guid.NewGuid()) with { Files = [] };

		await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(library, [empty], CancellationToken.None));
	}

	[Theory]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("nested/traversal")]
	public async Task WriteAsync_UnsafeDirectoryName_Throws(string directoryName)
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		ContentLibraryItemWrite item = MakeItem(Guid.NewGuid(), directoryName: directoryName);

		await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(library, [item], CancellationToken.None));
	}
}
