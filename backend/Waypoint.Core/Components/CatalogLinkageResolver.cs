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
/// The single (component_key, exact_version) -&gt; catalog_component_id lookup issue
/// #985 introduced for the discovered-fact path
/// (<see cref="Waypoint.Infrastructure.Execution.Discovery.DiscoverJobHandler.ResolveCatalogLinkageAsync"/>,
/// which this type was extracted FROM verbatim, not reimplemented) and issue #1000
/// reuses unchanged for the configured-fact path
/// (<see cref="Waypoint.Infrastructure.Components.ComponentRepository.SetConfiguredFactAsync"/>).
/// One place decides "does this (key, version) resolve to exactly one catalog
/// component" so both provenances share identical exact-match/ambiguity/fail-closed
/// semantics (ADR-0022 "never guesses a winner"; ADR-0023 "[Waypoint] never guesses a
/// winner" for facts generally) -- neither path forks its own copy of this rule.
/// </summary>
public static class CatalogLinkageResolver
{
	/// <summary>
	/// Resolves <paramref name="catalogComponentKey"/> + <paramref name="exactVersion"/>
	/// against <see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/>.
	/// A null/whitespace <paramref name="exactVersion"/> never looks up at all and
	/// resolves to <c>(null, null)</c> (no fact this pass/write -- nothing to link,
	/// nothing to report). Zero matches resolves to <c>(null, null)</c> (honest "no
	/// catalog coverage yet" -- not an error). More than one match resolves to
	/// <c>(null, warning)</c> with a human-readable ambiguity reason -- never an
	/// arbitrary first-match-wins. A repository fault is caught here exactly as #995
	/// hardened the discovery path: this one lookup fails closed (unlinked) with a
	/// warning rather than throwing out of the caller's larger unit of work.
	/// </summary>
	public static async Task<(Guid? CatalogComponentId, string? Warning)> ResolveAsync(
		ICatalogRepository catalog, string catalogComponentKey, string? exactVersion, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentException.ThrowIfNullOrWhiteSpace(catalogComponentKey);

		if (string.IsNullOrWhiteSpace(exactVersion))
		{
			return (null, null);
		}

		IReadOnlyList<CatalogComponent> candidates;
		try
		{
			candidates = await catalog
				.FindTopLevelComponentsByKeyAndVersionAsync(catalogComponentKey, exactVersion, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return (null,
				$"catalog linkage lookup for component key '{catalogComponentKey}' version '{exactVersion}' " +
				$"failed unexpectedly ({exception.GetType().Name}: {exception.Message}); left unlinked rather than failing the whole write.");
		}

		return candidates.Count switch
		{
			1 => (candidates[0].Id, null),
			0 => (null, null),
			_ => (null,
				$"component key '{catalogComponentKey}' version '{exactVersion}' matched {candidates.Count} catalog components " +
				$"across different products ({string.Join(", ", candidates.Select(c => c.Id))}); left unlinked rather than guessing."),
		};
	}
}
