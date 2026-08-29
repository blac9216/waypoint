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

using YamlDotNet.RepresentationModel;

namespace Waypoint.Core.Scans;

/// <summary>
/// Issue #911 (found during PR #907 review, closed by #739/#740's slice that finally
/// makes the hazard reachable): the platform passes TWO <c>--input-file</c> flags to one
/// narrowed InSpec invocation -- the platform-authored selector-scoping file (carrying
/// <c>vsphereSelectorKind</c>/<c>vmhostName</c>/<c>vmName</c>) and the operator-authored
/// config-doc-derived inputs file (issue #738/#879). InSpec applies later
/// <c>--input-file</c> keys over earlier ones on a literal key collision, and
/// <c>YamlDocumentValidator</c> deliberately enforces no key allowlist on a config doc
/// body ("the schemas belong to Broadcom/MITRE") -- so an operator-writable Input
/// config-doc body naming one of these reserved keys could otherwise override the
/// platform-computed scope and WIDEN a narrowed esxi/vm scan.
///
/// This is a FILTER, not a hard reject: an Input config doc is validated only for YAML
/// well-formedness at save time (no schema/key allowlist -- by design, per
/// docs/domain-model.md), so a reserved key colliding here is far more likely an
/// operator accident (copy-pasted a scoping example into a general inputs doc) than
/// deliberate widening. Silently dropping the key and logging a WARN keeps the scan
/// running at its correctly narrowed scope (fail-closed on SCOPE, per epic #726 Wave 3's
/// "never silently widen" discipline) while still executing the item -- hard-rejecting
/// the whole scan job over one stray key in an otherwise-legitimate operator document
/// would be disproportionate and does not serve the ACTUAL fail-closed target here
/// (scope integrity), which the drop already fully guarantees. The caller is expected to
/// emit the returned dropped-key list as a job.log WARN event (attribution, not a mere
/// filtered return) -- see <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/>.
///
/// Ordering is a second, independent line of defense: the caller must ALSO append the
/// platform selector-scoping file's <c>--input-file</c> flag AFTER (never before) the
/// config-doc-derived file's flag, so even a key this filter somehow missed still loses
/// to the platform's own scoping value on InSpec's last-file-wins semantics -- belt and
/// suspenders, not an either/or.
///
/// Issue #742 (NSX, epic #726 Wave 3's final transport) extends the reserved set with
/// the NSX session-authentication input keys <c>Invoke-WaypointNsxScan</c> generates
/// (<c>nsxManager</c>/<c>sessionToken</c>/<c>sessionCookieId</c> for the NSX 4.x
/// baselines, <c>nsx_managerAddress</c>/<c>nsx_sessionToken</c>/<c>nsx_sessionCookieId</c>
/// for the VCF 9.x NSX baselines): these are SECRET runner-generated auth inputs, not
/// merely platform scoping -- an operator config-doc body naming one must never be
/// allowed to inject a value the InSpec http() resource would then authenticate with
/// (or, on the more benign end, simply desync from the real session and break the
/// scan). The auth-input file is always appended LAST by
/// <c>Invoke-WaypointNsxScan</c> (after the operator inputs file), so this filter is
/// the PRIMARY defense here -- the ordering-based "belt and suspenders" described
/// above is still the second, independent line for every reserved key including
/// these.
/// </summary>
public static class ScanScopingInputFilter
{
	/// <summary>
	/// The platform selector-scoping keys (<c>WaypointScan.psm1</c>'s generated
	/// <c>$InputsPath</c> file) that an operator config-doc-derived inputs body must
	/// never be allowed to redefine. Issue #1123: includes both the 8.0 STIG names
	/// (<c>vmhostName</c>/<c>vmName</c>) and the VCF 9.x SRG names
	/// (<c>esx_vmhostName</c>/<c>vm_Name</c>) -- a given component's resolved profile
	/// only ever declares/uses one pair, but reserving both means an operator
	/// config-doc author can never smuggle either content generation's scoping key
	/// past the filter regardless of which baseline the component happens to run.
	/// </summary>
	public static readonly IReadOnlyCollection<string> ReservedScopingKeys =
		["vsphereSelectorKind", "vmhostName", "vmName", "esx_vmhostName", "vm_Name"];

	/// <summary>
	/// Issue #742: the NSX session-authentication input keys -- secret-carrying
	/// (session token/cookie), not merely scope-narrowing, but reserved through the
	/// same drop-and-warn mechanism since they ride the same operator-writable Input
	/// config-doc surface. Both the NSX 4.x (STIG) and VCF 9.x NSX (SRG) baselines'
	/// key names are included; a given component's catalog kind only ever uses one
	/// set, but reserving both means a config-doc author can never smuggle either
	/// baseline's auth shape past the filter.
	/// </summary>
	public static readonly IReadOnlyCollection<string> ReservedNsxAuthKeys =
		["nsxManager", "sessionToken", "sessionCookieId", "nsx_managerAddress", "nsx_sessionToken", "nsx_sessionCookieId"];

	/// <summary>The full reserved-key set: platform vSphere scoping keys plus NSX auth-input keys.</summary>
	public static readonly IReadOnlyCollection<string> AllReservedKeys =
		[.. ReservedScopingKeys, .. ReservedNsxAuthKeys];

	/// <summary>
	/// Removes any top-level mapping key in <paramref name="yaml"/> that collides with
	/// <see cref="ReservedScopingKeys"/>, returning the filtered document text plus the
	/// list of keys actually dropped (empty when nothing collided -- the overwhelmingly
	/// common case). A document that is not a top-level mapping (or fails to parse -- it
	/// already passed <c>YamlDocumentValidator</c> at save time, but this is re-read
	/// later, so a defensive fallback still applies) is returned unfiltered: this filter
	/// only ever narrows a mapping's keys, never rejects or rewrites a document shape it
	/// does not understand.
	/// </summary>
	public static ScanScopingFilterResult Filter(string yaml)
	{
		ArgumentNullException.ThrowIfNull(yaml);

		YamlStream stream = new();
		try
		{
			stream.Load(new StringReader(yaml));
		}
		catch (YamlDotNet.Core.YamlException)
		{
			return new ScanScopingFilterResult(yaml, []);
		}

		if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
		{
			return new ScanScopingFilterResult(yaml, []);
		}

		List<string> droppedKeys = [];
		List<KeyValuePair<YamlNode, YamlNode>> keptEntries = [];
		foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
		{
			if (entry.Key is YamlScalarNode { Value: { } keyText } && AllReservedKeys.Contains(keyText))
			{
				droppedKeys.Add(keyText);
				continue;
			}

			keptEntries.Add(entry);
		}

		if (droppedKeys.Count == 0)
		{
			return new ScanScopingFilterResult(yaml, []);
		}

		// Re-emit the surviving entries as simple `key: "value"` lines rather than a full
		// YamlDotNet serializer round-trip -- every value this filter has ever needed to
		// preserve is a plain scalar (InSpec inputs are flat key/value documents), and a
		// minimal re-emit avoids YamlDotNet's emitter normalizing quoting/anchors/comments
		// in ways that could change an unrelated, perfectly valid operator value.
		List<string> lines = [];
		foreach (KeyValuePair<YamlNode, YamlNode> entry in keptEntries)
		{
			string key = ((YamlScalarNode)entry.Key).Value ?? string.Empty;
			string value = entry.Value is YamlScalarNode { Value: { } scalarValue } ? scalarValue : string.Empty;
			lines.Add($"{key}: {YamlScalarQuote(value)}");
		}

		return new ScanScopingFilterResult(string.Join('\n', lines), droppedKeys);
	}

	private static string YamlScalarQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// The outcome of <see cref="ScanScopingInputFilter.Filter"/>: the (possibly rewritten)
/// document body plus every reserved key actually removed from it.
/// </summary>
public sealed record ScanScopingFilterResult(string FilteredYaml, IReadOnlyList<string> DroppedKeys);
