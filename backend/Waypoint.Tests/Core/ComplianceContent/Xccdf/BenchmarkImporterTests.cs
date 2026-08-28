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

using System.IO.Compression;
using System.Text;
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
		IReadOnlyList<BenchmarkImportResult> results = BenchmarkImporter.ImportZip([1, 2, 3, 4]);

		BenchmarkImportResult result = Assert.Single(results);
		Assert.False(result.Succeeded);
		Assert.Contains("not a valid zip archive", result.Error);
	}

	/// <summary>
	/// Issue #1073: a multi-XCCDF package fans out to one <see cref="BenchmarkImportResult"/>
	/// per benchmark it contains, each independently digest-addressed from its own
	/// parsed metadata -- never one-or-error, and never picking one entry and
	/// discarding the rest.
	/// </summary>
	[Fact]
	public void ImportZip_MultiXccdfPackage_ProducesOneSucceededResultPerBenchmark()
	{
		byte[] zipBytes = BuildZip(
			("First-xccdf.xml", DocumentA),
			("Second-xccdf.xml", DocumentBDifferentRuleTitle));

		IReadOnlyList<BenchmarkImportResult> results = BenchmarkImporter.ImportZip(zipBytes);

		Assert.Equal(2, results.Count);
		Assert.All(results, r => Assert.True(r.Succeeded));
		Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Candidate!.SourceEntryPath)));
	}

	[Fact]
	public void ImportZip_OneMalformedEntryAmongMultipleGood_FailsOnlyThatEntry()
	{
		byte[] zipBytes = BuildZip(
			("Good-xccdf.xml", DocumentA),
			("Bad-xccdf.xml", "<Benchmark><unclosed"));

		IReadOnlyList<BenchmarkImportResult> results = BenchmarkImporter.ImportZip(zipBytes);

		Assert.Equal(2, results.Count);
		Assert.Single(results, r => r.Succeeded);
		Assert.Single(results, r => !r.Succeeded);
	}

	private static byte[] BuildZip(params (string Name, string Content)[] entries)
	{
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string name, string content) in entries)
			{
				ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
				using Stream entryStream = entry.Open();
				byte[] bytes = Encoding.UTF8.GetBytes(content);
				entryStream.Write(bytes, 0, bytes.Length);
			}
		}

		return stream.ToArray();
	}
}
