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
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IVksItemNameGrammarParser"/>
public sealed partial class VksItemNameGrammarParser : IVksItemNameGrammarParser
{
	// One grammar covering all four eras research #1031 Layer B found (see the
	// interface doc comment for the full template). The k8s-token slot
	// ([-vmi-k8s|-k8s]) and the arch slot are both independently optional because
	// the current era carries neither a k8s token nor a missing arch, while the two
	// oldest eras carry a k8s token but no arch, and the vmi_k8s era carries both.
	// `---` is captured literally (never translated to `+` here -- that belongs to
	// whichever child renders/orders the version string, per the hand-off note
	// below) and the trailing `.<suffix>` is intentionally permissive (alphanumeric)
	// since #1031 did not enumerate every suffix value observed.
	// arch deliberately requires a trailing digit run ([a-z]+[0-9]+, e.g. amd64/
	// arm64) rather than a bare [a-z0-9]+: the legacy_k8s/tkgs_ova eras' own "-k8s"
	// token would otherwise be greedily (and wrongly) consumed as an arch value
	// before the regex engine backtracks into the k8s-token slot, since "k8s" is
	// itself a valid [a-z0-9]+ string. Requiring a trailing digit makes that a
	// dead end (k8s's own digit run is a single "8" sandwiched between letters, so
	// "[a-z]+[0-9]+" can only ever consume "k8", stranding the "s" and failing the
	// rest of the pattern), which forces the correct backtrack.
	//
	// The grammar's own `[-fips][.<n>]` is two independently optional pieces in
	// series, not one -- `vmware.3-fips.1-tkg.1` (fips revision `.1`) and
	// `vmware.3.1-tkg.1` (a vmware-build sub-revision, no fips) both occur. Only
	// the base `vmware.<n>` digits are kept as this model's `vmware_build`
	// dimension; the trailing `.<n>` (fips-revision or build-sub-revision alike)
	// is matched so the grammar accepts both shapes but is not itself surfaced as
	// a column dispatch's schema does not have.
	[GeneratedRegex(
		@"^ob-(?<buildId>\d+)-(?:tkgs-ova-)?(?<distro>[a-z]+)-(?<distroVersion>[0-9]+(?:\.[0-9]+)*)" +
		@"(?:-(?<arch>[a-z]+[0-9]+))?(?:-(?:vmi-k8s|k8s))?-v(?<k8sVersion>[0-9]+(?:\.[0-9]+)*)---vmware\.(?<vmwareBuild>[0-9]+)" +
		@"(?<fips>-fips)?(?:\.[0-9]+)?-(?<line>vkr|tkg)\.(?<lineBuild>[0-9]+)(?:\.(?<suffix>[a-zA-Z0-9]+))?$")]
	private static partial Regex NameGrammar();

	public VksItemNameParseResult Parse(string rawName)
	{
		ArgumentNullException.ThrowIfNull(rawName);

		Match match = NameGrammar().Match(rawName);
		if (!match.Success)
		{
			return VksItemNameParseResult.Unparsed;
		}

		string? arch = match.Groups["arch"].Success ? match.Groups["arch"].Value : null;
		string namingEra = ClassifyEra(rawName);

		VksItemDimensions dimensions = new(
			Distro: match.Groups["distro"].Value,
			DistroVersion: match.Groups["distroVersion"].Value,
			Arch: arch,
			K8sVersion: match.Groups["k8sVersion"].Value,
			VmwareBuild: match.Groups["vmwareBuild"].Value,
			Fips: match.Groups["fips"].Success,
			ReleaseLine: match.Groups["line"].Value,
			LineBuild: match.Groups["lineBuild"].Value,
			ObBuildId: match.Groups["buildId"].Value);

		return new VksItemNameParseResult(namingEra, VksParseStatuses.Parsed, dimensions);
	}

	/// <summary>
	/// Classifies which of the four eras a successfully-matched name belongs to,
	/// from the literal tokens research #1031 Layer B used to describe them: the
	/// `tkgs-ova-` prefix and the `-vmi-k8s-`/`-k8s-` token are mutually
	/// distinguishing once the grammar itself has already confirmed the name
	/// matches -- this only orders the classification, it does not re-validate.
	/// </summary>
	private static string ClassifyEra(string rawName)
	{
		if (rawName.Contains("tkgs-ova-", StringComparison.Ordinal))
		{
			return VksNamingEras.TkgsOva;
		}

		if (rawName.Contains("-vmi-k8s-", StringComparison.Ordinal))
		{
			return VksNamingEras.VmiK8s;
		}

		if (rawName.Contains("-k8s-", StringComparison.Ordinal))
		{
			return VksNamingEras.LegacyK8s;
		}

		return VksNamingEras.Current;
	}
}

/// <summary>
/// Orders VKR/VKS version strings in the form <c>&lt;k8s&gt;+vmware.&lt;n&gt;-[fips-]vkr.&lt;n&gt;</c>
/// (research #1031: every VKR catalog entry shares one releaseDate, so
/// version-string ordering is the only reliable one). This is a small, local
/// comparator that satisfies only this issue's need to order
/// <see cref="Waypoint.Core.Downloads.VksItemDimensions"/> rows -- never a general
/// version-comparison utility.
///
/// hand-off: adopt the shared comparator from the product-aware version-comparator
/// work once merged, rather than growing this local one further.
/// </summary>
public static class VksVersionOrdering
{
	/// <summary>
	/// Compares two items' dimensions by k8s version, then vmware build, then FIPS
	/// (non-FIPS before FIPS is an arbitrary but stable tiebreak -- FIPS is not
	/// monotonic with recency, #1031), then line build. Any dimension missing on
	/// either side sorts that side first (least information sorts first, never
	/// throws).
	/// </summary>
	public static int Compare(VksItemDimensions left, VksItemDimensions right)
	{
		ArgumentNullException.ThrowIfNull(left);
		ArgumentNullException.ThrowIfNull(right);

		int k8s = CompareDottedVersions(left.K8sVersion, right.K8sVersion);
		if (k8s != 0)
		{
			return k8s;
		}

		int vmwareBuild = CompareDottedVersions(left.VmwareBuild, right.VmwareBuild);
		if (vmwareBuild != 0)
		{
			return vmwareBuild;
		}

		int fips = left.Fips.CompareTo(right.Fips);
		if (fips != 0)
		{
			return fips;
		}

		return CompareDottedVersions(left.LineBuild, right.LineBuild);
	}

	private static int CompareDottedVersions(string? left, string? right)
	{
		if (left is null && right is null)
		{
			return 0;
		}

		if (left is null)
		{
			return -1;
		}

		if (right is null)
		{
			return 1;
		}

		int[] leftSegments = [.. left.Split('.').Select(ParseSegment)];
		int[] rightSegments = [.. right.Split('.').Select(ParseSegment)];
		int length = Math.Max(leftSegments.Length, rightSegments.Length);
		for (int i = 0; i < length; i++)
		{
			int leftValue = i < leftSegments.Length ? leftSegments[i] : 0;
			int rightValue = i < rightSegments.Length ? rightSegments[i] : 0;
			int comparison = leftValue.CompareTo(rightValue);
			if (comparison != 0)
			{
				return comparison;
			}
		}

		return 0;
	}

	private static int ParseSegment(string segment) => int.TryParse(segment, out int value) ? value : 0;
}
