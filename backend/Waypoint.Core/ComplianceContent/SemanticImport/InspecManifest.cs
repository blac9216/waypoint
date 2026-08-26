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

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>
/// One declared InSpec input (<c>inputs:</c> entry of an <c>inspec.yml</c>). Only the
/// fields the importer needs to reconcile/report on are captured -- an
/// <c>inspec.yml</c> is untrusted vendor input, so this is a narrow projection, not a
/// full schema mirror.
/// </summary>
public sealed record InspecManifestInput(string Name, string? Type, bool Required);

/// <summary>
/// A safely parsed <c>inspec.yml</c> manifest (issue #729 AC "Profile title, version,
/// declared inputs ... are populated from source metadata"). Parsing goes through
/// YamlDotNet's representation model (no custom-tag/type resolution, no aliasing
/// beyond what YAML 1.1 core schema itself performs) -- content under
/// <c>compliance-content</c> is untrusted vendor input, never deserialized into
/// arbitrary CLR types.
/// </summary>
public sealed record InspecManifest(
	string? Name,
	string? Title,
	string? Version,
	IReadOnlyList<InspecManifestInput> Inputs,
	IReadOnlyList<string> Supports,
	IReadOnlyList<string> Depends);

/// <summary>
/// Parses <c>inspec.yml</c> text into an <see cref="InspecManifest"/>, tolerating any
/// malformed or partial document rather than throwing -- issue #729 AC "unknown/new
/// layouts are quarantined with actionable diagnostics rather than guessed" starts
/// here: a manifest this cannot parse becomes a diagnostic the hierarchy interpreter
/// surfaces as a rejection, never an unhandled exception that aborts the whole import.
/// </summary>
public static class InspecManifestParser
{
	/// <summary>Bound on manifest size this parser will attempt (untrusted input; issue #729 "treat content as untrusted input").</summary>
	public const int MaxManifestBytes = 256 * 1024;

	/// <summary>
	/// Attempts to parse <paramref name="yamlText"/>. Returns <see langword="null"/> plus
	/// a human-actionable <paramref name="error"/> on any malformed/oversized/non-mapping
	/// document; never throws for untrusted content.
	/// </summary>
	public static InspecManifest? TryParse(string? yamlText, out string? error)
	{
		if (string.IsNullOrWhiteSpace(yamlText))
		{
			error = "inspec.yml is empty or missing";
			return null;
		}

		if (yamlText.Length > MaxManifestBytes)
		{
			error = $"inspec.yml exceeds the {MaxManifestBytes}-byte parse bound ({yamlText.Length} bytes)";
			return null;
		}

		YamlStream stream = new();
		try
		{
			using StringReader reader = new(yamlText);
			stream.Load(reader);
		}
		catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
		{
			error = $"inspec.yml is not valid YAML: {ex.Message}";
			return null;
		}

		if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
		{
			error = "inspec.yml does not contain a top-level mapping";
			return null;
		}

		string? name = ReadScalar(root, "name");
		string? title = ReadScalar(root, "title");
		string? version = ReadScalar(root, "version");
		IReadOnlyList<InspecManifestInput> inputs = ReadInputs(root);
		IReadOnlyList<string> supports = ReadSupports(root);
		IReadOnlyList<string> depends = ReadDepends(root);

		error = null;
		return new InspecManifest(name, title, version, inputs, supports, depends);
	}

	private static string? ReadScalar(YamlMappingNode root, string key)
	{
		if (root.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) && value is YamlScalarNode scalar)
		{
			return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value;
		}

		return null;
	}

	private static List<InspecManifestInput> ReadInputs(YamlMappingNode root)
	{
		// InSpec historically used `attributes:`; `inputs:` is the current key. Both are
		// accepted as a read-only alias -- this importer never writes the file back.
		YamlNode? node = FindFirst(root, "inputs", "attributes");
		if (node is not YamlSequenceNode sequence)
		{
			return [];
		}

		List<InspecManifestInput> results = [];
		foreach (YamlNode item in sequence.Children)
		{
			if (item is not YamlMappingNode entry)
			{
				continue;
			}

			string? inputName = ReadScalar(entry, "name");
			if (string.IsNullOrWhiteSpace(inputName))
			{
				continue;
			}

			string? type = ReadScalar(entry, "type");
			bool required = ReadScalar(entry, "required") is "true";
			results.Add(new InspecManifestInput(inputName, type, required));
		}

		return results;
	}

	private static List<string> ReadSupports(YamlMappingNode root)
	{
		if (root.Children.TryGetValue(new YamlScalarNode("supports"), out YamlNode? value) && value is YamlSequenceNode sequence)
		{
			return ReadPlatformNames(sequence);
		}

		return [];
	}

	private static List<string> ReadPlatformNames(YamlSequenceNode sequence)
	{
		List<string> names = [];
		foreach (YamlNode item in sequence.Children)
		{
			switch (item)
			{
				case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value):
					names.Add(scalar.Value);
					break;
				case YamlMappingNode mapping:
					string? platformName = ReadScalar(mapping, "platform-name") ?? ReadScalar(mapping, "platform");
					if (!string.IsNullOrWhiteSpace(platformName))
					{
						names.Add(platformName);
					}

					break;
			}
		}

		return names;
	}

	private static List<string> ReadDepends(YamlMappingNode root)
	{
		if (!root.Children.TryGetValue(new YamlScalarNode("depends"), out YamlNode? value) || value is not YamlSequenceNode sequence)
		{
			return [];
		}

		List<string> names = [];
		foreach (YamlNode item in sequence.Children)
		{
			if (item is YamlMappingNode mapping)
			{
				string? dependencyName = ReadScalar(mapping, "name");
				if (!string.IsNullOrWhiteSpace(dependencyName))
				{
					names.Add(dependencyName);
				}
			}
			else if (item is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
			{
				names.Add(scalar.Value);
			}
		}

		return names;
	}

	private static YamlNode? FindFirst(YamlMappingNode root, params string[] keys)
	{
		foreach (string key in keys)
		{
			if (root.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
			{
				return value;
			}
		}

		return null;
	}
}
