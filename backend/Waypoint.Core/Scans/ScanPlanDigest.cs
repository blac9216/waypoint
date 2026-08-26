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

using System.Security.Cryptography;
using System.Text;

namespace Waypoint.Core.Scans;

/// <summary>
/// Computes the deterministic, content-addressed digest issue #734 AC-4 requires:
/// "Preview and create use the same planner and produce the same plan digest" for the
/// same resolved inputs. Pure function of <see cref="ScanPlanItem"/> field values only
/// -- no wall-clock timestamp, no random/generated id (row ids and <c>CreatedAt</c> are
/// deliberately excluded), and no ordering dependency (items are sorted by
/// <see cref="ScanPlanItem.ComponentId"/> before hashing, so a planner that happens to
/// enumerate components in a different order over the same underlying set still
/// produces the same digest). Skips are excluded from the digest entirely: a skip
/// changes what DIDN'T run, not what the accepted plan commits to executing, and
/// re-planning after a purely cosmetic skip-detail wording change should not appear as
/// a different plan.
/// </summary>
public static class ScanPlanDigest
{
	/// <summary>
	/// Computes the lowercase-hex SHA-256 digest of <paramref name="items"/> plus
	/// <paramref name="planSchemaVersion"/> and the requested/resolved scope's own
	/// identity (<paramref name="resolvedComponentIdsDigestSeed"/> -- callers pass the
	/// sorted resolved component id set so two requests naming the same components in
	/// a different order still digest identically, and so an otherwise-identical
	/// accepted-item set under a DIFFERENT requested scope still digests differently,
	/// matching ADR-0023 "one plan freezes requested and resolved scope").
	/// </summary>
	public static string Compute(int planSchemaVersion, IReadOnlyList<Guid> resolvedComponentIdsDigestSeed, IReadOnlyList<ScanPlanItem> items)
	{
		ArgumentNullException.ThrowIfNull(resolvedComponentIdsDigestSeed);
		ArgumentNullException.ThrowIfNull(items);

		StringBuilder builder = new();
		builder.Append("v=").Append(planSchemaVersion).Append(';');

		builder.Append("scope=");
		foreach (Guid componentId in resolvedComponentIdsDigestSeed.OrderBy(id => id))
		{
			builder.Append(componentId).Append(',');
		}

		builder.Append(";items=");
		foreach (ScanPlanItem item in items.OrderBy(i => i.ComponentId))
		{
			builder
				.Append('[')
				.Append(item.ComponentId).Append('|')
				.Append(item.CatalogExecutionProfileId).Append('|')
				.Append(item.BaselineId?.ToString() ?? "-").Append('|')
				.Append(item.BenchmarkRevisionId?.ToString() ?? "-").Append('|')
				.Append(item.Transport).Append('|')
				.Append(item.SelectorKind).Append('|')
				.Append(item.SelectorName ?? "-").Append('|')
				.Append(item.ReportGroupKey).Append('|')
				.Append(item.Priority).Append('|')
				.Append(item.OutputKind).Append('|');

			foreach (string purpose in item.RequiredPurposes.OrderBy(p => p, StringComparer.Ordinal))
			{
				builder.Append(purpose).Append(',');
			}

			builder.Append('|');
			foreach (string inputName in item.DeclaredInputNames.OrderBy(n => n, StringComparer.Ordinal))
			{
				builder.Append(inputName).Append(',');
			}

			// Issue #735/ADR-0024: fold the resolved config snapshot into the digest too --
			// a re-plan against the same catalog/baseline state but a DIFFERENT resolved
			// config-doc (an operator edited/added an Input or Attestation between two
			// otherwise-identical plan compiles) must not silently collide on the same
			// digest. Sorted by input name (never by insertion order) so the digest stays
			// independent of PlanConfigResolutionService's own declared-input iteration
			// order, matching every other list in this builder.
			builder.Append('|');
			foreach (Waypoint.Core.ConfigDocs.PlanInputResolution input in item.InputResolutionsOrEmpty.OrderBy(r => r.InputName, StringComparer.Ordinal))
			{
				builder
					.Append(input.InputName).Append(':')
					.Append(input.State).Append(':')
					.Append(input.DocId?.ToString() ?? "-").Append(':')
					.Append(input.DocVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append(',');
			}

			builder.Append('|');
			if (item.AttestationResolution is { } attestation)
			{
				builder
					.Append(attestation.State).Append(':')
					.Append(attestation.DocId?.ToString() ?? "-").Append(':')
					.Append(attestation.DocVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-").Append(':')
					.Append(attestation.Applied).Append(':')
					.Append(attestation.Expired);
			}
			else
			{
				builder.Append('-');
			}

			builder.Append(']');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
