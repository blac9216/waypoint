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

using System.Text.Json;

namespace Waypoint.Core.Catalog;

/// <summary>
/// Turns the existing <c>depot_artifacts</c> catalog into the Library tab's mode-aware
/// presence model (issue #36). Pure/stateless so it is unit-testable without Postgres --
/// <c>LibraryController</c> is the only caller, and it owns fetching the artifact list
/// and the current <c>appliance_state.mode</c>.
///
/// Scope line for "missing vs last bundle manifest" (docs/api-contract.md's exact
/// phrase): a <c>bundles</c> table with imported-manifest metadata does not exist on
/// `main` yet -- that's issue #44 ("transfer import: verify, diff, apply (disconnected
/// side)"), a different content type from PR #566's compliance-content pull. Until #44
/// lands, "missing in air-gapped mode" here means "not `present` in the local
/// <c>depot_artifacts</c> catalog" -- the same set a bundle import would have populated,
/// and the honest answer with today's schema. When #44's manifest tracking lands, swap
/// this evaluator's air-gapped branch to diff against the imported manifest's
/// referenced-artifact list instead.
/// </summary>
public static class LibraryPresenceEvaluator
{
	/// <summary>
	/// Projects the full artifact list into <see cref="LibraryItem"/>s. "Superseded" is
	/// evaluated per product: a `present` artifact is superseded if another `present`
	/// artifact of the same product has a lexicographically later version. Version
	/// strings are compared ordinally (no semver parser here) -- good enough to rank the
	/// invented fixtures/dotted-numeric depot versions this project actually sees;
	/// documented rather than silently approximate.
	/// </summary>
	public static IReadOnlyList<LibraryItem> Evaluate(IReadOnlyList<DepotArtifact> artifacts, bool connected)
	{
		ArgumentNullException.ThrowIfNull(artifacts);

		Dictionary<string, string?> latestPresentVersionByProduct = artifacts
			.Where(a => string.Equals(a.Status, "present", StringComparison.Ordinal) && a.Product is not null)
			.GroupBy(a => a.Product!)
			.ToDictionary(g => g.Key, g => g.Select(a => a.Version).Where(v => v is not null).OrderDescending(StringComparer.Ordinal).FirstOrDefault());

		List<LibraryItem> items = new(artifacts.Count);
		foreach (DepotArtifact artifact in artifacts)
		{
			bool present = string.Equals(artifact.Status, "present", StringComparison.Ordinal);
			string presence;
			if (present)
			{
				string? latest = artifact.Product is not null && latestPresentVersionByProduct.TryGetValue(artifact.Product, out string? v) ? v : null;
				bool isLatest = latest is null || artifact.Version is null || string.Equals(latest, artifact.Version, StringComparison.Ordinal);
				presence = isLatest ? LibraryPresenceStates.Present : LibraryPresenceStates.Superseded;
			}
			else
			{
				presence = connected ? LibraryPresenceStates.InDepot : LibraryPresenceStates.Missing;
			}

			items.Add(new LibraryItem(
				artifact.Id,
				artifact.ExternalId,
				artifact.Product,
				artifact.Version,
				artifact.Status,
				presence,
				TryReadSizeBytes(artifact.MetadataJson),
				BuildProvenance(artifact, presence, connected),
				artifact.IndexedAt,
				artifact.UpdatedAt));
		}

		return items;
	}

	/// <summary>Rolls up presence by product into the rail's per-family counts (prototype screen 7's "PRODUCT FAMILIES" list). A product with no entries never appears -- there is no fixed family taxonomy to pad against.</summary>
	public static IReadOnlyList<LibraryFamily> GroupByFamily(IReadOnlyList<LibraryItem> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		return items
			.Where(i => i.Product is not null)
			.GroupBy(i => i.Product!)
			.Select(g => new LibraryFamily(
				g.Key,
				g.Count(i => i.Presence is LibraryPresenceStates.Present or LibraryPresenceStates.Superseded),
				g.Count(i => i.Presence is LibraryPresenceStates.InDepot or LibraryPresenceStates.Missing)))
			.OrderBy(f => f.Name, StringComparer.Ordinal)
			.ToArray();
	}

	private static string BuildProvenance(DepotArtifact artifact, string presence, bool connected)
	{
		if (presence == LibraryPresenceStates.InDepot)
		{
			return "indexed at depot, not yet downloaded";
		}
		if (presence == LibraryPresenceStates.Missing)
		{
			return "not present in the local catalog";
		}
		return connected ? $"depot · indexed {artifact.IndexedAt:yyyy-MM-dd}" : $"imported · indexed {artifact.IndexedAt:yyyy-MM-dd}";
	}

	private static long? TryReadSizeBytes(string metadataJson)
	{
		if (string.IsNullOrWhiteSpace(metadataJson))
		{
			return null;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(metadataJson);
			if (document.RootElement.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long size))
			{
				return size;
			}
		}
		catch (JsonException)
		{
			// Malformed vendor metadata (ADR-0002: not ours to normalise) -- size is
			// optional on the wire, so swallow and report unknown rather than fail the
			// whole listing over one row's bad JSON.
		}

		return null;
	}
}
