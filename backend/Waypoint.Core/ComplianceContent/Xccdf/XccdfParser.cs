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

using System.Xml;

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// Parses an XCCDF <c>Benchmark</c> XML document into an <see cref="XccdfDocument"/>,
/// tolerating any malformed/oversized/unexpected document rather than throwing --
/// mirrors <c>InspecManifestParser</c>'s convention (issue #729): a document this
/// cannot parse becomes an actionable diagnostic, never an unhandled exception that
/// aborts the whole import (issue #730 AC "malformed input protections ... never a
/// crash").
///
/// Untrusted-input discipline (issue #730 AC "size, path traversal, entity-expansion,
/// and malformed-input protections"):
/// <list type="bullet">
/// <item><see cref="MaxDocumentBytes"/> bounds the XML this parser will attempt.</item>
/// <item><see cref="XmlReaderSettings.DtdProcessing"/> is <c>Prohibit</c> (not merely
/// <c>Ignore</c>) so a DOCTYPE declaration is a parse error, not a silently accepted
/// no-op -- the standard .NET XXE defense: DTD processing is what enables both external
/// entity expansion and the "billion laughs" internal-entity bomb.</item>
/// <item><see cref="XmlReaderSettings.XmlResolver"/> is left <see langword="null"/>
/// (the default for a settings object explicitly constructed here, not inherited from
/// a caller), so even if some future document sneaks past DTD prohibition, nothing
/// resolves an external URI.</item>
/// <item><see cref="XmlReaderSettings.MaxCharactersInDocument"/> is a second, reader-level
/// bound belt-and-suspenders alongside the byte-length check.</item>
/// </list>
/// </summary>
public static class XccdfParser
{
	/// <summary>Bound on document size this parser will attempt (untrusted input).</summary>
	public const int MaxDocumentBytes = 8 * 1024 * 1024;

	/// <summary>Bound on distinct rules a single benchmark revision may declare (defense against a maliciously huge rule list exhausting memory).</summary>
	public const int MaxRules = 20_000;

	/// <summary>
	/// Attempts to parse <paramref name="xmlText"/>. Returns <see langword="null"/> plus
	/// a human-actionable <paramref name="error"/> on any malformed/oversized/
	/// unexpected-shape document; never throws for untrusted content.
	/// </summary>
	public static XccdfDocument? TryParse(string? xmlText, out string? error)
	{
		if (string.IsNullOrWhiteSpace(xmlText))
		{
			error = "XCCDF document is empty or missing";
			return null;
		}

		if (xmlText.Length > MaxDocumentBytes)
		{
			error = $"XCCDF document exceeds the {MaxDocumentBytes}-byte parse bound ({xmlText.Length} bytes)";
			return null;
		}

		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersInDocument = MaxDocumentBytes,
			IgnoreComments = true,
			IgnoreProcessingInstructions = true,
			IgnoreWhitespace = true,
			CloseInput = true,
		};

		XmlDocument document = new() { XmlResolver = null };

		try
		{
			using StringReader textReader = new(xmlText);
			using XmlReader reader = XmlReader.Create(textReader, settings);
			document.Load(reader);
		}
		catch (Exception ex) when (ex is XmlException or InvalidOperationException or NotSupportedException)
		{
			error = $"XCCDF document is not valid/safe XML: {ex.Message}";
			return null;
		}

		XmlElement? root = document.DocumentElement;
		if (root is null || !string.Equals(LocalName(root), "Benchmark", StringComparison.Ordinal))
		{
			error = "XCCDF document does not have a top-level 'Benchmark' element";
			return null;
		}

		string? benchmarkId = root.GetAttribute("id");
		if (string.IsNullOrWhiteSpace(benchmarkId))
		{
			error = "Benchmark element is missing a required 'id' attribute";
			return null;
		}

		string? title = FindChildText(root, "title");
		if (string.IsNullOrWhiteSpace(title))
		{
			error = "Benchmark element is missing a required 'title' element";
			return null;
		}

		string? version = FindChildText(root, "version");
		if (string.IsNullOrWhiteSpace(version))
		{
			error = "Benchmark element is missing a required 'version' element";
			return null;
		}

		// DISA's XCCDF convention splits version/release either as "version" plus a
		// plain-text-note "release" child, or as "N.R" within the version element
		// itself. Both are accepted, read-only, never guessed past what is declared.
		string release = FindChildAttribute(root, "version", "update") ?? ExtractReleaseFromVersion(version) ?? "0";

		List<XccdfRule> rules = [];
		foreach (XmlElement ruleElement in EnumerateDescendants(root, "Rule"))
		{
			if (rules.Count >= MaxRules)
			{
				error = $"XCCDF document declares more than the {MaxRules}-rule parse bound";
				return null;
			}

			string? ruleId = ruleElement.GetAttribute("id");
			if (string.IsNullOrWhiteSpace(ruleId))
			{
				// A rule with no id is not identifiable; skip it as a per-rule quarantine
				// rather than failing the whole document -- the importer reports the count
				// discrepancy via rule_count vs. actual persisted rules.
				continue;
			}

			string severity = NormalizeSeverity(ruleElement.GetAttribute("severity"));
			string ruleTitle = FindChildText(ruleElement, "title") ?? ruleId;
			string? vulnId = FindVulnId(ruleElement);

			rules.Add(new XccdfRule(ruleId, vulnId, severity, ruleTitle));
		}

		error = null;
		return new XccdfDocument(benchmarkId, title, version, release, rules);
	}

	private static string NormalizeSeverity(string? rawSeverity) => rawSeverity?.Trim().ToLowerInvariant() switch
	{
		"low" => BenchmarkRuleSeverities.Low,
		"medium" => BenchmarkRuleSeverities.Medium,
		"high" => BenchmarkRuleSeverities.High,
		// XCCDF permits "unknown" and a missing attribute; DISA STIGs never leave CAT
		// unset in practice, but this parser treats an absent/unrecognized value as the
		// safest (non-silent) default rather than rejecting the whole document over one
		// rule's missing attribute -- CAT III/low is XCCDF's own documented default.
		_ => BenchmarkRuleSeverities.Low,
	};

	private static string? FindVulnId(XmlElement ruleElement)
	{
		// DISA's convention nests the V-#### legacy vulnerability id under
		// <ident system=".../legacy">V-000001</ident>; fall back to any <ident> text if
		// no legacy-system one is present, but never fabricate one.
		XmlElement? legacyIdent = EnumerateDescendants(ruleElement, "ident")
			.FirstOrDefault(e => (e.GetAttribute("system") ?? string.Empty).Contains("legacy", StringComparison.OrdinalIgnoreCase));
		if (legacyIdent is not null && !string.IsNullOrWhiteSpace(legacyIdent.InnerText))
		{
			return legacyIdent.InnerText.Trim();
		}

		XmlElement? anyIdent = EnumerateDescendants(ruleElement, "ident").FirstOrDefault();
		return string.IsNullOrWhiteSpace(anyIdent?.InnerText) ? null : anyIdent!.InnerText.Trim();
	}

	private static string? ExtractReleaseFromVersion(string version)
	{
		int dotIndex = version.IndexOf('.', StringComparison.Ordinal);
		return dotIndex >= 0 && dotIndex < version.Length - 1 ? version[(dotIndex + 1)..] : null;
	}

	private static string? FindChildText(XmlElement parent, string localName)
	{
		foreach (XmlNode child in parent.ChildNodes)
		{
			if (child is XmlElement element && string.Equals(LocalName(element), localName, StringComparison.OrdinalIgnoreCase))
			{
				return element.InnerText;
			}
		}

		return null;
	}

	private static string? FindChildAttribute(XmlElement parent, string localName, string attributeName)
	{
		foreach (XmlNode child in parent.ChildNodes)
		{
			if (child is XmlElement element && string.Equals(LocalName(element), localName, StringComparison.OrdinalIgnoreCase))
			{
				string value = element.GetAttribute(attributeName);
				return string.IsNullOrWhiteSpace(value) ? null : value;
			}
		}

		return null;
	}

	/// <summary>
	/// Depth-first descendants matching <paramref name="localName"/>, ignoring any XML
	/// namespace prefix -- DISA XCCDF documents vary in their declared default
	/// namespace/prefix across releases, and this parser only ever needs local-name
	/// matching, never full namespace-qualified identity.
	/// </summary>
	private static IEnumerable<XmlElement> EnumerateDescendants(XmlElement root, string localName)
	{
		foreach (XmlNode node in root.ChildNodes)
		{
			if (node is not XmlElement element)
			{
				continue;
			}

			if (string.Equals(LocalName(element), localName, StringComparison.OrdinalIgnoreCase))
			{
				yield return element;
			}

			foreach (XmlElement descendant in EnumerateDescendants(element, localName))
			{
				yield return descendant;
			}
		}
	}

	private static string LocalName(XmlElement element) => element.LocalName;
}
