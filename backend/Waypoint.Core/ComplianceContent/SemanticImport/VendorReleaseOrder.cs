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

using System.Globalization;
using System.Text.RegularExpressions;

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Parses and orders the two closed vendor release-directory forms observed across the
/// documented family table (docs/compliance-parity.md): <c>V#R#[-stig|-srg]</c> (e.g.
/// <c>v2r3-stig</c>) and <c>Y##M##-srg</c> (e.g. <c>Y26M05-srg</c>). This is a pure,
/// data-free parser/comparator -- no filesystem access, no catalog lookups -- consumed
/// by <see cref="SemanticImportReconciler"/>'s newest-release-wins collision resolution
/// (issue #986, owner decision 2026-08-28).
///
/// Each vendor family in the provenance matrix uses exactly ONE of these two forms
/// consistently (vSphere/VCSA/NSX/Photon/Aria/vIDM STIG releases are all <c>V#R#</c>;
/// the 9.x/SRG generation is all <c>Y##M##-srg</c>) -- docs/compliance-parity.md never
/// documents a family mixing both forms within one declared version scope. Whether a
/// single scope could genuinely contain BOTH forms at once (e.g. a STIG-form and an
/// SRG-form release side by side) is therefore a design hole rather than an answered
/// question: <see cref="Compare"/> deliberately does NOT invent a cross-form ordering.
/// Comparing two releases of different forms throws <see cref="InvalidOperationException"/>
/// so the caller fails closed into collision-quarantine for the tie rather than guessing
/// which form is "newer" -- see <see cref="SemanticImportReconciler"/>'s handling.
/// </summary>
public static class VendorReleaseOrder
{
	// V#R# optionally followed by a -stig/-srg kind suffix (the suffix is not part of
	// ordering identity -- ParseReleaseSegment already splits kind out separately -- but
	// release directory segments carry it, so both bare and suffixed forms parse).
	private static readonly Regex VFormPattern = new(@"^[vV](?<major>\d+)[rR](?<release>\d+)(?:-(?:stig|srg))?$", RegexOptions.Compiled);

	// Y##M## followed by a mandatory -srg suffix (the only vendor generation observed
	// using this form is SRG content; docs/compliance-parity.md never documents a
	// Y##M##-stig release).
	private static readonly Regex YFormPattern = new(@"^[yY](?<year>\d{2})[mM](?<month>\d{2})-srg$", RegexOptions.Compiled);

	/// <summary>
	/// Attempts to parse <paramref name="releaseKey"/> as one of the two closed forms.
	/// Returns <see langword="false"/> for any other shape -- unknown forms fail closed
	/// (never guessed into the nearest-looking form).
	/// </summary>
	public static bool TryParse(string releaseKey, out ParsedRelease parsed)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(releaseKey);

		Match vMatch = VFormPattern.Match(releaseKey);
		if (vMatch.Success)
		{
			parsed = new ParsedRelease(
				releaseKey,
				VendorReleaseForm.VForm,
				int.Parse(vMatch.Groups["major"].Value, CultureInfo.InvariantCulture),
				int.Parse(vMatch.Groups["release"].Value, CultureInfo.InvariantCulture));
			return true;
		}

		Match yMatch = YFormPattern.Match(releaseKey);
		if (yMatch.Success)
		{
			parsed = new ParsedRelease(
				releaseKey,
				VendorReleaseForm.YForm,
				int.Parse(yMatch.Groups["year"].Value, CultureInfo.InvariantCulture),
				int.Parse(yMatch.Groups["month"].Value, CultureInfo.InvariantCulture));
			return true;
		}

		parsed = default;
		return false;
	}

	/// <summary>
	/// Orders two SAME-FORM parsed releases: <see cref="VendorReleaseForm.VForm"/>
	/// compares (major, release) numerically; <see cref="VendorReleaseForm.YForm"/>
	/// compares (year, month) numerically. Both forms use the same two-component
	/// major/minor shape, so the comparison logic is identical once form identity is
	/// confirmed equal.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The two releases are different forms. This is the documented cross-form design
	/// hole (see class remarks): callers must treat this as a fail-closed collision, not
	/// catch-and-guess.
	/// </exception>
	public static int Compare(ParsedRelease left, ParsedRelease right)
	{
		if (left.Form != right.Form)
		{
			throw new InvalidOperationException(
				$"cannot order releases of different forms: '{left.ReleaseKey}' ({left.Form}) vs '{right.ReleaseKey}' ({right.Form}) -- " +
				"cross-form release ordering is an unresolved design hole (issue #986), never guessed");
		}

		int primary = left.Major.CompareTo(right.Major);
		return primary != 0 ? primary : left.Minor.CompareTo(right.Minor);
	}
}

/// <summary>The closed set of recognized vendor release-directory forms.</summary>
public enum VendorReleaseForm
{
	/// <summary><c>V#R#[-stig|-srg]</c>, e.g. <c>v2r3-stig</c>.</summary>
	VForm,

	/// <summary><c>Y##M##-srg</c>, e.g. <c>Y26M05-srg</c>.</summary>
	YForm,
}

/// <summary>
/// One successfully parsed release key. <see cref="Major"/>/<see cref="Minor"/> hold
/// (V, R) for <see cref="VendorReleaseForm.VForm"/> or (year, month) for
/// <see cref="VendorReleaseForm.YForm"/> -- the field names are form-neutral because
/// <see cref="VendorReleaseOrder.Compare"/> treats both forms identically once form
/// identity is confirmed equal.
/// </summary>
public readonly record struct ParsedRelease(string ReleaseKey, VendorReleaseForm Form, int Major, int Minor);
