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
/// Save-time YAML validation (issue #265 AC: "Invalid YAML on save -> 400 with the
/// standard error envelope; valid YAML round-trips byte-stable"). Deliberately a
/// well-formedness check only -- SAF attestation YAML, InSpec input YAML, and
/// remediation input files each carry their own schema owned by Broadcom/MITRE
/// (docs/domain-model.md: "the schemas belong to Broadcom/MITRE and change under us"),
/// so this parses-as-YAML gate is the whole of what this store is allowed to enforce.
/// The submitted text is stored byte-for-byte on success (see
/// <c>ConfigDocRepository.SaveVersionAsync</c>) -- this type never rewrites or
/// re-serializes the input, only parses it to prove it is well-formed.
/// </summary>
public static class YamlDocumentValidator
{
	/// <summary>
	/// Returns null when <paramref name="yaml"/> parses as well-formed YAML; otherwise a
	/// human-readable message safe to surface in the 400 envelope's <c>detail</c>.
	/// </summary>
	public static string? Validate(string? yaml)
	{
		if (string.IsNullOrWhiteSpace(yaml))
		{
			return "Document body must not be empty.";
		}

		try
		{
			YamlStream stream = new();
			stream.Load(new StringReader(yaml));
			return null;
		}
		catch (YamlException exception)
		{
			return $"Invalid YAML at line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}";
		}
	}
}
