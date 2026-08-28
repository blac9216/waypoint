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

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #730 remainder (epic #726 Wave 1, after PR #828's migration 0052 model):
/// read APIs for immutable, digest-addressed XCCDF benchmark revisions/rules and the
/// component-to-benchmark-revision mapping/coverage surface, plus the Admin-only
/// explicit mapping/override write endpoint. Every write here goes through
/// <see cref="IBenchmarkRepository.SetMappingAsync"/>'s existing supersede-in-place
/// model (PR #828) unchanged -- this controller adds no new mapping semantics, only a
/// validated HTTP adapter over it.
///
/// docs/api-contract.md's "Profiles &amp; benchmarks" section previously marked
/// <c>/benchmarks</c>/<c>/profiles/{id}/mapping</c> superseded by the ADR-0022
/// candidate/diff/approval pipeline; that supersession is about automatic vendor-sync
/// ingestion (still #729/#731 territory), not about this read/override surface over
/// the benchmark identity model this PR delivers. The delivered-section edit below
/// narrows the claim to exactly the endpoints this PR ships.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class BenchmarksController : ControllerBase
{
	private readonly IBenchmarkRepository _benchmarks;

	public BenchmarksController(IBenchmarkRepository benchmarks)
	{
		ArgumentNullException.ThrowIfNull(benchmarks);
		_benchmarks = benchmarks;
	}

	/// <summary>Every known benchmark_key (issue #730 AC groundwork for revision listing). Viewer+.</summary>
	[HttpGet("benchmarks")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<string>>> ListBenchmarkKeys(CancellationToken cancellationToken)
	{
		IReadOnlyList<string> keys = await _benchmarks.ListBenchmarkKeysAsync(cancellationToken).ConfigureAwait(false);
		return Ok(keys);
	}

	/// <summary>
	/// Every revision sharing one benchmark_key, newest-imported first -- issue #730 AC
	/// "multiple revisions of the same benchmark can coexist and are digest-addressed"
	/// made directly observable. Viewer+. Returns an empty array for an unknown key
	/// (a benchmark_key is not itself a stored entity, only a grouping value on
	/// revisions, so there is nothing to 404 against).
	/// </summary>
	[HttpGet("benchmarks/by-key/{benchmarkKey}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkRevisionResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<BenchmarkRevisionResponse>>> ListRevisionsByKey(string benchmarkKey, CancellationToken cancellationToken)
	{
		IReadOnlyList<BenchmarkRevision> revisions = await _benchmarks.ListRevisionsByBenchmarkKeyAsync(benchmarkKey, cancellationToken).ConfigureAwait(false);
		return Ok(revisions.Select(BenchmarkRevisionResponse.FromDomain).ToArray());
	}

	/// <summary>Single revision detail, including digest and lifecycle state. Viewer+. 404 when unknown.</summary>
	[HttpGet("benchmarks/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkRevisionResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BenchmarkRevisionResponse>> GetRevision(Guid id, CancellationToken cancellationToken)
	{
		BenchmarkRevision revision = await GetRevisionOrThrowAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(BenchmarkRevisionResponse.FromDomain(revision));
	}

	/// <summary>Every rule in one revision, ordered by rule_id. Viewer+. 404 when the revision itself is unknown.</summary>
	[HttpGet("benchmarks/{id:guid}/rules")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkRuleResponse[]), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<IReadOnlyList<BenchmarkRuleResponse>>> ListRules(Guid id, CancellationToken cancellationToken)
	{
		await GetRevisionOrThrowAsync(id, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<BenchmarkRule> rules = await _benchmarks.ListRulesAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(rules.Select(BenchmarkRuleResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Coverage/ambiguity report: every component's current mapping decision plus
	/// closed-vocabulary status counts (issue #730 AC "rule-level mapping coverage and
	/// unmatched/ambiguous rules are queryable" at the mapping-set level). Viewer+.
	/// </summary>
	[HttpGet("benchmark-mappings")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkMappingCoverageResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<BenchmarkMappingCoverageResponse>> ListMappingCoverage(CancellationToken cancellationToken)
	{
		IReadOnlyList<BenchmarkComponentMapping> current = await _benchmarks.ListCurrentMappingsAsync(cancellationToken).ConfigureAwait(false);

		// Issue #1002: resolve each distinct component's derived state once (a coverage
		// report can list many components; avoid a redundant kind lookup per row when
		// several rows -- there is at most one CURRENT row per component today, but this
		// stays a dictionary keyed by component id for clarity and future-proofing).
		Dictionary<Guid, string?> derivedStates = [];
		foreach (BenchmarkComponentMapping mapping in current)
		{
			if (!derivedStates.ContainsKey(mapping.CatalogComponentId))
			{
				derivedStates[mapping.CatalogComponentId] = await ResolveDerivedStateAsync(mapping, cancellationToken).ConfigureAwait(false);
			}
		}

		return Ok(BenchmarkMappingCoverageResponse.FromDomain(current, m => derivedStates[m.CatalogComponentId]));
	}

	/// <summary>
	/// The current mapping for one catalog component, or 404 when no mapping decision
	/// has ever been recorded for it (distinct from a recorded <c>unmapped</c> row,
	/// which is 200). Viewer+.
	/// </summary>
	[HttpGet("benchmark-mappings/{catalogComponentId:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkMappingResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BenchmarkMappingResponse>> GetCurrentMapping(Guid catalogComponentId, CancellationToken cancellationToken)
	{
		BenchmarkComponentMapping? mapping = await _benchmarks.GetCurrentMappingAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
		if (mapping is null)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No mapping decision has ever been recorded for catalog component '{catalogComponentId}'.");
		}

		string? derivedState = await ResolveDerivedStateAsync(mapping, cancellationToken).ConfigureAwait(false);
		return Ok(BenchmarkMappingResponse.FromDomain(mapping, derivedState));
	}

	/// <summary>
	/// Full mapping history for one component, newest first -- the versioned audit
	/// trail (issue #730 AC "explicit Admin mapping/override with versioned audit
	/// history") made directly readable. Viewer+ (reading history is not itself a
	/// state-changing action, matching this codebase's read/write RBAC split
	/// elsewhere -- e.g. <c>CatalogController.PullStatus</c>/<c>Pull</c>). Returns an
	/// empty array, never 404, for a component that has never received a mapping
	/// decision -- "no history" and "unknown component" are not conflated.
	/// </summary>
	[HttpGet("benchmark-mappings/{catalogComponentId:guid}/history")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(BenchmarkMappingResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<BenchmarkMappingResponse>>> GetMappingHistory(Guid catalogComponentId, CancellationToken cancellationToken)
	{
		IReadOnlyList<BenchmarkComponentMapping> history = await _benchmarks.GetMappingHistoryAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
		if (history.Count == 0)
		{
			return Ok(Array.Empty<BenchmarkMappingResponse>());
		}

		// Every history row is the SAME catalog component, so its derived catalog
		// content kind is a single lookup shared across every row -- only the current
		// row's mapping-derived state (benchmark_missing) can legitimately differ row
		// to row, and BenchmarkMappingDerivedStates.NotApplicableSrg depends only on the
		// component's kind, never on which row is current.
		string? kind = await _benchmarks.GetComponentContentKindAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
		return Ok(history.Select(m => BenchmarkMappingResponse.FromDomain(m, DeriveState(m, kind))).ToArray());
	}

	/// <summary>
	/// Explicit Admin mapping/override (issue #730 AC "mapping changes are Admin-only,
	/// versioned, and audited"). Always records a NEW current row via
	/// <see cref="IBenchmarkRepository.SetMappingAsync"/> with <c>is_admin_override:
	/// true</c> and this caller's identity as <c>actor</c> -- the prior current row (if
	/// any) is superseded, never overwritten (PR #828's existing model, unchanged
	/// here). 404 when <paramref name="catalogComponentId"/> does not name a real
	/// catalog component; 400 when the body fails the closed mapping vocabulary; 404
	/// when <see cref="BenchmarkMappingOverrideRequest.BenchmarkRevisionId"/> names an
	/// unknown revision.
	///
	/// Issue #1002: this endpoint no longer accepts an "SRG has no published
	/// benchmark" declaration -- migration 0071 dropped the column
	/// <c>is_srg_no_benchmark</c> backed. SRG participation is now DERIVED from the
	/// component's bound catalog content kind (see <c>GET</c> responses'
	/// <c>derived_state</c>), never admin-stated. Following this repo's
	/// fail-closed-with-actionable-message convention (matching every other rejected
	/// shape on this same endpoint) rather than silently ignoring a caller still
	/// sending the old field: a request that sends <c>is_srg_no_benchmark: true</c> is
	/// rejected with 400 naming the replacement; sending it absent, null, or false is
	/// accepted (a legacy client that always echoed a previously-read `false` value
	/// back should not break).
	/// </summary>
	[HttpPut("benchmark-mappings/{catalogComponentId:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(BenchmarkMappingResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<BenchmarkMappingResponse>> SetMapping(
		Guid catalogComponentId, [FromBody] BenchmarkMappingOverrideRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.IsSrgNoBenchmarkRemoved)
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "validation_failed",
				"'is_srg_no_benchmark' was removed (issue #1002): SRG participation in benchmark mapping is now derived automatically "
					+ "from the component's bound catalog content kind and can no longer be admin-stated. Omit this field.");
		}

		if (!await _benchmarks.ComponentExistsAsync(catalogComponentId, cancellationToken).ConfigureAwait(false))
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No catalog component exists with id '{catalogComponentId}'.");
		}

		if (!BenchmarkMappingStatuses.IsValid(request.Status))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "validation_failed",
				$"'status' must be one of: {string.Join(", ", BenchmarkMappingStatuses.All)}.");
		}

		Guid? benchmarkRevisionId = null;
		if (!string.IsNullOrWhiteSpace(request.BenchmarkRevisionId))
		{
			if (!Guid.TryParse(request.BenchmarkRevisionId, out Guid parsedRevisionId))
			{
				throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", "'benchmark_revision_id' must be a GUID.");
			}

			if (await _benchmarks.GetRevisionAsync(parsedRevisionId, cancellationToken).ConfigureAwait(false) is null)
			{
				throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No benchmark revision exists with id '{parsedRevisionId}'.");
			}

			benchmarkRevisionId = parsedRevisionId;
		}

		string actor = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		try
		{
			BenchmarkComponentMapping mapping = await _benchmarks.SetMappingAsync(
				catalogComponentId,
				benchmarkRevisionId,
				request.Status,
				isAdminOverride: true,
				ambiguousCandidateCount: 0,
				request.Reason,
				actor,
				cancellationToken).ConfigureAwait(false);
			string? derivedState = await ResolveDerivedStateAsync(mapping, cancellationToken).ConfigureAwait(false);
			return Ok(BenchmarkMappingResponse.FromDomain(mapping, derivedState));
		}
		catch (ArgumentException ex)
		{
			// The repository itself fail-closed validates the same closed vocabulary
			// this action pre-checks above (defense in depth against a future caller
			// bypassing this HTTP layer) -- surface any remaining case (e.g. status
			// 'mapped' requiring a non-null revision) as 400, not 500.
			throw new ApiException(HttpStatusCode.BadRequest, "validation_failed", ex.Message);
		}
	}

	private async Task<BenchmarkRevision> GetRevisionOrThrowAsync(Guid id, CancellationToken cancellationToken)
	{
		BenchmarkRevision? revision = await _benchmarks.GetRevisionAsync(id, cancellationToken).ConfigureAwait(false);
		if (revision is null)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No benchmark revision exists with id '{id}'.");
		}

		return revision;
	}

	/// <summary>
	/// Issue #1002: resolves <paramref name="mapping"/>'s derived state by looking up
	/// its component's bound catalog content kind. Single-mapping convenience wrapper
	/// over <see cref="DeriveState"/> for the single-component read endpoints; the
	/// coverage/history endpoints batch or reuse the kind lookup instead of calling
	/// this once per row.
	/// </summary>
	private async Task<string?> ResolveDerivedStateAsync(BenchmarkComponentMapping mapping, CancellationToken cancellationToken)
	{
		string? kind = await _benchmarks.GetComponentContentKindAsync(mapping.CatalogComponentId, cancellationToken).ConfigureAwait(false);
		return DeriveState(mapping, kind);
	}

	/// <summary>
	/// Issue #1002 item 1/2: the pure derivation rule shared by every read path.
	/// <paramref name="kind"/> is <see cref="Waypoint.Core.ComplianceContent.CatalogKinds.Srg"/>
	/// when the component has no benchmark concept at all (never
	/// <see cref="BenchmarkMappingDerivedStates.BenchmarkMissing"/> regardless of
	/// mapping status -- SRG is not "missing" a benchmark, it never has one).
	/// <paramref name="kind"/> is <see cref="Waypoint.Core.ComplianceContent.CatalogKinds.Stig"/>
	/// (or <see langword="null"/> -- no execution profile staged/activated yet, treated
	/// the same as stig since a benchmark concept is still possible once one is) with
	/// no benchmark revision on the CURRENT mapping: a persistent, non-blocking,
	/// visible alert. A stig component with a mapped benchmark revision has nothing to
	/// surface -- <see langword="null"/>.
	/// </summary>
	private static string? DeriveState(BenchmarkComponentMapping mapping, string? kind)
	{
		if (kind == CatalogKinds.Srg)
		{
			return BenchmarkMappingDerivedStates.NotApplicableSrg;
		}

		return mapping.IsCurrent && mapping.BenchmarkRevisionId is null
			? BenchmarkMappingDerivedStates.BenchmarkMissing
			: null;
	}
}
