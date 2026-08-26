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

using Waypoint.Core.Components;
using Waypoint.Core.Scans;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #736 (epic #726 Wave 2, ADR-0024): derives, per target, the credential
/// purposes its scan run must resolve from the immutable <see cref="ScanPlan"/>'s
/// PER-COMPONENT catalog-derived requirements (<see cref="ScanPlanItem.RequiredPurposes"/>,
/// already populated by #857's <c>ScanPlannerService</c> from
/// <c>catalog_credential_requirements</c> joined through the plan item) rather than the
/// coarse static <c>CredentialPurposeMatrix.RequiredScanPurposes</c>/
/// <c>ConditionalScanPurposes(target.Kind)</c> matrix. This is the mechanism that makes
/// AC "VCSA SSH is required exactly when a selected VCSA component consumes it" true:
/// <c>vcsa-ssh</c> only appears in a target's derived purpose set when the target's
/// resolved plan actually contains an accepted VCSA-component plan item that declares it
/// (via that component's catalog execution profile's <c>catalog_credential_requirements</c>
/// row), never merely because the target's KIND is <c>vsphere</c>.
///
/// This class only ever operates on a target-scoped run whose scope was compiled into a
/// plan (issue #733/#734's <c>target_scope</c> request shape) -- a legacy
/// <c>target_ids</c>/<c>profile_id</c>-only request has no plan and keeps using the
/// static matrix unchanged (<see cref="RunCreationService"/> only calls into this type
/// when a plan was actually compiled).
/// </summary>
public static class PlanCredentialRequirements
{
	/// <summary>
	/// Maps every accepted <paramref name="plan"/> item back to its owning target (via
	/// <see cref="IComponentRepository.GetAsync"/>'s <see cref="Component.ParentTargetId"/>)
	/// and unions each target's items' <see cref="ScanPlanItem.RequiredPurposes"/> into one
	/// applicable/required purpose set per target. A plan item whose component has since
	/// disappeared (should not happen -- <c>scan_plan_items.component_id</c> is
	/// <c>ON DELETE RESTRICT</c> -- defensive only) is skipped from the map rather than
	/// throwing; <see cref="ResolveAndDemoteAsync"/> still resolves every other item
	/// normally.
	/// </summary>
	public static async Task<IReadOnlyDictionary<Guid, PlanTargetRequirement>> GroupByTargetAsync(
		ScanPlan plan, IComponentRepository components, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(components);

		Dictionary<Guid, List<ScanPlanItem>> itemsByTarget = [];
		foreach (ScanPlanItem item in plan.Items)
		{
			Component? component = await components.GetAsync(item.ComponentId, cancellationToken).ConfigureAwait(false);
			if (component is null)
			{
				continue;
			}

			if (!itemsByTarget.TryGetValue(component.ParentTargetId, out List<ScanPlanItem>? existing))
			{
				existing = [];
				itemsByTarget[component.ParentTargetId] = existing;
			}

			existing.Add(item);
		}

		return itemsByTarget.ToDictionary(
			pair => pair.Key,
			pair => new PlanTargetRequirement(
				pair.Value,
				[.. pair.Value.SelectMany(i => i.RequiredPurposes).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)]));
	}
}

/// <summary>One target's plan items and the union of purposes they require (issue #736).</summary>
public sealed record PlanTargetRequirement(IReadOnlyList<ScanPlanItem> Items, IReadOnlyList<string> RequiredPurposes);
