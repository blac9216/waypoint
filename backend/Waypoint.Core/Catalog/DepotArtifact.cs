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
/// the depot-relative path both write paths now populate (CatalogIndexJobHandler's
/// offline disk walk already wrote a relative path here; VendorProductVersionCatalogParser's
/// connected pull, issue #687, writes the vendor catalog's bare filename -- true
/// nested-path resolution for the connected side is presence-sweep behavior, #1503).
/// <see cref="SizeBytes"/> and <see cref="LastVerifiedAt"/> are migration 0100's other
/// new columns; <see cref="LastVerifiedAt"/> is left null by every upsert path in this
/// slice (deciding when a row counts as freshly verified is presence-sweep behavior,
/// #1503/#1512).
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
	DateTimeOffset? LastVerifiedAt = null);

/// <summary>
/// One artifact to upsert (e.g. parsed from a <c>catalog-index</c> job's tool output
/// -- issue #194 -- or from <see cref="VendorProductVersionCatalogParser"/>, issue
/// #687). <see cref="RelativePath"/> is the depot's own stable catalog identity
/// (migration 0100, issue #1488: renamed from a bare <c>ExternalId</c> string that
/// silently stood in for two incompatible namespaces -- an offline disk-relative path
/// and a connected-pull bare filename) and is the idempotency key: upserting the same
/// path twice yields one row with the newer payload (issue #193 acceptance
/// criterion). <see cref="SizeBytes"/> is the other half of the catalog identity pair
/// #1488 calls for (relative path + size/hash, <see cref="Sha256"/> already existed);
/// it defaults to null so every pre-existing call site that does not yet know a
/// binary's size keeps compiling unchanged -- populating it for every write path is
/// out of this slice's scope (#1503/#1512).
/// </summary>
public sealed record DepotArtifactUpsert(
	string RelativePath,
	string? Sha256,
	string Status,
	string MetadataJson,
	long? SizeBytes = null);

/// <summary>Filters accepted by <c>GET /api/v1/catalog/artifacts</c>. A null field means "no filter".</summary>
public sealed record DepotArtifactFilter(string? Product, string? Version, string? Status);

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
