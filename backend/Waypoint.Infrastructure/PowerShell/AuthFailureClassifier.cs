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

using System.Text.RegularExpressions;

namespace Waypoint.Infrastructure.PowerShell;

/// <summary>
/// Shared failure-reason classification (extracted from <see cref="PowerShellJobHandler"/>
/// so <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/> does not carry
/// its own, potentially drifting, copy of the #162 bare-digit-run fix).
/// </summary>
public static class AuthFailureClassifier
{
	/// <summary>
	/// Bare digit-run markers (<c>"401"</c>, <c>"403"</c>, ...) match only as a standalone
	/// token, not as a substring: an unanchored match on three digits also fires on
	/// GUIDs, byte counts, ports, or any identifier that happens to embed those digits
	/// (#162). Word markers keep the original coarse substring match -- the vcf modules
	/// surface vendor/HTTP wording, not typed exceptions, and there is no digit-run
	/// ambiguity to guard against there.
	/// </summary>
	public static bool IsAuthFailure(string failureReason, IReadOnlyList<string> markers)
	{
		ArgumentNullException.ThrowIfNull(failureReason);
		ArgumentNullException.ThrowIfNull(markers);

		foreach (string marker in markers)
		{
			bool isMatch = IsBareDigitRun(marker)
				? Regex.IsMatch(failureReason, $@"\b{Regex.Escape(marker)}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
				: failureReason.Contains(marker, StringComparison.OrdinalIgnoreCase);

			if (isMatch)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>True for markers like "401"/"403": digits only, no letters/punctuation
	/// that would already anchor a substring match to auth-ish context.</summary>
	private static bool IsBareDigitRun(string marker) =>
		marker.Length > 0 && marker.All(char.IsAsciiDigit);
}
