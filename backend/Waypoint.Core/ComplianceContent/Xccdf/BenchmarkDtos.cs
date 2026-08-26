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

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// One immutable, digest-addressed DISA XCCDF/STIG benchmark revision (migration
/// 0052's <c>benchmark_revisions</c>). Issue #730 AC "multiple revisions of the same
/// benchmark can coexist and are digest-addressed" -- <see cref="BenchmarkKey"/> groups
/// revisions of the same benchmark identity, <see cref="ContentDigest"/> is the exact
/// addressing key that makes re-importing byte-identical content idempotent.
/// </summary>
public sealed record BenchmarkRevision(
	Guid Id,
	string BenchmarkKey,
	string Title,
	string Version,
	string Release,
	string Source,
	string ContentDigest,
	int RuleCount,
	string LifecycleState,
	DateTimeOffset ImportedAt);

/// <summary>One rule/vulnerability within one immutable <see cref="BenchmarkRevision"/> (migration 0052's <c>benchmark_rules</c>).</summary>
public sealed record BenchmarkRule(
	Guid Id,
	Guid BenchmarkRevisionId,
	string RuleId,
	string? VulnId,
	string Severity,
	string Title,
	DateTimeOffset CreatedAt);

/// <summary>
/// The current or historical mapping of one catalog component to a benchmark revision
/// (migration 0052's <c>benchmark_component_mappings</c>). Issue #730 AC "explicit
/// Admin mapping/override with versioned audit history": every row is one point-in-time
/// mapping decision; <see cref="IsCurrent"/> marks the one row per component that is
/// presently in effect, and superseded rows remain queryable history.
/// </summary>
public sealed record BenchmarkComponentMapping(
	Guid Id,
	Guid CatalogComponentId,
	Guid? BenchmarkRevisionId,
	string Status,
	bool IsSrgNoBenchmark,
	bool IsAdminOverride,
	bool IsCurrent,
	int AmbiguousCandidateCount,
	string? Reason,
	string? Actor,
	DateTimeOffset CreatedAt);
