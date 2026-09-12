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

using System.Runtime.Versioning;
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
		// Derived from directoryName (rather than a fixed literal) so tests that write
		// several items with distinct directory names in one call never collide with
		// issue #1681's in-write item-name-uniqueness check.
		Name: $"Invented Item ({directoryName})",
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
	public async Task WriteAsync_CancelledMidWrite_LeavesPriorLibraryTreeCompletelyUntouched()
	{
		ContentLibrary library = CreateLibrary();

		await CreateWriter().WriteAsync(library, [MakeItem(Guid.NewGuid(), directoryName: "existing-item")], CancellationToken.None);
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");
		string libJsonPath = Path.Combine(library.DiskPath, "lib.json");
		byte[] itemsBefore = File.ReadAllBytes(itemsJsonPath);
		byte[] libBefore = File.ReadAllBytes(libJsonPath);

		// A wide brand-new item set gives the writer plenty of items still queued
		// behind the one this test cancels on, so a bug that let the loop run past
		// the cancellation would have a wide, reliable window to be caught.
		const int newItemCount = 300;
		List<ContentLibraryItemWrite> newItems = Enumerable.Range(0, newItemCount)
			.Select(i => MakeItem(Guid.NewGuid(), directoryName: $"new-item-{i:D4}"))
			.ToList();
		string firstNewItemJson = Path.Combine(library.DiskPath, "new-item-0000", "item.json");
		string secondNewItemJson = Path.Combine(library.DiskPath, "new-item-0001", "item.json");

		// Issue #1691: earlier revisions raced a filesystem poll (first a
		// Task.Run/Task.Delay(1) loop -- issue #1811 -- then a dedicated spin-loop
		// Thread) against the writer's own progress to catch the cancellation
		// mid-write, and needed a teardown `finally` to keep a failed poll from
		// leaking that thread (issue #1878). Both problems existed only because the
		// test had no way to be told directly when the writer's write reached a
		// chosen point. The onTempFileWritten seam removes the race entirely: it
		// fires strictly BEFORE the rename, with the temp file already on disk, so
		// cancelling from inside it is a genuine, deterministic mid-write
		// cancellation on EVERY run -- never a wall-clock race, and nothing to leak
		// or tear down. Cancelling on the SECOND new item (rather than the first)
		// additionally leaves one full new item.json committed beforehand, proving
		// real per-item writes happened before the interruption.
		using CancellationTokenSource cts = new();
		VcspContentLibraryWriter writer = new(_clock, onTempFileWritten: targetPath =>
		{
			if (targetPath == secondNewItemJson)
			{
				cts.Cancel();
			}
		});

		// Simulates a writer killed mid-run: a cancellation that fires after some
		// item.json files are committed but before items.json/lib.json are ever
		// touched must leave both of those documents byte-for-byte the same file
		// they were before this call, and must leave no partial temp artifact behind
		// for any document the cancellation interrupted.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => writer.WriteAsync(library, newItems, cts.Token));

		Assert.True(File.Exists(firstNewItemJson), "the cancellation fired before any new item.json was written -- this run did not exercise a mid-write cancellation");
		Assert.Equal(itemsBefore, File.ReadAllBytes(itemsJsonPath));
		Assert.Equal(libBefore, File.ReadAllBytes(libJsonPath));
		Assert.Empty(Directory.GetFiles(library.DiskPath, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task WriteAsync_WritesEveryItemJsonBeforeItemsJson_AndItemsJsonBeforeLibJson()
	{
		ContentLibrary library = CreateLibrary();
		List<string> writeOrder = [];
		VcspContentLibraryWriter writer = new(_clock, onDocumentWritten: writeOrder.Add);

		// Issue #1680 gap 2: the write-order guarantee IContentLibraryWriter's own XML
		// docs promise ("a subscriber never observes a bumped lib.json.version before
		// the documents it points at exist") was asserted nowhere -- reordering the
		// three WriteJsonAtomicAsync calls in WriteAsync would have been caught by
		// nothing. The onDocumentWritten seam fires in the writer's own write order,
		// so recording it directly proves the order without timing or polling.
		await writer.WriteAsync(
			library,
			[MakeItem(Guid.NewGuid(), directoryName: "item-a"), MakeItem(Guid.NewGuid(), directoryName: "item-b")],
			CancellationToken.None);

		Assert.Equal(["item-a", "item-b", "items.json", "lib.json"], writeOrder);
	}

	[Fact]
	public async Task WriteAsync_ItemHrefsAndSelfHref_PercentEncodeNamesThatNeedIt()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		// Issue #1680 gap 1: the only href test ("my-item"/"disk.vmdk") needs no
		// escaping at all, so removing Uri.EscapeDataString from the href
		// construction entirely would not fail a single existing test. A space, a
		// '+', a '%' and a non-ASCII character each force real percent-encoding.
		const string directoryName = "my item+50%é";
		const string fileName = "a b+c%d é.vmdk";

		await writer.WriteAsync(
			library,
			[MakeItem(Guid.NewGuid(), directoryName: directoryName, fileName: fileName)],
			CancellationToken.None);

		ItemJson item = ReadItems(library).Items.Single();
		string expectedDirectorySegment = Uri.EscapeDataString(directoryName);
		string expectedFileSegment = Uri.EscapeDataString(fileName);
		Assert.Equal($"{expectedDirectorySegment}/item.json", item.SelfHref);
		string href = Assert.Single(item.Files.Single().Hrefs);
		Assert.Equal($"{expectedDirectorySegment}/{expectedFileSegment}", href);

		// Percent-decoding the hrefs back must resolve to the REAL on-disk names --
		// a literal Path.Combine of the encoded segments (as the pre-existing ASCII
		// test does) is only a valid resolution for names that need no encoding.
		string decodedSelfHrefPath = Uri.UnescapeDataString(item.SelfHref).Replace('/', Path.DirectorySeparatorChar);
		Assert.True(File.Exists(Path.Combine(library.DiskPath, decodedSelfHrefPath)));
		string decodedHrefPath = Uri.UnescapeDataString(href).Replace('/', Path.DirectorySeparatorChar);
		Assert.Equal(Path.Combine(directoryName, fileName), decodedHrefPath);
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

		int expectedItemCount = items.Count;
		using CancellationTokenSource stop = new();
		int readCount = 0;
		// Issue #1680 gap 3: asserting only readCount > 0 lets a single read landing
		// entirely before the rewrites even begin satisfy the test -- it never proves
		// any read observed a FULL document. Every parsed read below must carry the
		// full, unchanged item count: a torn rename that dropped or truncated part of
		// the item array would surface here even though the bytes still happen to
		// parse as valid JSON.
		Task readerTask = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				byte[] bytes = await File.ReadAllBytesAsync(itemsJsonPath, CancellationToken.None);
				// A torn/partial rename would surface here as a JSON parse failure --
				// File.Move's rename is the only thing standing between "always valid"
				// and this throwing.
				using JsonDocument document = JsonDocument.Parse(bytes);
				int observedItemCount = document.RootElement.GetProperty("items").GetArrayLength();
				Assert.Equal(expectedItemCount, observedItemCount);
				Interlocked.Increment(ref readCount);
			}
		});

		const int rewriteCount = 25;
		for (int rewrite = 0; rewrite < rewriteCount; rewrite++)
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
		// A meaningfully large read count, not merely > 0: proves the reader had a
		// real chance to interleave with the 25 rewrites above rather than getting one
		// lucky read in edgewise.
		Assert.True(readCount >= rewriteCount, $"the reader loop only completed {readCount} reads against {rewriteCount} rewrites -- too few to meaningfully exercise interleaving");
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

	// ---- issue #1679: invariant-culture counters, no silent reset -----------------

	[Fact]
	public async Task ComputeEtag_ForFixedFileSet_PinsToLiteralHexValue()
	{
		// Issue #1679: locks the etag's exact bytes for a fixed input, which pins
		// determinism for free -- any future change to ComputeEtag's hash input
		// (separator, field order, culture-sensitive formatting) fails this test.
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		ContentLibraryItemWrite item = new(
			Guid.NewGuid(),
			"item-one",
			Name: "Fixed Item",
			Type: ContentLibraryItemTypes.Other,
			Description: "",
			Files: [new ContentLibraryItemFileWrite("payload.txt", Size: 123456, ContentHash: "hash-fixed")]);

		await writer.WriteAsync(library, [item], CancellationToken.None);

		ItemFileJson file = ReadItems(library).Items.Single().Files.Single();
		Assert.Equal("0ad1bbf65460c328953fda2b427c895f8f989b7a4bd27f2b80120ce5857b5165", file.Etag);
	}

	[Fact]
	public async Task WriteAsync_UnparseableItemVersionOnDisk_ThrowsInsteadOfSilentlyResettingToOne()
	{
		// Issue #1679: a hand-edited or truncated item.json version must be a visible
		// error, never a silent regression to "1" -- a subscriber that already saw a
		// high version and now sees "1" is the exact "subscriber never notices
		// updates" failure mode this writer's whole design exists to prevent.
		ContentLibrary library = CreateLibrary();
		Guid itemId = Guid.NewGuid();
		await CreateWriter().WriteAsync(library, [MakeItem(itemId, directoryName: "item-one")], CancellationToken.None);

		// previousById is built from items.json alone when it is present (issue #1682's
		// per-item fallback only kicks in when it is missing), so corrupting THIS
		// document's item version is what the writer actually reads back.
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");
		ItemsJson itemsDocument = ReadItems(library);
		ItemJson corruptedItem = itemsDocument.Items.Single() with { Version = "not-a-number" };
		File.WriteAllText(itemsJsonPath, JsonSerializer.Serialize(new ItemsJson([corruptedItem]), WireOptions));

		await Assert.ThrowsAsync<InvalidDataException>(() => CreateWriter().WriteAsync(
			library,
			[MakeItem(itemId, directoryName: "item-one", contentHash: "hash-v2")],
			CancellationToken.None));
	}

	[Fact]
	public async Task WriteAsync_UnparseableLibVersionOnDisk_ThrowsInsteadOfSilentlyResettingToZero()
	{
		ContentLibrary library = CreateLibrary();
		await CreateWriter().WriteAsync(library, [MakeItem(Guid.NewGuid())], CancellationToken.None);

		string libJsonPath = Path.Combine(library.DiskPath, "lib.json");
		LibJson corruptedLib = ReadLib(library) with { Version = "garbage" };
		File.WriteAllText(libJsonPath, JsonSerializer.Serialize(corruptedLib, WireOptions));

		await Assert.ThrowsAsync<InvalidDataException>(() => CreateWriter().WriteAsync(
			library,
			[MakeItem(Guid.NewGuid(), directoryName: "item-two")],
			CancellationToken.None));
	}

	// ---- issue #1681: name cap, in-write uniqueness, null description -------------

	[Fact]
	public async Task WriteAsync_ItemNameOverEightyChars_IsTruncatedPreservingExtension()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		string longName = new string('a', 90) + ".vmdk";
		ContentLibraryItemWrite item = MakeItem(Guid.NewGuid()) with { Name = longName };

		await writer.WriteAsync(library, [item], CancellationToken.None);

		string writtenName = ReadItems(library).Items.Single().Name;
		Assert.Equal(80, writtenName.Length);
		Assert.EndsWith(".vmdk", writtenName, StringComparison.Ordinal);
		Assert.Equal(new string('a', 75) + ".vmdk", writtenName);
	}

	[Fact]
	public async Task WriteAsync_ItemNameAtOrUnderEightyChars_IsUnchanged()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		string exactName = new string('b', 80);
		ContentLibraryItemWrite item = MakeItem(Guid.NewGuid()) with { Name = exactName };

		await writer.WriteAsync(library, [item], CancellationToken.None);

		Assert.Equal(exactName, ReadItems(library).Items.Single().Name);
	}

	[Fact]
	public async Task WriteAsync_DuplicateItemNamesInOneWrite_Throws()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();

		await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(
			library,
			[
				MakeItem(Guid.NewGuid(), directoryName: "one") with { Name = "Same Name" },
				MakeItem(Guid.NewGuid(), directoryName: "two") with { Name = "Same Name" },
			],
			CancellationToken.None));
	}

	[Fact]
	public async Task WriteAsync_NullDescription_EmitsEmptyString()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		// A non-nullable-oblivious caller (research #1032: the wire format pins `""`).
		ContentLibraryItemWrite item = MakeItem(Guid.NewGuid()) with { Description = null! };

		await writer.WriteAsync(library, [item], CancellationToken.None);

		Assert.Equal(string.Empty, ReadItems(library).Items.Single().Description);
	}

	[Fact]
	[UnsupportedOSPlatform("windows")]
	public async Task WriteAsync_TempFileDeleteFailsDuringCleanup_DoesNotMaskOriginalException()
	{
		// Issue #1681: the atomic-write `finally`'s File.Delete must never mask the
		// exception already propagating out of the `try`. Removing write permission on
		// the temp file's directory right after it is written (via the onTempFileWritten
		// seam), then cancelling, makes BOTH happen on the same call: the ORIGINAL
		// OperationCanceledException from ThrowIfCancellationRequested, and a SECONDARY
		// UnauthorizedAccessException from the doomed File.Delete in `finally`. Only the
		// original must surface. Unix file modes are POSIX-only (CI and this dev
		// environment both are); skip on Windows rather than fail the build there.
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		ContentLibrary library = CreateLibrary();
		using CancellationTokenSource cts = new();
		string? lockedDirectory = null;
		VcspContentLibraryWriter writer = new(_clock, onTempFileWritten: targetPath =>
		{
			lockedDirectory = Path.GetDirectoryName(targetPath)!;
			SetDirectoryWritable(lockedDirectory, writable: false);
			cts.Cancel();
		});

		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => writer.WriteAsync(library, [MakeItem(Guid.NewGuid())], cts.Token));
		}
		finally
		{
			if (lockedDirectory is not null)
			{
				SetDirectoryWritable(lockedDirectory, writable: true);
			}
		}
	}

	[UnsupportedOSPlatform("windows")]
	private static void SetDirectoryWritable(string directory, bool writable) => File.SetUnixFileMode(
		directory,
		writable
			? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
			: UnixFileMode.UserRead | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

	// ---- issue #1682: items.json-missing recovery ----------------------------------

	[Fact]
	public async Task WriteAsync_ItemsJsonMissing_RebuildsFromPerItemJsonFiles_PreservingCreatedAndVersion()
	{
		ContentLibrary library = CreateLibrary();
		VcspContentLibraryWriter writer = CreateWriter();
		Guid itemId = Guid.NewGuid();

		await writer.WriteAsync(library, [MakeItem(itemId, directoryName: "item-one")], CancellationToken.None);
		_clock.Advance(TimeSpan.FromHours(1));
		await writer.WriteAsync(library, [MakeItem(itemId, directoryName: "item-one", contentHash: "hash-v2")], CancellationToken.None);
		ItemJson before = ReadItems(library).Items.Single();
		Assert.Equal("2", before.Version);

		// Simulates an operator deleting items.json (or a partial backup restore) while
		// lib.json and the per-item item.json files survive.
		File.Delete(Path.Combine(library.DiskPath, "items.json"));

		_clock.Advance(TimeSpan.FromHours(1));
		await writer.WriteAsync(library, [MakeItem(itemId, directoryName: "item-one", contentHash: "hash-v2")], CancellationToken.None);

		ItemJson after = ReadItems(library).Items.Single();
		// Without the #1682 fix, previousById would be empty (items.json is gone), the
		// item would be treated as brand new, `created` would reset to `_clock`'s
		// current instant, and `version` would reset to "1".
		Assert.Equal(before.Created, after.Created);
		Assert.Equal(before.Version, after.Version);
		Assert.Equal(before.Id, after.Id);
	}
}
