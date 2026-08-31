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

using Waypoint.Core.Catalog;

namespace Waypoint.Core.Downloads;

/// <summary>
/// Verifies one <c>binaries-download</c> job's downloaded file against the
/// authenticated catalog metadata for its <see cref="DepotArtifact"/> row (issue #1486,
/// epic #1181, split from #795's design record; carries the parent's AC "Downloads are
/// verified against the authenticated catalog metadata before being reported present").
/// Distinct from <see cref="IManagedToolCatalogVerifier"/>, which authenticates the raw
/// Broadcom catalog document itself (issue #669) -- by the time a <c>binaries-download</c>
/// job runs, that authentication already happened once, at <c>catalog-index</c>/
/// <c>catalog-pull</c> time (issue #1488), and its result is what <see cref="DepotArtifact.Sha256"/>
/// and <see cref="DepotArtifact.SizeBytes"/> already carry. This verifier's job is
/// narrower: does the file the tool just wrote to disk actually match that already-
/// authenticated row.
///
/// Grill decision Q8 (epic #16, 2026-08-28): "accept vendor size-only where nothing
/// better exists, but ALWAYS self-hash SHA-256 at download so all content has standard
/// database shape" -- <see cref="BinaryDownloadVerificationResult.Sha256"/> is always
/// the freshly computed hash of the file on disk, not an echo of
/// <see cref="DepotArtifact.Sha256"/>, so a catalog row indexed with size only (no
/// vendor-published hash) still ends up with one after a successful verified download.
/// </summary>
public interface IBinaryDownloadVerifier
{
	/// <summary>
	/// Resolves <paramref name="artifact"/>'s <see cref="DepotArtifact.ExternalId"/>
	/// (the depot-relative path, migration 0100/issue #1488) under
	/// <paramref name="depotStorePath"/> -- the SAME root the tool was invoked with as
	/// <c>--depot-store</c> (<c>BinariesDownloadJobHandler</c>) and the disk-walk root
	/// <c>CatalogIndexJobHandler</c> re-indexes from, so no separate "depot store
	/// layout" translation exists to get wrong -- then checks the resolved file's size
	/// and SHA-256 against the catalog row. A null <see cref="DepotArtifact.SizeBytes"/>
	/// or <see cref="DepotArtifact.Sha256"/> is treated as "nothing better exists"
	/// (Q8): that one dimension is skipped rather than failing verification, but the
	/// SHA-256 half is always computed regardless, per <see cref="BinaryDownloadVerificationResult.Sha256"/>'s
	/// doc comment. A path that resolves outside <paramref name="depotStorePath"/>, or a
	/// missing file, fails verification -- never partial success.
	/// </summary>
	Task<BinaryDownloadVerificationResult> VerifyAsync(
		DepotArtifact artifact, string depotStorePath, CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IBinaryDownloadVerifier.VerifyAsync"/>.</summary>
public sealed record BinaryDownloadVerificationResult(bool Verified, string? Sha256, string? FailureReason, string? ResolvedPath)
{
	/// <summary><paramref name="sha256"/> is the freshly computed self-hash (Q8), lower-case hex.</summary>
	public static BinaryDownloadVerificationResult Ok(string sha256, string resolvedPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
		ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
		return new(true, sha256, null, resolvedPath);
	}

	/// <summary>
	/// <paramref name="resolvedPath"/> is the on-disk file the failure was evaluated
	/// against (issue #1486 review finding 1) -- non-null for a size/hash mismatch
	/// (the file exists, at a confined path, but its contents disagree with the
	/// catalog) so the caller can quarantine it; null for a missing file or a path
	/// that resolved outside the depot store root, where there is nothing safe to move.
	/// </summary>
	public static BinaryDownloadVerificationResult Fail(string reason, string? resolvedPath = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, null, reason, resolvedPath);
	}
}
