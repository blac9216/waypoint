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

using System.Text.Json.Serialization;
using Waypoint.Core.Catalog;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for one row of <c>GET /api/v1/catalog/artifacts</c>
/// (docs/api-contract.md "Depot catalog": artifact, sha256, product, version, size,
/// status). <see cref="Metadata"/> carries the raw vendor JSON verbatim (ADR-0002) --
/// same "JSON string, not a nested object" convention <c>RunResponse.Scope</c>
/// already uses for a JSONB column on the wire.
/// </summary>
public sealed record CatalogArtifactResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("external_id")]
	string ExternalId,

	[property: JsonPropertyName("sha256")]
	string? Sha256,

	[property: JsonPropertyName("status")]
	string Status,

	[property: JsonPropertyName("product")]
	string? Product,

	[property: JsonPropertyName("version")]
	string? Version,

	[property: JsonPropertyName("metadata")]
	string Metadata,

	[property: JsonPropertyName("indexed_at")]
	DateTimeOffset IndexedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static CatalogArtifactResponse FromDomain(DepotArtifact artifact)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		return new CatalogArtifactResponse(
			artifact.Id.ToString(),
			artifact.ExternalId,
			artifact.Sha256,
			artifact.Status,
			artifact.Product,
			artifact.Version,
			artifact.MetadataJson,
			artifact.IndexedAt,
			artifact.UpdatedAt);
	}
}

/// <summary>Response body for <c>POST /api/v1/catalog/sync</c> (202 Accepted).</summary>
public sealed record CatalogSyncStartedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId);

/// <summary>Response body for <c>POST /api/v1/catalog/pull</c> (202 Accepted, issue #687).</summary>
public sealed record CatalogPullStartedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("job_id")]
	string JobId);

/// <summary>
/// Response body for <c>GET /api/v1/catalog/pull</c> (issue #687): the connected
/// vendor catalog-pull's readiness (gated on the #691 <c>depot_enrollment</c> state
/// being <c>validated</c>) plus the most recent attempt/success facts from
/// <c>catalog_pull_state</c> (migration 0049). <see cref="LastSuccessAt"/>/
/// <see cref="LastSuccessItemCount"/> report only a genuinely successful, authenticated
/// remote refresh -- never advanced by a local <c>catalog-index</c> re-index.
/// </summary>
public sealed record CatalogPullStatusResponse(
	[property: JsonPropertyName("ready")]
	bool Ready,

	[property: JsonPropertyName("not_ready_reason")]
	string? NotReadyReason,

	[property: JsonPropertyName("last_attempt_at")]
	DateTimeOffset? LastAttemptAt,

	[property: JsonPropertyName("last_outcome")]
	string? LastOutcome,

	[property: JsonPropertyName("last_failure_reason")]
	string? LastFailureReason,

	[property: JsonPropertyName("last_success_at")]
	DateTimeOffset? LastSuccessAt,

	[property: JsonPropertyName("last_success_item_count")]
	int? LastSuccessItemCount)
{
	public static CatalogPullStatusResponse FromDomain(CatalogPullState? state, bool ready, string? notReadyReason)
	{
		return new CatalogPullStatusResponse(
			ready,
			notReadyReason,
			state?.LastAttemptAt,
			state?.LastOutcome,
			state?.LastFailureReason,
			state?.LastSuccessAt,
			state?.LastSuccessItemCount);
	}
}
