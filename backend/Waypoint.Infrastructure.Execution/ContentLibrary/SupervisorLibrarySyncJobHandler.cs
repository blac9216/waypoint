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

using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.ContentLibraries;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;

namespace Waypoint.Infrastructure.Execution.ContentLibrary;

/// <summary>
/// The <c>content-library-sync</c> <see cref="JobShape.Simple"/> job handler for the
/// Supervisor lane (issue #1513, epic #1185, split from #1160/#1057): re-serves the
/// depot's already-acquired SUPERVISOR product tree (VM OVAs, the spherelet
/// VIB-depot ZIP, and solution JSON -- <see cref="DepotRelativePaths"/>'s
/// <c>PROD/COMP/SUPERVISOR/...</c> identity, indexed as <see cref="DepotArtifact"/>
/// rows by the existing acquisition lane this issue does not touch) into its own,
/// dedicated VCSP content library via <see cref="IContentLibraryWriter"/> (#1393's
/// writer, extended by #1506/#1678 to accept <see cref="ContentLibraryItemTypes.Other"/>).
///
/// <para>
/// <b>Cross-process disk path (deploy/compose.yaml, issue #1706/#1647):</b> the
/// <c>content-libraries</c> volume is the SAME bytes mounted at three different
/// absolute paths -- <c>/var/lib/waypoint/content-libraries</c> on <c>backend</c>,
/// <c>/srv/content-libraries</c> (read-only) on <c>nginx</c>, and
/// <c>/vcf/ContentLibrary</c> on <c>download-runner</c> (nested under the depot mount
/// deliberately, matching <c>vcf-download-manager.common.ps1</c>'s own
/// <c>$Script:ContentLibPath</c> convention). <see cref="Waypoint.Core.ContentLibraries.ContentLibrary.DiskPath"/>
/// as read from the database is whatever absolute string the PROCESS that created the
/// row resolved it to -- backend's controller resolves it under backend's own root.
/// This handler therefore never trusts that stored value for a filesystem operation;
/// it always re-derives the library's real local path from THIS process's own
/// <see cref="ContentLibraryOptions.RootPath"/> (bound from download-runner's
/// <c>appsettings.json</c> to <c>/vcf/ContentLibrary</c>) plus the library's
/// <see cref="Waypoint.Core.ContentLibraries.ContentLibrary.Name"/> -- the exact same
/// derivation <c>ContentLibraryRepository.ResolveDiskPath</c> uses, just recomputed
/// under this process's own mount.
/// </para>
///
/// <para>
/// Skip-if-current (AC: re-running with no depot change is a no-op): every synced
/// item's <see cref="ContentLibraryItem.Description"/> carries the depot artifact's
/// own <see cref="DepotArtifact.ExternalId"/> (relative path) -- the reconciliation
/// key this handler uses to find "the item this artifact was synced into last time"
/// without needing a new column. An artifact is skipped entirely (no file copy, no
/// row write) when its indexed <see cref="DepotArtifact.Sha256"/> matches the
/// existing item's already-recorded content hash; only a changed or brand-new
/// artifact is copied and republished. A depot artifact that later disappears from
/// the product tree leaves its library item in place (this repo's own
/// never-auto-remove convention -- <c>UnknownCatalogFile</c>,
/// <c>IDepotArtifactRepository</c>'s superseded-not-deleted rows) rather than being
/// silently unpublished; deleting it is an explicit operator action through the
/// existing item CRUD surface.
/// </para>
/// </summary>
public sealed class SupervisorLibrarySyncJobHandler : IJobHandler
{
	/// <summary>
	/// The library's operator-facing name (also its disk directory leaf, research
	/// #1032: one path segment). Lowercase to match this codebase's other
	/// machine-chosen identifiers; distinct from <see cref="SupervisorProduct"/>,
	/// the depot's own (uppercase) product directory name this handler filters on.
	/// </summary>
	public const string LibraryName = "supervisor";

	/// <summary>The depot's own product identifier (<c>PROD/COMP/SUPERVISOR/...</c>) this handler re-serves.</summary>
	public const string SupervisorProduct = "SUPERVISOR";

	private readonly IContentLibraryRepository _libraries;
	private readonly IContentLibraryItemRepository _items;
	private readonly IContentLibraryWriter _writer;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IOptions<ContentLibraryOptions> _contentLibraryOptions;
	private readonly IOptions<CatalogOptions> _catalogOptions;

	public SupervisorLibrarySyncJobHandler(
		IContentLibraryRepository libraries,
		IContentLibraryItemRepository items,
		IContentLibraryWriter writer,
		IDepotArtifactRepository artifacts,
		IOptions<ContentLibraryOptions> contentLibraryOptions,
		IOptions<CatalogOptions> catalogOptions)
	{
		ArgumentNullException.ThrowIfNull(libraries);
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(contentLibraryOptions);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		_libraries = libraries;
		_items = items;
		_writer = writer;
		_artifacts = artifacts;
		_contentLibraryOptions = contentLibraryOptions;
		_catalogOptions = catalogOptions;
	}

	public string JobType => RunTypes.ContentLibrarySync;

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		Waypoint.Core.ContentLibraries.ContentLibrary library = await EnsureLibraryAsync(cancellationToken).ConfigureAwait(false);
		string localDiskPath = Path.Combine(_contentLibraryOptions.Value.RootPath, library.Name);
		Waypoint.Core.ContentLibraries.ContentLibrary localLibrary = library with { DiskPath = localDiskPath };
		Directory.CreateDirectory(localDiskPath);

		List<DepotArtifact> artifacts = await ListSupervisorArtifactsAsync(cancellationToken).ConfigureAwait(false);
		if (artifacts.Count == 0)
		{
			return JobExecutionOutcome.Succeeded($"No present '{SupervisorProduct}' depot artifacts found; nothing to sync.");
		}

		IReadOnlyList<ContentLibraryItem> existingItems = await _items.ListAsync(library.Id, cancellationToken).ConfigureAwait(false);
		Dictionary<string, ContentLibraryItem> existingByRelativePath = new(StringComparer.Ordinal);
		foreach (ContentLibraryItem item in existingItems)
		{
			// An item this handler did not create (empty/foreign Description) never
			// matches a depot artifact -- left untouched, never overwritten.
			if (!string.IsNullOrEmpty(item.Description))
			{
				existingByRelativePath.TryAdd(item.Description, item);
			}
		}

		int created = 0;
		int updated = 0;
		int unchanged = 0;
		int skippedMissing = 0;
		bool anyChange = false;

		foreach (DepotArtifact artifact in artifacts)
		{
			string? sourcePath = ResolveDepotPath(_catalogOptions.Value.DepotPath, artifact.ExternalId);
			if (sourcePath is null || !File.Exists(sourcePath))
			{
				skippedMissing++;
				continue;
			}

			string fileName = artifact.ExternalId.Split('/').Last();
			string type = ClassifyType(fileName);

			if (existingByRelativePath.TryGetValue(artifact.ExternalId, out ContentLibraryItem? existing))
			{
				string? priorHash = existing.Files.Count > 0 ? existing.Files[0].ContentHash : null;
				if (artifact.Sha256 is not null && string.Equals(priorHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
				{
					unchanged++;
					continue;
				}

				string itemDirectory = Path.Combine(localDiskPath, existing.DirectoryName);
				Directory.CreateDirectory(itemDirectory);
				ContentLibraryItemFileWrite file = await CopyIntoLibraryAsync(sourcePath, itemDirectory, fileName, cancellationToken)
					.ConfigureAwait(false);
				await _items.UpdateAsync(library.Id, existing.Id, fileName, type, artifact.ExternalId, [file], cancellationToken).ConfigureAwait(false);
				updated++;
				anyChange = true;
			}
			else
			{
				Guid id = Guid.NewGuid();
				string directoryName = id.ToString("N");
				string itemDirectory = Path.Combine(localDiskPath, directoryName);
				Directory.CreateDirectory(itemDirectory);
				ContentLibraryItemFileWrite file = await CopyIntoLibraryAsync(sourcePath, itemDirectory, fileName, cancellationToken)
					.ConfigureAwait(false);
				await _items.AddAsync(id, library.Id, directoryName, fileName, type, artifact.ExternalId, [file], cancellationToken).ConfigureAwait(false);
				created++;
				anyChange = true;
			}
		}

		if (anyChange)
		{
			IReadOnlyList<ContentLibraryItem> finalItems = await _items.ListAsync(library.Id, cancellationToken).ConfigureAwait(false);
			List<ContentLibraryItemWrite> writes = [.. finalItems.Select(item => new ContentLibraryItemWrite(
				item.Id, item.DirectoryName, item.Name, item.Type, item.Description, item.Files))];
			await _writer.WriteAsync(localLibrary, writes, cancellationToken).ConfigureAwait(false);
		}

		return JobExecutionOutcome.Succeeded(
			$"Supervisor library sync: {created} added, {updated} updated, {unchanged} unchanged, {skippedMissing} skipped (not present on disk).");
	}

	/// <summary>
	/// Resolves the well-known Supervisor library, creating it on the first run.
	/// <see cref="ContentLibraryCreateOutcome.NameTaken"/> covers both "another run
	/// already created it" and a genuine concurrent create race -- either way, the
	/// row now exists and a plain list-and-find resolves it.
	/// </summary>
	private async Task<Waypoint.Core.ContentLibraries.ContentLibrary> EnsureLibraryAsync(CancellationToken cancellationToken)
	{
		(ContentLibraryCreateOutcome outcome, Waypoint.Core.ContentLibraries.ContentLibrary? created) =
			await _libraries.CreateAsync(LibraryName, cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryCreateOutcome.Created)
		{
			return created!;
		}

		IReadOnlyList<Waypoint.Core.ContentLibraries.ContentLibrary> all = await _libraries.ListAsync(cancellationToken).ConfigureAwait(false);
		return all.First(library => string.Equals(library.Name, LibraryName, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>Pages through every <see cref="DepotArtifactStatuses.Present"/> row for <see cref="SupervisorProduct"/> (same bounded-loop shape as <c>SubscriptionEvaluationJobHandler</c>).</summary>
	private async Task<List<DepotArtifact>> ListSupervisorArtifactsAsync(CancellationToken cancellationToken)
	{
		PageRequest page = new() { Limit = 200 };
		List<DepotArtifact> results = [];
		long total;
		do
		{
			(IReadOnlyList<DepotArtifact> items, total) = await _artifacts.ListAsync(
				new DepotArtifactFilter(SupervisorProduct, null, DepotArtifactStatuses.Present), page, cancellationToken).ConfigureAwait(false);
			results.AddRange(items);
			page.Offset += page.Limit;
		}
		while (results.Count < total);

		return results;
	}

	/// <summary>
	/// Research #1032 Q3/issue #1506: OVA is the VCSP OVF item type; the spherelet
	/// VIB-depot ZIP and solution JSON both fall through to the closed vocabulary's
	/// catch-all, matching <c>ContentLibraryItemService.InferType</c>'s identical
	/// extension-only rule (no operator input either way).
	/// </summary>
	private static string ClassifyType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
	{
		".ova" or ".ovf" => ContentLibraryItemTypes.Ovf,
		_ => ContentLibraryItemTypes.Other,
	};

	/// <summary>Same traversal-confinement shape as <c>BinariesDownloadJobHandler.ResolveDestinationPath</c> -- this is the code that turns a DB-stored relative path into a real filesystem path.</summary>
	private static string? ResolveDepotPath(string depotRoot, string relativePath)
	{
		string fullRoot = Path.GetFullPath(depotRoot);
		string fullPath = Path.GetFullPath(Path.Combine(depotRoot, relativePath));
		bool confined = fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
		return confined ? fullPath : null;
	}

	/// <summary>
	/// Copies the depot's bytes into the item's directory via a same-directory temp
	/// file plus atomic rename (the same primitive
	/// <c>ContentLibraryItemService.WriteFileAsync</c>/<c>VcspContentLibraryWriter</c>
	/// use for every on-disk write here), so a concurrent VCSP subscriber never
	/// observes a truncated file, computing the SHA-256 digest in the same pass
	/// (decision 8's always-self-hash rule -- this handler never trusts
	/// <see cref="DepotArtifact.Sha256"/> for the item's own recorded content hash,
	/// only for the cheaper unchanged-vs-changed pre-check above).
	/// </summary>
	private static async Task<ContentLibraryItemFileWrite> CopyIntoLibraryAsync(
		string sourcePath, string itemDirectory, string fileName, CancellationToken cancellationToken)
	{
		string destinationPath = Path.Combine(itemDirectory, fileName);
		string tempPath = Path.Combine(itemDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
		using System.Security.Cryptography.IncrementalHash hasher =
			System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
		long size = 0;
		try
		{
			await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
			await using (FileStream destination = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				byte[] buffer = new byte[81920];
				int read;
				while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
				{
					await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
					hasher.AppendData(buffer, 0, read);
					size += read;
				}

				await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
				destination.Flush(flushToDisk: true);
			}

			cancellationToken.ThrowIfCancellationRequested();
			File.Move(tempPath, destinationPath, overwrite: true);
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
}
