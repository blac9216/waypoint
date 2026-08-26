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
		return Ok(BenchmarkMappingCoverageResponse.FromDomain(current));
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

		return Ok(BenchmarkMappingResponse.FromDomain(mapping));
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
		return Ok(history.Select(BenchmarkMappingResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Explicit Admin mapping/override (issue #730 AC "mapping changes are Admin-only,
	/// versioned, and audited"). Always records a NEW current row via
	/// <see cref="IBenchmarkRepository.SetMappingAsync"/> with <c>is_admin_override:
	/// true</c> and this caller's identity as <c>actor</c> -- the prior current row (if
	/// any) is superseded, never overwritten (PR #828's existing model, unchanged
	/// here). 404 when <paramref name="catalogComponentId"/> does not name a real
	/// catalog component; 400 when the body fails the closed mapping vocabulary or the
	/// SRG/revision mutual-exclusion the repository itself enforces (issue #730 AC
	/// "SRG 'no published benchmark' is explicit, never inferred" -- this endpoint
	/// requires the caller to state <see cref="BenchmarkMappingOverrideRequest.IsSrgNoBenchmark"/>
	/// explicitly, it is never derived from the request shape); 404 when
	/// <see cref="BenchmarkMappingOverrideRequest.BenchmarkRevisionId"/> names an
	/// unknown revision.
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

		if (request.IsSrgNoBenchmark && benchmarkRevisionId is not null)
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "validation_failed",
				"A mapping cannot both declare 'SRG has no published benchmark' and reference a benchmark revision.");
		}

		string actor = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		try
		{
			BenchmarkComponentMapping mapping = await _benchmarks.SetMappingAsync(
				catalogComponentId,
				benchmarkRevisionId,
				request.Status,
				request.IsSrgNoBenchmark,
				isAdminOverride: true,
				ambiguousCandidateCount: 0,
				request.Reason,
				actor,
				cancellationToken).ConfigureAwait(false);
			return Ok(BenchmarkMappingResponse.FromDomain(mapping));
		}
		catch (ArgumentException ex)
		{
			// The repository itself fail-closed validates the same closed vocabulary and
			// SRG/revision exclusivity this action pre-checks above (defense in depth
			// against a future caller bypassing this HTTP layer) -- surface any remaining
			// case (e.g. status 'mapped' requiring a non-null revision) as 400, not 500.
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
}
