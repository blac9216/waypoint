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

using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// Copies the <c>content-pull</c> working tree (<see cref="ComplianceContentOptions.ContentPath"/>)
/// into an immutable, digest-addressed snapshot directory under
/// <c>{ContentPath}/revisions/{contentDigest}</c> and records it via
/// <see cref="IBaselineRepository.RecordStagedRevisionAsync"/> -- issue #731's "stage
/// vendor and XCCDF revisions into immutable digest/revision directories" AC.
///
/// This runs AFTER a pull's git checkout/parse succeeds but BEFORE any activation
/// decision: staging never touches the active baseline, so a pull/import failure --
/// including a failure that happens after the working tree was mutated by git itself
/// -- cannot disturb whatever revision is currently active (issue #731 AC "pull/import
/// failure cannot disturb the active revision"). The working tree
/// (<see cref="ComplianceContentOptions.ContentPath"/> itself) remains the git
/// checkout scratch space content-pull's PowerShell module manages; THIS class is what
/// turns one successful checkout into a permanent, read-only, content-addressed copy
/// that outlives the next pull's checkout of a different ref.
///
/// Snapshotting is a plain recursive file copy (no hardlink/reflink optimization in
/// this slice -- content volumes are modest StigTemplate-sized trees, and correctness/
/// immutability matter more than storage efficiency for a first slice; a later
/// optimization pass can switch to hardlinks without changing this class's contract).
/// Idempotent: if the target digest directory already exists (an identical revision
/// was already staged), the copy is skipped and the existing directory is left
/// untouched, matching <see cref="IBaselineRepository.RecordStagedRevisionAsync"/>'s
/// own idempotent-by-digest contract.
/// </summary>
public sealed class ContentRevisionStager : IContentRevisionStager
{
	private readonly IBaselineRepository _baselines;

	public ContentRevisionStager(IBaselineRepository baselines)
	{
		ArgumentNullException.ThrowIfNull(baselines);
		_baselines = baselines;
	}

	/// <summary>
	/// Snapshots <paramref name="contentPath"/> into
	/// <paramref name="contentPath"/>/revisions/<paramref name="contentDigest"/> and
	/// records the resulting <see cref="ContentRevision"/>. The digest is supplied by
	/// the caller (issue #729's already-computed <c>SemanticImportReport.SourceDigest"</c>)
	/// rather than recomputed here -- one deterministic whole-import digest, not two
	/// independently-computed ones that could silently diverge.
	/// </summary>
	public async Task<ContentRevision> StageAsync(
		string contentPath, string sourceCommit, string contentDigest, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentDigest);

		string revisionsRoot = Path.Combine(contentPath, "revisions");
		Directory.CreateDirectory(revisionsRoot);

		string relativePath = Path.Combine("revisions", contentDigest);
		string targetDirectory = Path.Combine(contentPath, relativePath);

		if (!Directory.Exists(targetDirectory))
		{
			// Stage into a temp sibling directory first, then atomically rename into
			// place -- a process crash mid-copy must never leave a partially-populated
			// directory visible under the final digest name (which activation/rollback
			// would otherwise treat as a complete, trustworthy revision).
			string stagingDirectory = Path.Combine(revisionsRoot, $".staging-{Guid.NewGuid():N}");
			Directory.CreateDirectory(stagingDirectory);
			try
			{
				CopyDirectory(contentPath, stagingDirectory, revisionsRoot);
				Directory.Move(stagingDirectory, targetDirectory);
			}
			catch
			{
				if (Directory.Exists(stagingDirectory))
				{
					Directory.Delete(stagingDirectory, recursive: true);
				}

				throw;
			}
		}

		return await _baselines.RecordStagedRevisionAsync(sourceCommit, contentDigest, relativePath, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Recursively copies <paramref name="sourceRoot"/> into <paramref name="targetRoot"/>,
	/// skipping the <paramref name="excludeDirectory"/> subtree entirely (the
	/// <c>revisions/</c> directory itself lives inside <c>ContentPath</c>, so a naive
	/// copy would otherwise recurse into its own already-staged siblings).
	/// </summary>
	private static void CopyDirectory(string sourceRoot, string targetRoot, string excludeDirectory)
	{
		foreach (string directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
		{
			if (IsUnder(directory, excludeDirectory))
			{
				continue;
			}

			string relative = Path.GetRelativePath(sourceRoot, directory);
			Directory.CreateDirectory(Path.Combine(targetRoot, relative));
		}

		foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			if (IsUnder(file, excludeDirectory))
			{
				continue;
			}

			string relative = Path.GetRelativePath(sourceRoot, file);
			string destination = Path.Combine(targetRoot, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination, overwrite: false);
		}
	}

	private static bool IsUnder(string path, string ancestor)
	{
		string normalizedPath = Path.GetFullPath(path);
		string normalizedAncestor = Path.GetFullPath(ancestor);
		return normalizedPath.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| normalizedPath == normalizedAncestor;
	}
}
