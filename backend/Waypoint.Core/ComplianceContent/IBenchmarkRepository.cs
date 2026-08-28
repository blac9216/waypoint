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

using Waypoint.Core.ComplianceContent.Xccdf;

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Storage for immutable, digest-addressed DISA XCCDF/STIG benchmark revisions and
/// rules, plus the component-to-benchmark-revision mapping and its versioned audit
/// history (migration 0052). Issue #730 scope only -- source acquisition/sync
/// scheduling (#729), semantic-equivalence reconciliation, and baseline activation
/// (#731) each layer their own repositories/services on top of this read/write
/// surface rather than extending it. The Admin-only HTTP write surface for mapping
/// overrides is deferred to the remainder PR issue #730's body names; this interface
/// exists so that surface (and this PR's own tests) have a supported entry point.
/// </summary>
public interface IBenchmarkRepository
{
	/// <summary>
	/// Persists <paramref name="candidate"/> as a new <see cref="BenchmarkRevision"/> plus
	/// its rules. Idempotent by (benchmark_key, content_digest) -- issue #730 AC "multiple
	/// revisions ... coexist and are digest-addressed": re-importing byte-identical
	/// content returns the EXISTING revision rather than creating a duplicate.
	/// </summary>
	Task<BenchmarkRevision> ImportRevisionAsync(BenchmarkImportCandidate candidate, string source, CancellationToken cancellationToken);

	/// <summary>Single revision by id, or null when unknown.</summary>
	Task<BenchmarkRevision?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken);

	/// <summary>Every revision sharing one benchmark_key, newest-imported first -- proves multiple revisions coexist (issue #730 AC).</summary>
	Task<IReadOnlyList<BenchmarkRevision>> ListRevisionsByBenchmarkKeyAsync(string benchmarkKey, CancellationToken cancellationToken);

	/// <summary>Every benchmark_key currently known, ordinal-ordered.</summary>
	Task<IReadOnlyList<string>> ListBenchmarkKeysAsync(CancellationToken cancellationToken);

	/// <summary>Every rule for one revision, ordered by rule_id.</summary>
	Task<IReadOnlyList<BenchmarkRule>> ListRulesAsync(Guid revisionId, CancellationToken cancellationToken);

	/// <summary>
	/// Records a new current mapping decision for <paramref name="catalogComponentId"/>,
	/// superseding any prior current mapping in the same transaction (issue #730 AC
	/// "versioned audit history" -- the prior row's is_current flips to false, it is
	/// never deleted or overwritten in place). Issue #1002: the caller can no longer
	/// state "SRG has no published benchmark" -- migration 0071 dropped the column that
	/// backed it; that fact is now derived at read time from the component's bound
	/// catalog content kind (see <see cref="GetComponentContentKindAsync"/>), never
	/// written here.
	/// </summary>
	Task<BenchmarkComponentMapping> SetMappingAsync(
		Guid catalogComponentId,
		Guid? benchmarkRevisionId,
		string status,
		bool isAdminOverride,
		int ambiguousCandidateCount,
		string? reason,
		string? actor,
		CancellationToken cancellationToken);

	/// <summary>The current mapping for one component, or null if no mapping decision has ever been recorded for it.</summary>
	Task<BenchmarkComponentMapping?> GetCurrentMappingAsync(Guid catalogComponentId, CancellationToken cancellationToken);

	/// <summary>Full mapping history for one component, newest first -- the versioned audit trail (issue #730 AC).</summary>
	Task<IReadOnlyList<BenchmarkComponentMapping>> GetMappingHistoryAsync(Guid catalogComponentId, CancellationToken cancellationToken);

	/// <summary>
	/// Every component's CURRENT mapping (one row per component that has ever received a
	/// mapping decision) -- the backing read for a coverage/ambiguity report (issue #730
	/// AC "coverage and ambiguity diagnostics").
	/// </summary>
	Task<IReadOnlyList<BenchmarkComponentMapping>> ListCurrentMappingsAsync(CancellationToken cancellationToken);

	/// <summary>
	/// True when <paramref name="catalogComponentId"/> names a real row in migration
	/// 0050's <c>catalog_components</c> table. Exists so the mapping-write HTTP surface
	/// (issue #730 remainder) can 404 an unknown component before recording a mapping
	/// decision against it, without this repository depending on
	/// <c>ICatalogRepository</c>'s write-oriented surface -- a read-only existence check
	/// against a table this repository's own FKs already reference.
	/// </summary>
	Task<bool> ComponentExistsAsync(Guid catalogComponentId, CancellationToken cancellationToken);

	/// <summary>
	/// Issue #1002: the closed <see cref="Waypoint.Core.ComplianceContent.CatalogKinds"/>
	/// value (<c>stig</c>|<c>srg</c>) this catalog component is bound to, derived by
	/// joining its <c>catalog_execution_profiles</c> row(s) to
	/// <c>catalog_content_releases.kind</c> -- never a stored column on this
	/// repository's own tables. Returns <see langword="null"/> when the component has
	/// no execution profile at all yet (content not staged/activated); a component
	/// with execution profiles of BOTH kinds (should not happen for any catalog row
	/// this repository's own seed data produces -- a component is bound to exactly one
	/// content kind in every parity-matrix row) returns <c>srg</c> deterministically
	/// (SRG is the more conservative "never has a benchmark concept" answer, matching
	/// ADR-0022's fail-closed posture for ambiguous catalog state) rather than
	/// guessing.
	/// </summary>
	Task<string?> GetComponentContentKindAsync(Guid catalogComponentId, CancellationToken cancellationToken);
}
