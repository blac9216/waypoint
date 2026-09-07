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
		// never creates a new item identity. Stale files from a prior upload under a
		// different name are removed first so this stays a one-file-per-item directory
		// rather than accumulating orphaned bytes across renamed re-uploads.
		string itemDirectory = Path.Combine(library.DiskPath, existing.DirectoryName);
		foreach (ContentLibraryItemFileWrite priorFile in existing.Files)
		{
			if (!string.Equals(priorFile.Name, fileName, StringComparison.Ordinal))
			{
				string stalePath = Path.Combine(itemDirectory, priorFile.Name);
				if (File.Exists(stalePath))
				{
					File.Delete(stalePath);
				}
			}
		}

		ContentLibraryItemFileWrite file = await WriteFileAsync(itemDirectory, fileName, content, cancellationToken).ConfigureAwait(false);
		string type = InferType(fileName);

		ContentLibraryItemUpdateOutcome outcome = await _items.UpdateAsync(
			libraryId, itemId, fileName, type, description ?? string.Empty, [file], cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryItemUpdateOutcome.NotFound)
		{
			return (ContentLibraryItemOperationOutcome.ItemNotFound, null);
		}

		await RepublishAsync(library, cancellationToken).ConfigureAwait(false);
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

		string itemDirectory = Path.Combine(library.DiskPath, existing.DirectoryName);
		if (Directory.Exists(itemDirectory))
		{
			Directory.Delete(itemDirectory, recursive: true);
		}

		await RepublishAsync(library, cancellationToken).ConfigureAwait(false);
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
	/// </summary>
	private static async Task<ContentLibraryItemFileWrite> WriteFileAsync(
		string itemDirectory, string fileName, Stream content, CancellationToken cancellationToken)
	{
		string filePath = Path.Combine(itemDirectory, fileName);
		using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		long size = 0;

		await using (FileStream output = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			byte[] buffer = new byte[81920];
			int read;
			while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
			{
				await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				hasher.AppendData(buffer, 0, read);
				size += read;
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
	/// combines an operator-supplied file name with a real filesystem path.
	/// </summary>
	private static void ValidateFileName(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName)
			|| fileName is "." or ".."
			|| Path.GetFileName(fileName) != fileName
			|| Path.IsPathRooted(fileName))
		{
			throw new ArgumentException($"'{fileName}' is not a valid item file name.", nameof(fileName));
		}
	}
}
