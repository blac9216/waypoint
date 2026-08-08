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
	DateTimeOffset UpdatedAt);

/// <summary>
/// One artifact to upsert (e.g. parsed from a <c>catalog-index</c> job's tool output
/// -- issue #194, not built here). <see cref="ExternalId"/> is the depot's own stable
/// catalog identifier and is the idempotency key: upserting the same id twice yields
/// one row with the newer payload (issue #193 acceptance criterion).
/// </summary>
public sealed record DepotArtifactUpsert(
	string ExternalId,
	string? Sha256,
	string Status,
	string MetadataJson);

/// <summary>Filters accepted by <c>GET /api/v1/catalog/artifacts</c>. A null field means "no filter".</summary>
public sealed record DepotArtifactFilter(string? Product, string? Version, string? Status);
