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
using Waypoint.Core.ComplianceContent.Xccdf;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for one row of <c>GET /api/v1/benchmarks</c>,
/// <c>GET /api/v1/benchmarks/{id}</c>, and the entries of
/// <c>GET /api/v1/benchmarks/by-key/{benchmarkKey}</c> (issue #730 remainder after
/// PR #828's migration 0052 model). One row is one immutable, digest-addressed XCCDF
/// benchmark revision -- never a mutable "current benchmark" record; multiple
/// revisions of the same <see cref="BenchmarkKey"/> coexist side by side (issue #730
/// AC), distinguished by <see cref="ContentDigest"/> and <see cref="LifecycleState"/>.
/// </summary>
public sealed record BenchmarkRevisionResponse(
	string Id,
	string BenchmarkKey,
	string Title,
	string Version,
	string Release,
	string Source,
	string ContentDigest,
	int RuleCount,
	string LifecycleState,
	DateTimeOffset ImportedAt)
{
	public static BenchmarkRevisionResponse FromDomain(BenchmarkRevision revision)
	{
		ArgumentNullException.ThrowIfNull(revision);
		return new BenchmarkRevisionResponse(
			revision.Id.ToString(),
			revision.BenchmarkKey,
			revision.Title,
			revision.Version,
			revision.Release,
			revision.Source,
			revision.ContentDigest,
			revision.RuleCount,
			revision.LifecycleState,
			revision.ImportedAt);
	}
}

/// <summary>
/// One rule/vulnerability within one benchmark revision --
/// <c>GET /api/v1/benchmarks/{id}/rules</c>.
/// </summary>
public sealed record BenchmarkRuleResponse(
	string Id,
	string BenchmarkRevisionId,
	string RuleId,
	string? VulnId,
	string Severity,
	string Title,
	DateTimeOffset CreatedAt)
{
	public static BenchmarkRuleResponse FromDomain(BenchmarkRule rule)
	{
		ArgumentNullException.ThrowIfNull(rule);
		return new BenchmarkRuleResponse(
			rule.Id.ToString(),
			rule.BenchmarkRevisionId.ToString(),
			rule.RuleId,
			rule.VulnId,
			rule.Severity,
			rule.Title,
			rule.CreatedAt);
	}
}

/// <summary>
/// One catalog-component-to-benchmark-revision mapping decision, current or
/// historical -- <c>GET /api/v1/benchmark-mappings/{catalogComponentId}</c> and
/// <c>GET /api/v1/benchmark-mappings/{catalogComponentId}/history</c> (issue #730 AC
/// "versioned audit history"). <see cref="IsSrgNoBenchmark"/> is the explicit "SRG
/// content has no published DISA benchmark" marker (issue #730 AC) -- it is never
/// inferred from <see cref="BenchmarkRevisionId"/> being null on its own, since an
/// `unmapped`/`ambiguous` row is also null there.
/// </summary>
public sealed record BenchmarkMappingResponse(
	string Id,
	string CatalogComponentId,
	string? BenchmarkRevisionId,
	string Status,
	bool IsSrgNoBenchmark,
	bool IsAdminOverride,
	bool IsCurrent,
	int AmbiguousCandidateCount,
	string? Reason,
	string? Actor,
	DateTimeOffset CreatedAt)
{
	public static BenchmarkMappingResponse FromDomain(BenchmarkComponentMapping mapping)
	{
		ArgumentNullException.ThrowIfNull(mapping);
		return new BenchmarkMappingResponse(
			mapping.Id.ToString(),
			mapping.CatalogComponentId.ToString(),
			mapping.BenchmarkRevisionId?.ToString(),
			mapping.Status,
			mapping.IsSrgNoBenchmark,
			mapping.IsAdminOverride,
			mapping.IsCurrent,
			mapping.AmbiguousCandidateCount,
			mapping.Reason,
			mapping.Actor,
			mapping.CreatedAt);
	}
}

/// <summary>
/// <c>GET /api/v1/benchmark-mappings</c> coverage/ambiguity report row (issue #730 AC
/// "rule-level mapping coverage and unmatched/ambiguous rules are queryable"). One row
/// per component that has ever received a mapping decision -- a component with no
/// mapping decision at all is absent from this list, distinct from one recorded as
/// <c>unmapped</c> (which IS a queryable row, per <see cref="BenchmarkMappingResponse"/>'s
/// remark above).
/// </summary>
public sealed record BenchmarkMappingCoverageResponse(
	IReadOnlyList<BenchmarkMappingResponse> Mappings,
	int MappedCount,
	int SuggestedCount,
	int AmbiguousCount,
	int UnmappedCount)
{
	public static BenchmarkMappingCoverageResponse FromDomain(IReadOnlyList<BenchmarkComponentMapping> current)
	{
		ArgumentNullException.ThrowIfNull(current);
		return new BenchmarkMappingCoverageResponse(
			current.Select(BenchmarkMappingResponse.FromDomain).ToArray(),
			current.Count(m => m.Status == BenchmarkMappingStatuses.Mapped),
			current.Count(m => m.Status == BenchmarkMappingStatuses.Suggested),
			current.Count(m => m.Status == BenchmarkMappingStatuses.Ambiguous),
			current.Count(m => m.Status == BenchmarkMappingStatuses.Unmapped));
	}
}

/// <summary>
/// Request body for <c>PUT /api/v1/benchmark-mappings/{catalogComponentId}</c> --
/// the Admin-only explicit mapping/override endpoint (issue #730 AC). Every field
/// mirrors <see cref="IBenchmarkRepository.SetMappingAsync"/>'s parameters exactly;
/// this endpoint is a thin, validated wire adapter over that repository call, not a
/// second copy of its rules -- the repository itself still fail-closed validates
/// status/vocabulary/mutual-exclusion so a caller cannot bypass those invariants by
/// going around this contract.
/// </summary>
public sealed record BenchmarkMappingOverrideRequest(
	string? BenchmarkRevisionId,
	string Status,
	bool IsSrgNoBenchmark,
	string? Reason);
