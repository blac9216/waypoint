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

namespace Waypoint.Api.Contracts;

/// <summary>
/// Wire shape for <c>GET /api/v1/dashboard</c> (issue #513, prototype screen 2):
/// docs/api-contract.md "Aggregate: KPI tiles, site posture, recent runs, attention
/// items." <see cref="Schedules"/> and appliance/version info are deliberately NOT
/// included here -- the frontend composes the SCHEDULES card from the existing
/// <c>GET /schedules</c> endpoint and the APPLIANCE card from the existing
/// <c>GET /system</c> endpoint, so this aggregate carries only the data no other
/// endpoint already answers: targets/compliance KPIs, per-site posture, recent runs,
/// and attention signals.
/// </summary>
public sealed record DashboardResponse(
	[property: JsonPropertyName("targets")]
	DashboardTargetsResponse Targets,

	[property: JsonPropertyName("findings")]
	DashboardFindingsResponse Findings,

	[property: JsonPropertyName("sites")]
	IReadOnlyList<DashboardSiteResponse> Sites,

	[property: JsonPropertyName("recent_runs")]
	IReadOnlyList<DashboardRunResponse> RecentRuns,

	[property: JsonPropertyName("attention")]
	IReadOnlyList<DashboardAttentionResponse> Attention);

/// <summary>TARGETS KPI tile: fleet total plus per-kind breakdown (prototype's "6 vCenter · 5 NSX · 92 ESXi · 49 SSH" chips -- SSH covers both ESXi and generic SSH targets since <c>TargetKinds</c> has no separate ESXi kind).</summary>
public sealed record DashboardTargetsResponse(
	[property: JsonPropertyName("total")]
	int Total,

	[property: JsonPropertyName("site_count")]
	int SiteCount,

	[property: JsonPropertyName("by_kind")]
	IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// OPEN FINDINGS KPI tile: open CAT I/II/III counts summed from each site's latest
/// completed scan run (see <see cref="DashboardSiteResponse"/>). Countable only when
/// at least one site contributed a countable run -- mirrors the run-artifacts
/// endpoint's "uncountable, never a fabricated zero" rule (issue #299) at fleet scale.
/// </summary>
public sealed record DashboardFindingsResponse(
	[property: JsonPropertyName("counts_available")]
	bool CountsAvailable,

	[property: JsonPropertyName("cat_i_open")]
	int CatIOpen,

	[property: JsonPropertyName("cat_ii_open")]
	int CatIIOpen,

	[property: JsonPropertyName("cat_iii_open")]
	int CatIIIOpen);

/// <summary>One SITE POSTURE row: target count, compliance percent (derived from the
/// latest completed scan run touching this site), open CAT counts, and when that run
/// last completed. <see cref="CompliancePercent"/> and the CAT counts are null when no
/// completed scan run exists yet for the site, or its HDF artifacts are uncountable --
/// the frontend renders that as an explicit "no data" state, never a fabricated 100%.</summary>
public sealed record DashboardSiteResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("target_count")]
	int TargetCount,

	[property: JsonPropertyName("compliance_percent")]
	double? CompliancePercent,

	[property: JsonPropertyName("cat_i_open")]
	int? CatIOpen,

	[property: JsonPropertyName("cat_ii_open")]
	int? CatIIOpen,

	[property: JsonPropertyName("cat_iii_open")]
	int? CatIIIOpen,

	[property: JsonPropertyName("last_scan_at")]
	string? LastScanAt);

/// <summary>One RECENT RUNS row -- the same fields <c>RunResponse</c> carries, narrowed to what the dashboard list renders.</summary>
public sealed record DashboardRunResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("run_type")]
	string RunType,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("scope")]
	string Scope,

	[property: JsonPropertyName("job_count")]
	int JobCount,

	[property: JsonPropertyName("created_at")]
	string CreatedAt);

/// <summary>
/// One ATTENTION signal (issue #513's feasibility audit): <see cref="Kind"/> is one of
/// <c>credential_auth_failing</c>, <c>inventory_stale</c>, or <c>depot_token_expiring</c>.
/// The last kind is a dormant signal type today -- no depot token metadata exposes an
/// expiry yet (see <c>DashboardAggregateService</c>'s doc comment) -- so it never
/// appears in <see cref="DashboardResponse.Attention"/> until that metadata exists.
/// Unattested CAT I is explicitly OUT of scope (tracked in #514: needs a
/// control-severity catalog that does not exist).
/// </summary>
public sealed record DashboardAttentionResponse(
	[property: JsonPropertyName("kind")]
	string Kind,

	[property: JsonPropertyName("text")]
	string Text,

	[property: JsonPropertyName("meta")]
	string Meta);
