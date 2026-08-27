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

using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// Issue #738 (epic #726 Wave 3, ADR-0022/ADR-0024): resolves a vCenter execution
/// item's frozen <see cref="Waypoint.Core.Scans.ScanPlanItem.CatalogExecutionProfileId"/>
/// and <see cref="Waypoint.Core.Scans.ScanPlanItem.BaselineId"/> down to the exact
/// on-disk InSpec profile directory the ACTIVATED content revision materialized (PR
/// #850's staged <c>{ContentPath}/revisions/{digest}</c> snapshot), rather than the
/// mutable content-pull working tree #639's <c>ResolveProfilePath</c> reads.
///
/// This is PR #850's stated remainder item 1 ("Wiring scans to resolve one immutable
/// activated revision directory ... requires the planner/plan-snapshot work") made
/// real for the vCenter component path specifically -- ScanComponentNarrowing's other
/// narrowed selectors (esxi/vm) and every other transport are untouched by this issue
/// and keep resolving through <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/>'s
/// existing <c>ResolveProfilePath</c> fallback (#739/#740's remit).
///
/// Resolution happens at EXECUTION time (not plan-compile time): the plan item only
/// freezes the IDENTITY of the baseline/profile it must execute -- ADR-0024's "the
/// immutable snapshot is part of the planned item's compliance definition" -- while
/// this resolver re-reads the CURRENT on-disk materialization of that already-frozen
/// identity on every attempt. Because a content revision's staged directory is
/// immutable once written (#850's atomic temp-dir+rename), re-resolving on retry can
/// never observe a different profile than the first attempt did for the SAME baseline
/// id; it only tolerates the revision becoming available later than plan-compile time
/// (e.g. a slow first pull) or catches a revision purged/never-staged by an operator
/// mistake -- exactly the class of failure this issue's AC 1 requires to "fail closed
/// with an actionable diagnostic if the revision isn't materialized" rather than
/// silently falling back to a wrong or fixed legacy profile.
/// </summary>
public sealed class VCenterProfileRevisionResolver
{
	private readonly IBaselineRepository _baselines;
	private readonly ICatalogRepository _catalog;
	private readonly IOptions<ComplianceContentOptions> _complianceContentOptions;

	public VCenterProfileRevisionResolver(
		IBaselineRepository baselines,
		ICatalogRepository catalog,
		IOptions<ComplianceContentOptions> complianceContentOptions)
	{
		ArgumentNullException.ThrowIfNull(baselines);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(complianceContentOptions);
		_baselines = baselines;
		_catalog = catalog;
		_complianceContentOptions = complianceContentOptions;
	}

	/// <summary>
	/// Resolves <paramref name="baselineId"/> (the plan item's frozen
	/// <see cref="Waypoint.Core.Scans.ScanPlanItem.BaselineId"/>) to an absolute,
	/// existing InSpec profile directory. Every failure path returns a
	/// <see cref="VCenterProfileRevisionResult"/> naming exactly which link in the
	/// chain (baseline row / content revision row / staged directory / promoted
	/// profile-key provenance) is missing -- never a bare null or generic exception --
	/// per this issue's AC "fail closed with an actionable diagnostic if the revision
	/// isn't materialized."
	/// </summary>
	public async Task<VCenterProfileRevisionResult> ResolveAsync(
		Guid catalogExecutionProfileId, Guid baselineId, CancellationToken cancellationToken)
	{
		Baseline? baseline = await _baselines.GetBaselineAsync(baselineId, cancellationToken).ConfigureAwait(false);
		if (baseline is null)
		{
			return VCenterProfileRevisionResult.Failure(
				$"baseline '{baselineId}' frozen onto this plan item no longer exists (retention/#850 baselines are RESTRICT-protected while referenced, so this should not happen for a real plan item -- treat as data corruption).");
		}

		ContentRevision? revision = await _baselines.GetRevisionAsync(baseline.ContentRevisionId, cancellationToken).ConfigureAwait(false);
		if (revision is null)
		{
			return VCenterProfileRevisionResult.Failure(
				$"content revision '{baseline.ContentRevisionId}' for baseline '{baselineId}' no longer exists.");
		}

		string? profileKey = await _catalog.GetProfileKeyForExecutionProfileAsync(catalogExecutionProfileId, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			return VCenterProfileRevisionResult.Failure(
				$"catalog execution profile '{catalogExecutionProfileId}' has no recorded import provenance (no accepted catalog_import_report_entries row); its on-disk profile directory name is unknown.");
		}

		string revisionRoot = Path.Combine(_complianceContentOptions.Value.ContentPath, revision.StagedRelativePath);
		string profilePath = Path.Combine(revisionRoot, profileKey);

		if (!Directory.Exists(profilePath))
		{
			return VCenterProfileRevisionResult.Failure(
				$"activated content revision '{revision.Id}' (digest '{revision.ContentDigest}') is not materialized at '{profilePath}' -- "
					+ "the staged revision directory or the profile's subpath within it is missing on this runner. "
					+ "Re-run content-pull/content-import to (re)stage the revision, or verify the compliance-content volume is mounted, before retrying this scan.");
		}

		return VCenterProfileRevisionResult.Success(profilePath, revision.Id, baseline.Id);
	}
}

/// <summary>
/// Outcome of <see cref="VCenterProfileRevisionResolver.ResolveAsync"/>: either the
/// resolved absolute profile directory plus the exact revision/baseline identity it
/// came from (attribution the caller folds into structured events/HDF metadata), or a
/// safe, non-secret diagnostic naming which link in the chain failed.
/// </summary>
public sealed record VCenterProfileRevisionResult(bool Succeeded, string? ProfilePath, Guid? ContentRevisionId, Guid? BaselineId, string? FailureReason)
{
	public static VCenterProfileRevisionResult Success(string profilePath, Guid contentRevisionId, Guid baselineId) =>
		new(true, profilePath, contentRevisionId, baselineId, null);

	public static VCenterProfileRevisionResult Failure(string reason) => new(false, null, null, null, reason);
}
