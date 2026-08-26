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

using Waypoint.Core.ComplianceContent.Xccdf;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Issue #730 deliverable 1's digest-addressing contract at the importer layer (before
/// any repository/database involvement): two imports of logically identical XCCDF
/// content always produce the same digest, and a genuine content change always
/// produces a different one. Every fixture is invented.
/// </summary>
public sealed class BenchmarkImporterTests
{
	private const string DocumentA = """
		<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
		  <title>Invented Example STIG</title>
		  <version update="1">1</version>
		  <Rule id="SV-1" severity="high"><title>r1</title></Rule>
		  <Rule id="SV-2" severity="low"><title>r2</title></Rule>
		</Benchmark>
		""";

	// Same logical content as DocumentA, reordered rules and incidental whitespace --
	// must digest identically.
	private const string DocumentAReorderedWhitespace = """
		<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">

		  <title>Invented Example STIG</title>
		  <version update="1">1</version>
		  <Rule id="SV-2"   severity="low"><title>r2</title></Rule>
		  <Rule id="SV-1" severity="high"><title>r1</title></Rule>
		</Benchmark>
		""";

	private const string DocumentBDifferentRuleTitle = """
		<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
		  <title>Invented Example STIG</title>
		  <version update="1">1</version>
		  <Rule id="SV-1" severity="high"><title>a genuinely different rule title</title></Rule>
		  <Rule id="SV-2" severity="low"><title>r2</title></Rule>
		</Benchmark>
		""";

	[Fact]
	public void ImportXml_ValidDocument_ProducesCandidateWithDigest()
	{
		BenchmarkImportResult result = BenchmarkImporter.ImportXml(DocumentA);

		Assert.True(result.Succeeded);
		Assert.Null(result.Error);
		Assert.Equal("xccdf_invented.example_benchmark_EX-1-0_STIG", result.Candidate!.BenchmarkKey);
		Assert.Equal(2, result.Candidate.Rules.Count);
		Assert.False(string.IsNullOrWhiteSpace(result.Candidate.ContentDigest));
	}

	[Fact]
	public void ImportXml_ReorderedRulesAndWhitespace_ProducesTheSameDigest()
	{
		BenchmarkImportResult first = BenchmarkImporter.ImportXml(DocumentA);
		BenchmarkImportResult second = BenchmarkImporter.ImportXml(DocumentAReorderedWhitespace);

		Assert.True(first.Succeeded);
		Assert.True(second.Succeeded);
		Assert.Equal(first.Candidate!.ContentDigest, second.Candidate!.ContentDigest);
	}

	[Fact]
	public void ImportXml_GenuinelyDifferentContent_ProducesADifferentDigest()
	{
		BenchmarkImportResult first = BenchmarkImporter.ImportXml(DocumentA);
		BenchmarkImportResult second = BenchmarkImporter.ImportXml(DocumentBDifferentRuleTitle);

		Assert.True(first.Succeeded);
		Assert.True(second.Succeeded);
		Assert.NotEqual(first.Candidate!.ContentDigest, second.Candidate!.ContentDigest);
	}

	[Fact]
	public void ImportXml_MalformedDocument_FailsClosedWithoutThrowing()
	{
		BenchmarkImportResult result = BenchmarkImporter.ImportXml("<Benchmark><unclosed");

		Assert.False(result.Succeeded);
		Assert.Null(result.Candidate);
		Assert.NotNull(result.Error);
	}

	[Fact]
	public void ImportZip_MalformedZip_FailsClosedWithoutThrowing()
	{
		BenchmarkImportResult result = BenchmarkImporter.ImportZip([1, 2, 3, 4]);

		Assert.False(result.Succeeded);
		Assert.Contains("not a valid zip archive", result.Error);
	}
}
