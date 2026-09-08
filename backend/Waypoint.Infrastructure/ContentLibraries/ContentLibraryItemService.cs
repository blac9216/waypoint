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
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Infrastructure.ContentLibraries;

/// <inheritdoc cref="IContentLibraryItemService"/>
public sealed class ContentLibraryItemService : IContentLibraryItemService
{
	private readonly IContentLibraryRepository _libraries;
	private readonly IContentLibraryItemRepository _items;
	private readonly IContentLibraryWriter _writer;

	public ContentLibraryItemService(IContentLibraryRepository libraries, IContentLibraryItemRepository items, IContentLibraryWriter writer)
	{
		ArgumentNullException.ThrowIfNull(libraries);
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(writer);
		_libraries = libraries;
		_items = items;
		_writer = writer;
	}

	public async Task<(ContentLibraryItemOperationOutcome Outcome, ContentLibraryItem? Item)> AddAsync(
		Guid libraryId, string fileName, Stream content, string? description, CancellationToken cancellationToken)
	{
		ValidateFileName(fileName);
		ArgumentNullException.ThrowIfNull(content);

		ContentLibrary? library = await _libraries.GetAsync(libraryId, cancellationToken).ConfigureAwait(false);
		if (library is null)
		{
			return (ContentLibraryItemOperationOutcome.LibraryNotFound, null);
		}

		Guid id = Guid.NewGuid();
		string directoryName = id.ToString("N");
		string itemDirectory = Path.Combine(library.DiskPath, directoryName);
		Directory.CreateDirectory(itemDirectory);

		ContentLibraryItemFileWrite file = await WriteFileAsync(itemDirectory, fileName, content, cancellationToken).ConfigureAwait(false);
		string type = InferType(fileName);

		(ContentLibraryItemAddOutcome outcome, ContentLibraryItem? item) = await _items.AddAsync(
			id, libraryId, directoryName, fileName, type, description ?? string.Empty, [file], cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryItemAddOutcome.LibraryNotFound)
		{
			// Lost a race against a concurrent library delete between the GetAsync above
			// and this insert. The directory this call just created is orphaned on disk,
			// same shape of leftover ContentLibraryRepository.DeleteAsync already accepts
			// for a directory that appears after its own emptiness check -- not
			// compensated here, surfaced honestly as a library-not-found outcome.
			return (ContentLibraryItemOperationOutcome.LibraryNotFound, null);
		}

		await RepublishAsync(library, cancellationToken).ConfigureAwait(false);
		return (ContentLibraryItemOperationOutcome.Succeeded, item);
	}

	public async Task<(ContentLibraryItemOperationOutcome Outcome, ContentLibraryItem? Item)> UpdateAsync(
		Guid libraryId, Guid itemId, string fileName, Stream content, string? description, CancellationToken cancellationToken)
	{
		ValidateFileName(fileName);
		ArgumentNullException.ThrowIfNull(content);

		ContentLibrary? library = await _libraries.GetAsync(libraryId, cancellationToken).ConfigureAwait(false);
		if (library is null)
		{
			return (ContentLibraryItemOperationOutcome.LibraryNotFound, null);
		}

		ContentLibraryItem? existing = await _items.GetAsync(libraryId, itemId, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return (ContentLibraryItemOperationOutcome.ItemNotFound, null);
		}

		// Reuses the SAME directory (== the item's own id) -- issue #1396 AC: an update
		// never creates a new item identity.
		string itemDirectory = Path.Combine(library.DiskPath, existing.DirectoryName);

		// The ORDER of the four steps below is the atomicity contract, not an
		// implementation detail. (1) The new payload lands via a same-directory temp
		// file plus atomic rename (WriteFileAsync), so the file at the href items.json
		// already advertises is never absent, truncated, or half-written for any
		// instant -- a reader either gets the complete prior bytes or the complete new
		// bytes. (2) The identity row moves. (3) The republish is what advertises the
		// new name/size/content_hash, so it happens only once those bytes are already
		// in place; a VCSP subscriber learns there is anything new solely by seeing
		// lib.json.version move, and by then everything the new documents point at
		// exists in full. (4) Stale files from a prior upload under a DIFFERENT name
		// are deleted last, once nothing advertises them any more, keeping this a
		// one-file-per-item directory without ever unlinking a file items.json still
		// names. Doing (4) first -- the shape this method shipped with, caught in
		// review round 1 -- left a window in which items.json advertised a file that
		// was already gone, and truncating the final path in place left one in which
		// it advertised a size and hash matching neither the file on disk nor
		// anything else.
		ContentLibraryItemFileWrite file = await WriteFileAsync(itemDirectory, fileName, content, cancellationToken).ConfigureAwait(false);
		string type = InferType(fileName);

		ContentLibraryItemUpdateOutcome outcome = await _items.UpdateAsync(
			libraryId, itemId, fileName, type, description ?? string.Empty, [file], cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryItemUpdateOutcome.NotFound)
		{
			// Lost a race against a concurrent remove of this item. The payload just
			// renamed into place is orphaned inside a directory the winning remover
			// deletes; nothing advertises it, so there is nothing reader-visible to
			// compensate -- same shape of leftover AddAsync's library-delete race
			// already accepts, surfaced honestly as item-not-found.
			return (ContentLibraryItemOperationOutcome.ItemNotFound, null);
		}

		await RepublishAsync(library, cancellationToken).ConfigureAwait(false);

		foreach (ContentLibraryItemFileWrite priorFile in existing.Files)
		{
			if (!string.Equals(priorFile.Name, fileName, StringComparison.Ordinal))
			{
				string stalePath = ResolveItemFilePath(itemDirectory, priorFile.Name);
				if (File.Exists(stalePath))
				{
					File.Delete(stalePath);
				}
			}
		}

		ContentLibraryItem updated = (await _items.GetAsync(libraryId, itemId, cancellationToken).ConfigureAwait(false))!;
		return (ContentLibraryItemOperationOutcome.Succeeded, updated);
	}

	public async Task<ContentLibraryItemOperationOutcome> RemoveAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken)
	{
		ContentLibrary? library = await _libraries.GetAsync(libraryId, cancellationToken).ConfigureAwait(false);
		if (library is null)
		{
			return ContentLibraryItemOperationOutcome.LibraryNotFound;
		}

		ContentLibraryItem? existing = await _items.GetAsync(libraryId, itemId, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return ContentLibraryItemOperationOutcome.ItemNotFound;
		}

		ContentLibraryItemRemoveOutcome outcome = await _items.RemoveAsync(libraryId, itemId, cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryItemRemoveOutcome.NotFound)
		{
			return ContentLibraryItemOperationOutcome.ItemNotFound;
		}

		// Republish BEFORE unlinking anything (issue #1396 review round 1, F3). The
		// republish rewrites items.json/lib.json from the identity table, which no
		// longer holds this item, so it is the step that stops advertising the
		// directory -- deleting the directory first left a window in which items.json
		// still listed an item whose href resolved to a missing path. After the
		// republish nothing points at these bytes and removing them is invisible.
		await RepublishAsync(library, cancellationToken).ConfigureAwait(false);

		string itemDirectory = Path.Combine(library.DiskPath, existing.DirectoryName);
		if (Directory.Exists(itemDirectory))
		{
			Directory.Delete(itemDirectory, recursive: true);
		}

		return ContentLibraryItemOperationOutcome.Succeeded;
	}

	/// <summary>
	/// Re-lists the library's complete item set from the identity table and republishes
	/// it through <see cref="IContentLibraryWriter"/> -- the writer's own contract is a
	/// full-library rewrite, never an incremental patch, so every mutation in this
	/// service re-supplies the whole set rather than describing only what changed.
	/// </summary>
	private async Task RepublishAsync(ContentLibrary library, CancellationToken cancellationToken)
	{
		IReadOnlyList<ContentLibraryItem> items = await _items.ListAsync(library.Id, cancellationToken).ConfigureAwait(false);
		List<ContentLibraryItemWrite> writes = [.. items.Select(item => new ContentLibraryItemWrite(
			item.Id, item.DirectoryName, item.Name, item.Type, item.Description, item.Files))];
		await _writer.WriteAsync(library, writes, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Streams <paramref name="content"/> to <c>&lt;itemDirectory&gt;/&lt;fileName&gt;</c>
	/// while computing its SHA-256 digest in the same pass (decision 8's
	/// always-self-hash rule -- this service never trusts a caller-supplied hash), then
	/// returns the <see cref="ContentLibraryItemFileWrite"/> the writer's own
	/// per-write item-changed diff (<c>VcspContentLibraryWriter</c>) needs.
	/// <para>
	/// The bytes go to a same-directory temp file that is flushed to disk and then
	/// renamed over the final path -- the same write-temp-then-rename primitive
	/// <c>VcspContentLibraryWriter.WriteJsonAtomicAsync</c> uses for every VCSP
	/// document, and for the same reason: the payload sits at an href
	/// <c>items.json</c>/<c>item.json</c> already advertise, so a concurrent
	/// subscriber must only ever fetch the complete prior file or the complete new
	/// one, never a truncate-in-progress. Cancelling or failing before the rename
	/// leaves the final path exactly as it was found and no <c>.tmp</c> artifact
	/// behind.
	/// </para>
	/// </summary>
	private static async Task<ContentLibraryItemFileWrite> WriteFileAsync(
		string itemDirectory, string fileName, Stream content, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string filePath = ResolveItemFilePath(itemDirectory, fileName);
		string tempPath = Path.Combine(itemDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
		using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		long size = 0;

		try
		{
			await using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				byte[] buffer = new byte[81920];
				int read;
				while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
				{
					await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
					hasher.AppendData(buffer, 0, read);
					size += read;
				}

				await output.FlushAsync(cancellationToken).ConfigureAwait(false);
				// fsync before the rename, matching DepotIdentityTool.SeedMachineId
				// (issue #760): a directory entry that reaches stable storage ahead of
				// the payload's own data would leave the advertised href pointing at a
				// short file after a host crash.
				output.Flush(flushToDisk: true);
			}

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(tempPath, filePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}

		string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
		return new ContentLibraryItemFileWrite(fileName, size, hash);
	}

	/// <summary>
	/// Type inference from the uploaded file's extension (issue #1396 AC: no operator
	/// input) -- <c>.ovf</c>/<c>.ova</c> are the two VMware OVF packaging conventions,
	/// <c>.iso</c> is the only other type this codebase's runners produce today, and
	/// everything else falls back to the closed vocabulary's catch-all.
	/// </summary>
	private static string InferType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
	{
		".ovf" or ".ova" => ContentLibraryItemTypes.Ovf,
		".iso" => ContentLibraryItemTypes.Iso,
		_ => ContentLibraryItemTypes.Other,
	};

	/// <summary>
	/// Same shape of guard as <c>VcspContentLibraryWriter.ValidateDirectoryName</c> and
	/// <c>ContentLibraryRepository.ResolveDiskPath</c>: a single path segment, no
	/// <c>.</c>/<c>..</c>, no separators, never absolute -- this is the code that
	/// combines an operator-supplied file name with a real filesystem path. Both
	/// separator characters are rejected explicitly rather than left to
	/// <see cref="Path.GetFileName(string)"/>, which on Linux does not treat
	/// <c>\</c> as one and would happily admit <c>sub\dir.iso</c> as a single
	/// segment; the closed set of invalid file-name characters is rejected too, so a
	/// NUL-embedded name cannot reach the filesystem APIs below.
	/// </summary>
	private static void ValidateFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName)
			|| fileName is "." or ".."
			|| fileName.Contains('/', StringComparison.Ordinal)
			|| fileName.Contains('\\', StringComparison.Ordinal)
			|| fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| Path.GetFileName(fileName) != fileName
			|| Path.IsPathRooted(fileName))
		{
			throw new ArgumentException($"'{fileName}' is not a valid item file name.", nameof(fileName));
		}
	}

	/// <summary>
	/// The rooted-resolution half of this repository's escape-guard convention, applied
	/// at the one place a file name becomes a real path exactly as
	/// <c>ContentLibraryRepository.ResolveDiskPath</c> applies it for a library name:
	/// the name-level check above is re-run, then the combined path is fully resolved
	/// and asserted to sit strictly under <paramref name="itemDirectory"/>. A
	/// name-level check alone pins only the names its own author thought of; resolving
	/// and comparing pins the property that actually matters -- nothing this service
	/// writes, and nothing it deletes, can land outside the item's own directory.
	/// </summary>
	private static string ResolveItemFilePath(string itemDirectory, string fileName)
	{
		ValidateFileName(fileName);

		string directoryFullPath = Path.GetFullPath(itemDirectory);
		string filePath = Path.GetFullPath(Path.Combine(directoryFullPath, fileName));
		string directoryWithSeparator = directoryFullPath.EndsWith(Path.DirectorySeparatorChar)
			? directoryFullPath
			: directoryFullPath + Path.DirectorySeparatorChar;
		if (!filePath.StartsWith(directoryWithSeparator, StringComparison.Ordinal))
		{
			throw new ArgumentException($"'{fileName}' does not resolve inside the item directory.", nameof(fileName));
		}

		return filePath;
	}
}
