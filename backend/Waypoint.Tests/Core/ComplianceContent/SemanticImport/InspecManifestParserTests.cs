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

using Waypoint.Core.ComplianceContent.SemanticImport;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Issue #729 deliverable 1: "Safe inspec.yml metadata parsing ... treat content as
/// untrusted input." Every YAML document here is invented for this test.
/// </summary>
public sealed class InspecManifestParserTests
{
	[Fact]
	public void TryParse_FullManifest_PopulatesAllFields()
	{
		const string yaml = """
			name: sample-profile
			title: Sample Profile Title
			version: 1.2.3
			inputs:
			  - name: target_host
			    type: String
			    required: true
			  - name: target_port
			    type: Numeric
			supports:
			  - platform-name: windows
			depends:
			  - name: shared-controls
			""";

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.Null(error);
		Assert.NotNull(manifest);
		Assert.Equal("sample-profile", manifest.Name);
		Assert.Equal("Sample Profile Title", manifest.Title);
		Assert.Equal("1.2.3", manifest.Version);
		Assert.Equal(2, manifest.Inputs.Count);
		Assert.True(manifest.Inputs.Single(i => i.Name == "target_host").Required);
		Assert.False(manifest.Inputs.Single(i => i.Name == "target_port").Required);
		Assert.Equal("windows", Assert.Single(manifest.Supports));
		Assert.Equal("shared-controls", Assert.Single(manifest.Depends));
	}

	[Fact]
	public void TryParse_AttributesAlias_ReadAsInputs()
	{
		const string yaml = """
			name: legacy-profile
			attributes:
			  - name: legacy_input
			    type: String
			""";

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.Null(error);
		Assert.NotNull(manifest);
		Assert.Equal("legacy_input", Assert.Single(manifest.Inputs).Name);
	}

	[Fact]
	public void TryParse_EmptyOrWhitespace_FailsWithActionableError()
	{
		InspecManifest? manifest = InspecManifestParser.TryParse("   ", out string? error);

		Assert.Null(manifest);
		Assert.Contains("empty or missing", error);
	}

	[Fact]
	public void TryParse_InvalidYaml_FailsWithoutThrowing()
	{
		InspecManifest? manifest = InspecManifestParser.TryParse("not: [valid: yaml: at: all", out string? error);

		Assert.Null(manifest);
		Assert.Contains("not valid YAML", error);
	}

	[Fact]
	public void TryParse_NonMappingDocument_FailsWithActionableError()
	{
		InspecManifest? manifest = InspecManifestParser.TryParse("- just\n- a\n- list\n", out string? error);

		Assert.Null(manifest);
		Assert.Contains("top-level mapping", error);
	}

	[Fact]
	public void TryParse_OversizedManifest_FailsWithoutParsing()
	{
		string oversized = "name: x\ntitle: " + new string('a', InspecManifestParser.MaxManifestBytes + 1);

		InspecManifest? manifest = InspecManifestParser.TryParse(oversized, out string? error);

		Assert.Null(manifest);
		Assert.Contains("byte parse bound", error);
	}

	[Fact]
	public void TryParse_MissingOptionalFields_YieldsNullsNotDefaults()
	{
		const string yaml = "name: minimal\n";

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.Null(error);
		Assert.NotNull(manifest);
		Assert.Null(manifest.Title);
		Assert.Null(manifest.Version);
		Assert.Empty(manifest.Inputs);
		Assert.Empty(manifest.Supports);
		Assert.Empty(manifest.Depends);
	}

	[Fact]
	public void TryParse_DoesNotThrow_OnDeeplyNestedOrAnchoredYaml()
	{
		// A YAML anchor/alias bomb shape (bounded here, not exponential) -- proves the
		// representation-model parser tolerates aliasing without custom type resolution
		// blowing up; the byte-size bound above is the primary defense against a much
		// larger amplification attempt.
		const string yaml = """
			name: anchor-test
			a: &anchor [1, 2, 3]
			b: *anchor
			c: *anchor
			""";

		InspecManifest? manifest = InspecManifestParser.TryParse(yaml, out string? error);

		Assert.Null(error);
		Assert.NotNull(manifest);
		Assert.Equal("anchor-test", manifest.Name);
	}
}
