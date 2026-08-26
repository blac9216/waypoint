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

namespace Waypoint.Core.Components;

/// <summary>
/// Pure capability matcher: joins one <see cref="Component"/>'s resolved product/
/// version facts and catalog component link against the catalog's execution profiles
/// for that component, per issue #732's AC "capability matching against catalog
/// selectors and product/build/version facts ... exact reasons for unsupported
/// product/build/component/transport combinations." Fails closed at every step -- a
/// component with no catalog link, no exact fact, or a fact/catalog-version mismatch
/// returns zero compatible profiles and an explicit reason, never an empty result with
/// no explanation (ADR-0023: "Waypoint records missing or conflicting facts and fails
/// closed; it never guesses a winner").
///
/// This is intentionally domain logic with no I/O: callers supply the already-loaded
/// <see cref="CatalogExecutionProfileDetail"/> set for the component's catalog link (or
/// the full catalog product-version tree) so the matcher stays trivially unit-testable
/// without a database, matching this codebase's existing pure-matcher convention (cf.
/// <c>CatalogVocabularyValidator</c>).
/// </summary>
public static class ComponentCapabilityMatcher
{
	/// <summary>
	/// Matches <paramref name="component"/> against <paramref name="candidateProfiles"/>
	/// (every execution profile already known to belong to the component's linked
	/// catalog component). A profile is compatible only when:
	/// <list type="number">
	/// <item>the component has an exact resolved fact (see <see cref="ResolveExactVersion"/> -- fails closed on conflict);</item>
	/// <item>the component is linked to a catalog component at all;</item>
	/// <item>the linked catalog component's product version's <see cref="CatalogProductVersion.VersionKey"/> equals the resolved exact fact byte-for-byte (no ranges, no nearest-version -- ADR-0022).</item>
	/// </list>
	/// Every profile that fails a check contributes its own reason rather than a single
	/// generic failure, so a caller can render "unsupported because X" instead of a bare
	/// boolean.
	/// </summary>
	public static ComponentCapabilityMatch Match(
		Component component,
		Guid? linkedProductVersionId,
		string? linkedProductVersionKey,
		IReadOnlyList<CatalogExecutionProfileDetail> candidateProfiles)
	{
		ArgumentNullException.ThrowIfNull(component);
		ArgumentNullException.ThrowIfNull(candidateProfiles);

		List<string> reasons = [];

		if (component.FactConflict)
		{
			reasons.Add(
				$"component '{component.Id}' has a configured/discovered product-version conflict; " +
				"an interactive Cyber+ initiator must resolve it per run, or a scheduled run skips this component (ADR-0023).");
			return new ComponentCapabilityMatch(component.Id, false, [], reasons);
		}

		string? exactVersion = ResolveExactVersion(component);
		if (exactVersion is null)
		{
			reasons.Add($"component '{component.Id}' has no configured or discovered exact product version; catalog compatibility cannot be evaluated.");
			return new ComponentCapabilityMatch(component.Id, false, [], reasons);
		}

		if (component.CatalogComponentId is null || linkedProductVersionId is null)
		{
			reasons.Add($"component '{component.Id}' is not linked to a known catalog component; no execution profile can be resolved.");
			return new ComponentCapabilityMatch(component.Id, false, [], reasons);
		}

		if (!string.Equals(linkedProductVersionKey, exactVersion, StringComparison.Ordinal))
		{
			reasons.Add(
				$"component '{component.Id}' resolved exact version '{exactVersion}' does not match the linked catalog product version " +
				$"'{linkedProductVersionKey}'; Waypoint never substitutes the nearest older/newer baseline (ADR-0022).");
			return new ComponentCapabilityMatch(component.Id, false, [], reasons);
		}

		List<CatalogExecutionProfileDetail> compatible = [.. candidateProfiles.Where(p => p.Component.Id == component.CatalogComponentId)];
		if (compatible.Count == 0)
		{
			reasons.Add(
				$"component '{component.Id}' has no catalog execution profile for its linked component/version " +
				$"('{linkedProductVersionKey}'); content may not yet be staged or activated.");
			return new ComponentCapabilityMatch(component.Id, false, [], reasons);
		}

		return new ComponentCapabilityMatch(component.Id, true, compatible, []);
	}

	/// <summary>
	/// Resolves the single exact version fact a component may be matched against.
	/// Returns null (fails closed) when both facts are absent; returns the sole present
	/// fact's version when exactly one is present; and -- since a true conflict is
	/// already flagged and short-circuited by <see cref="Match"/> before this is
	/// reached -- returns the agreeing value when both are present and equal.
	/// </summary>
	private static string? ResolveExactVersion(Component component)
	{
		if (component.ConfiguredFact is { } configured && component.DiscoveredFact is { } discovered)
		{
			return string.Equals(configured.ExactVersion, discovered.ExactVersion, StringComparison.Ordinal)
				? configured.ExactVersion
				: null;
		}

		return component.ConfiguredFact?.ExactVersion ?? component.DiscoveredFact?.ExactVersion;
	}
}
