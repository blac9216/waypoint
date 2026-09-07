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

namespace Waypoint.Core.Catalog;

/// <summary>
/// One indexed depot artifact (docs/api-contract.md "Depot catalog": artifact,
/// sha256, product, version, size, status). <see cref="Metadata"/> is the raw vendor
/// JSON exactly as the depot tool describes it (ADR-0002 -- vendor catalog shapes are
/// not ours to normalise); <see cref="Product"/>/<see cref="Version"/> are the two
/// fields promoted out of it for filtering (migration 0007's generated columns),
/// duplicated here for convenience rather than re-derived by callers.
///
/// <see cref="ExternalId"/> is migration 0100's (issue #1488) <c>relative_path</c>
/// column, kept under its original C# property name to avoid a mechanical rename
/// across every read-side consumer -- schema and behavior are rekeyed to the
/// catalog's own relative-path identity; the property that carries it is not. It is
/// the SAME depot-relative path every write path now populates (issue #1784):
/// <c>CatalogIndexJobHandler</c>'s offline presence sweep and
/// <see cref="VendorProductVersionCatalogParser"/>'s connected pull both resolve to
/// <see cref="DepotRelativePaths.Resolve"/>'s <c>PROD/COMP/&lt;product&gt;/&lt;fileName&gt;</c>
/// identity, converging on one row per real artifact -- prior to #1784 the connected
/// pull wrote the vendor catalog's bare filename instead, and a stack that both
/// pulled and swept reported every shared artifact twice under two different
/// identities with contradictory statuses.
/// <see cref="SizeBytes"/> and <see cref="LastVerifiedAt"/> are migration 0100's other
/// new columns; <see cref="LastVerifiedAt"/> is left null by every upsert path in this
/// slice (deciding when a row counts as freshly verified is presence-sweep behavior,
/// #1503/#1512). <see cref="BundleId"/> is migration 0130's (issue #1783) new column:
/// the vendor catalog's <c>artifacts.bundles[].id</c>, the identifier the real
/// vcf-download-tool's <c>binaries download --id</c> actually selects on (#1027
/// finding) -- distinct from <see cref="ExternalId"/> (the binary fileName). Null for
/// rows indexed before this migration or by the offline disk walk, which has no
/// vendor catalog document to read it from.
/// </summary>
public sealed record DepotArtifact(
	Guid Id,
	string ExternalId,
	string? Sha256,
	string Status,
	string? Product,
	string? Version,
	string MetadataJson,
	DateTimeOffset IndexedAt,
	DateTimeOffset UpdatedAt,
	long? SizeBytes = null,
	DateTimeOffset? LastVerifiedAt = null,
	string? BundleId = null);

/// <summary>
/// One artifact to upsert (e.g. parsed from a <c>catalog-index</c> job's tool output
/// -- issue #194 -- or from <see cref="VendorProductVersionCatalogParser"/>, issue
/// #687). <see cref="RelativePath"/> is the depot's own stable catalog identity
/// (migration 0100, issue #1488: renamed from a bare <c>ExternalId</c> string that
/// silently stood in for two incompatible namespaces -- an offline disk-relative path
/// and a connected-pull bare filename, unified onto the one depot-relative identity by
/// issue #1784) and is the idempotency key: upserting the same
/// path twice yields one row with the newer payload (issue #193 acceptance
/// criterion). <see cref="SizeBytes"/> is the other half of the catalog identity pair
/// #1488 calls for (relative path + size/hash, <see cref="Sha256"/> already existed);
/// it defaults to null so every pre-existing call site that does not yet know a
/// binary's size keeps compiling unchanged -- populating it for every write path is
/// out of this slice's scope (#1503/#1512). <see cref="BundleId"/> is migration
/// 0130's (issue #1783) new field, populated by
/// <see cref="VendorProductVersionCatalogParser"/> from the vendor catalog's
/// <c>artifacts.bundles[].id</c> -- the identifier the real tool's
/// <c>binaries download --id</c> selects on, never the same value as
/// <see cref="RelativePath"/>. Defaults to null so every pre-existing caller (the
/// offline disk walk, which has no bundle id to read) keeps compiling unchanged; the
/// repository upsert preserves a prior non-null value on a caller that does not know
/// it (COALESCE), the same convention <see cref="Sha256"/>/<see cref="SizeBytes"/>
/// already use.
/// </summary>
public sealed record DepotArtifactUpsert(
	string RelativePath,
	string? Sha256,
	string Status,
	string MetadataJson,
	long? SizeBytes = null,
	string? BundleId = null);

/// <summary>Filters accepted by <c>GET /api/v1/catalog/artifacts</c>. A null field means "no filter".</summary>
public sealed record DepotArtifactFilter(string? Product, string? Version, string? Status);

/// <summary>
/// Issue #1705: the closed <c>depot_artifacts.status</c> vocabulary
/// <c>depot_artifacts_status_check</c> (migration 0129) enforces, and the single
/// source of truth <c>DepotArtifactStatusesConstraintDriftTests</c> parses that
/// constraint's SQL against -- so a value added to either side alone fails a test,
/// this repo's #1517/RepoCredentialBindingConstraintDriftTests convention. This is
/// the minimal constant this issue needs; a fuller shared-constants pass across every
/// depot_artifacts.status call site is issue #1675's separate, deferred scope.
/// </summary>
public static class DepotArtifactStatuses
{
	/// <summary>Connected-pull catalog entry (<see cref="VendorProductVersionCatalogParser"/>, issue #687) not yet download-verified.</summary>
	public const string Indexed = "indexed";

	/// <summary>Reserved by the original slice-1 schema (migration 0001); no current writer emits it.</summary>
	public const string Downloading = "downloading";

	/// <summary>Verified on disk (download success, or a presence-sweep entry the manifest confirms -- #1503).</summary>
	public const string Present = "present";

	/// <summary>Download verification failed (<c>DownloadJobHandler</c>/<c>BinariesDownloadJobHandler</c>).</summary>
	public const string Failed = "failed";

	/// <summary>
	/// A cataloged entry the #1503 presence sweep did not find on disk
	/// (<c>WaypointCatalogIndex.psm1</c>'s <c>ValidateSet('present', 'missing')</c>).
	/// </summary>
	public const string Missing = "missing";

	/// <summary>The full <c>depot_artifacts_status_check</c> vocabulary, in the constraint's declared order.</summary>
	public static readonly IReadOnlyList<string> All = [Indexed, Downloading, Present, Failed, Missing];

	/// <summary>The subset the presence sweep itself can emit (<c>WaypointCatalogIndex.psm1</c>'s <c>ValidateSet</c>).</summary>
	public static readonly IReadOnlyList<string> PresenceSweepEmitted = [Present, Missing];
}

/// <summary>
/// Issue #1784: the single depot-relative identity-resolution rule both catalog
/// writers must agree on. Before this fix, <see cref="VendorProductVersionCatalogParser"/>
/// (connected pull) passed the vendor catalog's bare <c>fileName</c> as
/// <see cref="DepotArtifactUpsert.RelativePath"/>, while <c>WaypointCatalogIndex.psm1</c>'s
/// <c>Get-CatalogEntryDepotRelativePath</c> (offline presence sweep, #1503/#1512)
/// resolved the SAME binary to <c>PROD/COMP/&lt;Product&gt;/&lt;fileName&gt;</c> --
/// two different <c>depot_artifacts.relative_path</c> values for one real artifact, so
/// a stack that both pulled and swept reported every shared artifact twice with
/// contradictory statuses (live validation, issue #1784). <see cref="DepotRoot"/>/
/// <see cref="ComponentBinariesDir"/> mirror the psm1's <c>$Script:DepotRoot</c>/
/// <c>$Script:ComponentBinariesDir</c> module-scoped variables exactly --
/// <c>DepotRelativePathsConstraintDriftTests</c> parses the psm1 source and asserts
/// both stay equal, the same drift-guard convention <see cref="DepotArtifactStatuses"/>
/// already established for the status vocabulary.
/// </summary>
public static class DepotRelativePaths
{
	public const string DepotRoot = "PROD";
	public const string ComponentBinariesDir = "COMP";

	/// <summary>
	/// Resolves a catalog binary's depot-relative identity exactly as
	/// <c>Get-CatalogEntryDepotRelativePath</c> does: <c>PROD/COMP/&lt;product&gt;/&lt;fileName&gt;</c>.
	/// </summary>
	public static string Resolve(string product, string fileName) => $"{DepotRoot}/{ComponentBinariesDir}/{product}/{fileName}";
}

/// <summary>
/// One file found on a depot share that the authenticated vendor catalog does not
/// describe (migration 0100, issue #1488; #1038's Motivation: today these are
/// "silently absent from every surface"). Insert-or-touch-last-seen only -- there is
/// deliberately no delete/remove path anywhere in
/// <see cref="IUnknownCatalogFileRepository"/> (design decision Q11: alert instead of
/// drop). Populating this from a real presence sweep is #1503/#1512's job; this
/// slice only proves the storage shape and the no-delete contract.
/// </summary>
public sealed record UnknownCatalogFile(
	Guid Id,
	string RelativePath,
	long? SizeBytes,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastSeenAt);
