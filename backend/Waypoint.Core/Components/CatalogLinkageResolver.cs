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
/// component" so both provenances share identical match/ambiguity/fail-closed
/// semantics (ADR-0022 "never guesses a winner"; ADR-0023 "[Waypoint] never guesses a
/// winner" for facts generally) -- neither path forks its own copy of this rule.
///
/// Issue #998's CORRECTED owner decision: the version match this resolver delegates to
/// <see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/> is
/// <see cref="VersionScopeMatcher"/>'s closed two-form DECLARED-SCOPE test (an observed
/// or Admin-configured full version like "8.0.3" matches a minor-scoped catalog key
/// "8.0"; any version under a major matches a major-line-scoped "9.x"; unknown key
/// forms fail closed), never byte-for-byte equality -- and because BOTH the discovered
/// and configured fact paths flow through this one resolver, both scope-match
/// identically by construction.
/// </summary>
/// <summary>
/// The closed set of reasons <see cref="CatalogLinkageResolver.ResolveAsync"/> leaves a
/// component unlinked (issue #1082: every fail-closed branch reports a machine-readable
/// reason, not just the ambiguous one -- so callers can log, count, and alert on all of
/// them instead of only the ambiguity case).
/// </summary>
public static class CatalogLinkageReasons
{
	/// <summary>No exact version fact was available to look up at all this pass/write (a null/whitespace <c>exactVersion</c>) -- nothing to link yet, not an error.</summary>
	public const string NoExactVersionFact = "no_exact_version_fact";

	/// <summary>An exact version fact WAS available, but it falls outside every catalog component's declared version scope for this key (<see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/> returned zero rows) -- honest "no catalog coverage yet," not an error.</summary>
	public const string OutOfDeclaredScope = "out_of_declared_scope";

	/// <summary>More than one catalog component across different products matched the same (key, version) -- fails closed rather than guessing a winner (ADR-0022).</summary>
	public const string Ambiguous = "ambiguous";

	/// <summary>The catalog lookup itself faulted unexpectedly; left unlinked rather than failing the caller's whole write (issue #995).</summary>
	public const string LookupFailed = "lookup_failed";
}

public static class CatalogLinkageResolver
{
	/// <summary>
	/// Resolves <paramref name="catalogComponentKey"/> + <paramref name="exactVersion"/>
	/// against <see cref="ICatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync"/>.
	/// A null/whitespace <paramref name="exactVersion"/> never looks up at all and
	/// resolves to <c>(null, NoExactVersionFact, detail)</c> (no fact this pass/write --
	/// nothing to link, but still an honest reason to report). Zero matches resolves to
	/// <c>(null, OutOfDeclaredScope, detail)</c> (honest "no catalog coverage yet" -- not
	/// an error, but still reported so a 100%-unlinked run is never silent, issue
	/// #1082). More than one match resolves to <c>(null, Ambiguous, detail)</c> with a
	/// human-readable ambiguity reason -- never an arbitrary first-match-wins. A
	/// repository fault is caught here exactly as #995 hardened the discovery path:
	/// this one lookup fails closed (unlinked, <c>LookupFailed</c>) rather than
	/// throwing out of the caller's larger unit of work. <paramref name="Reason"/> is
	/// null ONLY on a successful single match.
	/// </summary>
	public static async Task<(Guid? CatalogComponentId, string? Reason, string? Detail)> ResolveAsync(
		ICatalogRepository catalog, string catalogComponentKey, string? exactVersion, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentException.ThrowIfNullOrWhiteSpace(catalogComponentKey);

		if (string.IsNullOrWhiteSpace(exactVersion))
		{
			return (null, CatalogLinkageReasons.NoExactVersionFact,
				$"component key '{catalogComponentKey}' has no exact version fact (configured or discovered) this pass; nothing to link yet.");
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
			return (null, CatalogLinkageReasons.LookupFailed,
				$"catalog linkage lookup for component key '{catalogComponentKey}' version '{exactVersion}' " +
				$"failed unexpectedly ({exception.GetType().Name}: {exception.Message}); left unlinked rather than failing the whole write.");
		}

		return candidates.Count switch
		{
			1 => (candidates[0].Id, null, null),
			0 => (null, CatalogLinkageReasons.OutOfDeclaredScope,
				$"component key '{catalogComponentKey}' version '{exactVersion}' does not fall within any catalog component's declared " +
				"version scope; left unlinked rather than guessing (content may not yet be staged, or this product/version is genuinely unsupported)."),
			_ => (null, CatalogLinkageReasons.Ambiguous,
				$"component key '{catalogComponentKey}' version '{exactVersion}' matched {candidates.Count} catalog components " +
				$"across different products ({string.Join(", ", candidates.Select(c => c.Id))}); left unlinked rather than guessing."),
		};
	}
}
