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

namespace Waypoint.Core.ConfigDocs;

/// <summary>
/// The closed set of reasons a plan item's config resolution reports something other
/// than a clean applied value (ADR-0024 "Control-granular settings and snapshots":
/// "an explicit missing/inapplicable state"). Every declared Input name and the
/// Attestation slot resolve to exactly one of these plus, for
/// <see cref="Resolved"/>, the doc identity that supplied the value.
/// </summary>
public static class ConfigResolutionStates
{
	/// <summary>A document was found at some layer and applied.</summary>
	public const string Resolved = "resolved";

	/// <summary>No document exists at any of the three layers for this (kind, catalog execution profile).</summary>
	public const string Missing = "missing";

	/// <summary>A document existed but every candidate layer's attestation had lapsed (<see cref="ConfigDocResolution.AttestationExpired"/>).</summary>
	public const string Expired = "expired";

	public static readonly IReadOnlyCollection<string> All = [Resolved, Missing, Expired];
}

/// <summary>
/// One declared Input name's resolved value for one plan item (ADR-0024: "Planning
/// snapshots every effective setting needed by each control, including the source
/// layer/version, value or secret reference/digest ... and an explicit missing/
/// inapplicable state"). This slice resolves at whole-document granularity (the
/// existing <see cref="ConfigDocResolver"/> shortcut ADR-0024 explicitly supersedes
/// only for a FUTURE per-control catalog that does not exist yet) -- every declared
/// input name for a given plan item shares the same resolved Input document, so
/// <see cref="DocId"/>/<see cref="Version"/>/<see cref="Layer"/> repeat across the
/// item's <see cref="ScanPlanItem.InputResolutions"/> entries when more than one
/// input name is declared. <see cref="State"/> is <see cref="ConfigResolutionStates.Missing"/>
/// (never <see cref="ConfigResolutionStates.Expired"/> -- expiry is an attestation-only
/// concept) when no Input document exists at any layer for this profile; a missing
/// REQUIRED input is what <see cref="Waypoint.Core.Scans.ScanPlanSkipReasons"/>'s future
/// missing-input reason (deferred to #737, this issue's own scope note) will block
/// planning on -- this record only reports the fact, it does not itself gate planning.
/// </summary>
public sealed record PlanInputResolution(
	string InputName,
	string State,
	string? Layer,
	Guid? DocId,
	int? DocVersion,
	string? DocAuthor,
	DateTimeOffset? DocVersionCreatedAt);

/// <summary>
/// The resolved Attestation document for one plan item (ADR-0024: "Attestations
/// resolve by selected profile/component, not one fixed application setting" -- issue
/// #735 AC). Keyed to the plan item's own <see cref="Waypoint.Core.Scans.ScanPlanItem.CatalogExecutionProfileId"/>
/// rather than <c>ScanOptions.AttestationProfile</c>'s single fixed name -- see
/// <see cref="PlanConfigResolution"/>'s resolver for how a doc is matched to that
/// identity. <see cref="Applied"/> is true only when <see cref="State"/> is
/// <see cref="ConfigResolutionStates.Resolved"/>; an <see cref="ConfigResolutionStates.Expired"/>
/// result still names the layer/doc whose waiver lapsed (mirrors
/// <see cref="ConfigDocResolution.AttestationExpired"/>/<see cref="ConfigDocResolution.AttestationExpiresAt"/>
/// one layer down) so history can show exactly which waiver was skipped and why.
/// </summary>
public sealed record PlanAttestationResolution(
	string State,
	string? Layer,
	Guid? DocId,
	int? DocVersion,
	string? DocAuthor,
	DateTimeOffset? DocVersionCreatedAt,
	bool Applied,
	bool Expired,
	DateTimeOffset? ExpiresAt);
