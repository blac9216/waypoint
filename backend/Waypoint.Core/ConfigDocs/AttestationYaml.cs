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

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Waypoint.Core.ConfigDocs;

/// <summary>
/// Reads just enough of a SAF attestation YAML body to answer "has this waiver expired"
/// (docs/domain-model.md: "Expired attestations are not applied"; prototype attestation
/// EFFECTIVE card: <c>status: Not_A_Finding / justification: ... / expires: 2027-03-01</c>).
/// Deliberately narrow -- like <see cref="YamlDocumentValidator"/>, this does not adopt or
/// validate the rest of the SAF schema (that belongs to Broadcom/MITRE), it only looks for
/// a top-level <c>expires</c> scalar.
/// </summary>
public static class AttestationYaml
{
	private const string ExpiresKey = "expires";

	/// <summary>
	/// Returns the parsed <c>expires</c> date (midnight UTC on that date) if the YAML has a
	/// well-formed top-level <c>expires</c> scalar, otherwise null -- a doc with no
	/// <c>expires</c> key, an unparsable body, or a non-date value never expires by this
	/// check (fails safe toward "still applies"; malformed bodies are already rejected at
	/// save time by <see cref="YamlDocumentValidator"/>).
	/// </summary>
	public static DateTimeOffset? TryGetExpiresAt(string bodyYaml)
	{
		if (string.IsNullOrWhiteSpace(bodyYaml))
		{
			return null;
		}

		try
		{
			YamlStream stream = new();
			stream.Load(new StringReader(bodyYaml));
			if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
			{
				return null;
			}

			foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
			{
				if (entry.Key is YamlScalarNode { Value: ExpiresKey } && entry.Value is YamlScalarNode { Value: { } raw }
					&& DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
				{
					return parsed;
				}
			}

			return null;
		}
		catch (YamlException)
		{
			return null;
		}
	}
}
