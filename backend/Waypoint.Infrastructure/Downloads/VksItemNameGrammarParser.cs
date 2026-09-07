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
	// dimension; the trailing `.<n>` -- whether it is a fips revision or a
	// vmware-build sub-revision -- is matched so the grammar accepts both shapes,
	// but it is deliberately discarded rather than captured into any column: this
	// model's schema has no sub-revision dimension for it to populate. Two
	// upstream items differing only in that trailing `.<n>` therefore land on the
	// identical `vmware_build` value (the fixture
	// `ob-10000005-photon-3-k8s-v1.16.15---vmware.3.1-tkg.4` yields
	// `vmwareBuild == "3"`, dropping the `.1`), which matters to any later
	// consumer ordering or de-duplicating on that dimension.
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
