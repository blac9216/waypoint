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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Infrastructure.ContentLibraries;

/// <inheritdoc cref="IContentLibraryWriter"/>
public sealed class VcspContentLibraryWriter : IContentLibraryWriter
{
	// The VCSP wire format is camelCase (vcspVersion, itemsHref, selfHref, ...) --
	// deliberately NOT Waypoint.Core.Serialization.WaypointJsonOptions's snake_case,
	// which is this repo's OWN API convention and has nothing to do with a third-party
	// wire protocol vCenter parses. WriteIndented is a debugging nicety; the protocol
	// has no opinion on whitespace.
	private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private static readonly IReadOnlyDictionary<string, object> EmptyProperties =
		new Dictionary<string, object>(0, StringComparer.Ordinal);

	private readonly TimeProvider _clock;

	public VcspContentLibraryWriter(TimeProvider? clock = null)
	{
		_clock = clock ?? TimeProvider.System;
	}

	public async Task WriteAsync(ContentLibrary library, IReadOnlyList<ContentLibraryItemWrite> items, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(library);
		ArgumentNullException.ThrowIfNull(items);

		HashSet<string> seenDirectoryNames = new(StringComparer.Ordinal);
		HashSet<Guid> seenIds = new();
		foreach (ContentLibraryItemWrite item in items)
		{
			ValidateItem(item);
			if (!seenDirectoryNames.Add(item.DirectoryName))
			{
				throw new ArgumentException($"Duplicate item directory '{item.DirectoryName}' in one write.", nameof(items));
			}

			if (!seenIds.Add(item.Id))
			{
				throw new ArgumentException($"Duplicate item id '{item.Id}' in one write.", nameof(items));
			}
		}

		string libJsonPath = Path.Combine(library.DiskPath, "lib.json");
		string itemsJsonPath = Path.Combine(library.DiskPath, "items.json");

		LibJson? previousLib = await ReadExistingAsync<LibJson>(libJsonPath, cancellationToken).ConfigureAwait(false);
		ItemsJson? previousItems = await ReadExistingAsync<ItemsJson>(itemsJsonPath, cancellationToken).ConfigureAwait(false);
		Dictionary<string, ItemJson> previousById = new(StringComparer.Ordinal);
		foreach (ItemJson previousItem in previousItems?.Items ?? [])
		{
			previousById[previousItem.Id] = previousItem;
		}

		string nowIso = _clock.GetUtcNow().UtcDateTime.ToString("o");

		// Pass 1 (in memory only -- nothing is written to disk yet): compute each
		// item's final ItemJson plus whether ANY item's content changed, per research
		// #1032's rule that lib.json.version bumps once for the whole write, not once
		// per item.
		List<(string DirectoryName, ItemJson Document)> resolved = new(items.Count);
		bool anyItemChanged = false;
		HashSet<string> newIds = new(StringComparer.Ordinal);

		foreach (ContentLibraryItemWrite item in items)
		{
			string id = ItemUrn(item.Id);
			newIds.Add(id);
			string directorySegment = Uri.EscapeDataString(item.DirectoryName);
			string selfHref = $"{directorySegment}/item.json";
			string etag = ComputeEtag(item.Files);
			List<ItemFileJson> files = item.Files
				.Select(file => new ItemFileJson(file.Name, file.Size, etag, [$"{directorySegment}/{Uri.EscapeDataString(file.Name)}"]))
				.ToList();

			if (previousById.TryGetValue(id, out ItemJson? prior))
			{
				string priorEtag = prior.Files.Count > 0 ? prior.Files[0].Etag : string.Empty;
				bool contentChanged = !string.Equals(priorEtag, etag, StringComparison.Ordinal);
				string version = contentChanged ? (ParseCounter(prior.Version) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : prior.Version;
				anyItemChanged |= contentChanged;

				resolved.Add((item.DirectoryName, new ItemJson(
					prior.Created,
					item.Description,
					version,
					id,
					item.Name,
					selfHref,
					files,
					item.Type,
					EmptyProperties)));
			}
			else
			{
				anyItemChanged = true;
				resolved.Add((item.DirectoryName, new ItemJson(
					nowIso,
					item.Description,
					"1",
					id,
					item.Name,
					selfHref,
					files,
					item.Type,
					EmptyProperties)));
			}
		}

		// An item present before this call but absent from `items` is a removal --
		// still a library change even though nothing in the surviving item set itself
		// changed content.
		bool anyItemRemoved = previousById.Keys.Any(existingId => !newIds.Contains(existingId));
		bool isFirstWrite = previousLib is null;
		bool libraryChanged = isFirstWrite || anyItemChanged || anyItemRemoved;

		string libId = previousLib?.Id ?? LibUrn(Guid.NewGuid());
		string libCreated = previousLib?.Created ?? nowIso;
		long priorVersion = previousLib is not null ? ParseCounter(previousLib.Version) : 0;
		long newVersion = libraryChanged ? priorVersion + 1 : priorVersion;
		// contentVersion is fixed for the library's lifetime once assigned -- research
		// #1032: it is not a sync signal and this writer never advances it (the
		// sibling's inverted defect this issue exists to not repeat).
		string contentVersion = previousLib?.ContentVersion ?? "1";

		LibJson lib = new("2", newVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), contentVersion, library.Name, libId, libCreated, "items.json", LibCapabilitiesJson.HttpGetOnly);
		ItemsJson itemsDocument = new(resolved.Select(r => r.Document).ToList());

		// Write order matters for protocol safety, not just for atomicity of each
		// individual file: every item.json lands first, then items.json (which
		// references them), then lib.json LAST -- a subscriber only learns there is
		// anything new by polling lib.json.version, so it must never observe a bumped
		// version before the documents it points at already exist in full.
		foreach ((string directoryName, ItemJson document) in resolved)
		{
			string itemDirectory = Path.Combine(library.DiskPath, directoryName);
			Directory.CreateDirectory(itemDirectory);
			await WriteJsonAtomicAsync(Path.Combine(itemDirectory, "item.json"), document, cancellationToken).ConfigureAwait(false);
		}

		await WriteJsonAtomicAsync(itemsJsonPath, itemsDocument, cancellationToken).ConfigureAwait(false);
		await WriteJsonAtomicAsync(libJsonPath, lib, cancellationToken).ConfigureAwait(false);
	}

	private static void ValidateItem(ContentLibraryItemWrite item)
	{
		ArgumentNullException.ThrowIfNull(item);
		ArgumentException.ThrowIfNullOrWhiteSpace(item.Name);
		ArgumentException.ThrowIfNullOrWhiteSpace(item.Type);
		if (!ContentLibraryItemTypes.All.Contains(item.Type, StringComparer.Ordinal))
		{
			throw new ArgumentException($"'{item.Type}' is not a valid content library item type.", nameof(item));
		}

		if (item.Files.Count == 0)
		{
			throw new ArgumentException($"Item '{item.Name}' has no files.", nameof(item));
		}

		ValidateDirectoryName(item.DirectoryName);
	}

	/// <summary>
	/// Same shape of guard as <c>ContentLibraryRepository.ResolveDiskPath</c>: a single
	/// path segment, no <c>.</c>/<c>..</c>, no separators, never absolute -- this is
	/// the code that actually combines an item-supplied string with a real filesystem
	/// path, so it is validated here regardless of what any caller above it already
	/// checked.
	/// </summary>
	private static void ValidateDirectoryName(string directoryName)
	{
		if (string.IsNullOrWhiteSpace(directoryName)
			|| directoryName is "." or ".."
			|| Path.GetFileName(directoryName) != directoryName
			|| Path.IsPathRooted(directoryName))
		{
			throw new ArgumentException($"'{directoryName}' is not a valid item directory name.", nameof(directoryName));
		}
	}

	/// <summary>
	/// The item-level change token (research #1032 "etag semantics"): a SHA-256 over a
	/// deterministic, order-independent rendering of every file's name/size/content
	/// hash. Every file in the item shares this one value, matching the reference
	/// publisher and the vendor's own library -- the contract is only "changes when the
	/// content changes", never a per-file digest.
	/// </summary>
	private static string ComputeEtag(IReadOnlyList<ContentLibraryItemFileWrite> files)
	{
		StringBuilder builder = new();
		foreach (ContentLibraryItemFileWrite file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
		{
			builder.Append(file.Name).Append(' ').Append(file.Size).Append(' ').Append(file.ContentHash).Append('\n');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static long ParseCounter(string value) => long.TryParse(value, out long parsed) ? parsed : 0;

	private static string LibUrn(Guid id) => $"urn:uuid:{id}";

	private static string ItemUrn(Guid id) => $"urn:uuid:{id}";

	private static async Task<T?> ReadExistingAsync<T>(string path, CancellationToken cancellationToken)
		where T : class
	{
		if (!File.Exists(path))
		{
			return null;
		}

		await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return await JsonSerializer.DeserializeAsync<T>(stream, WireOptions, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The atomic-write primitive every document this writer produces goes through:
	/// serialize into a same-directory temp file, then rename it over the target --
	/// the same write-temp-then-rename pattern <c>DepotIdentityTool.SeedMachineId</c>
	/// uses for the machine_id file (issue #760) and <c>CatalogPullJobHandler</c> uses
	/// for the active catalog. A concurrent reader of <paramref name="targetPath"/>
	/// therefore only ever observes the file's prior fully-written content or its new
	/// fully-written content, never a truncated write-in-progress. Cancelling before
	/// the rename leaves <paramref name="targetPath"/> completely untouched: whatever
	/// was there before this call (or nothing, if this is the first write) survives
	/// exactly as it was, and no partial temp artifact is left behind either.
	/// </summary>
	private static async Task WriteJsonAtomicAsync<T>(string targetPath, T document, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string directory = Path.GetDirectoryName(targetPath)!;
		Directory.CreateDirectory(directory);
		string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				await JsonSerializer.SerializeAsync(stream, document, WireOptions, cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(tempPath, targetPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
	}
}
