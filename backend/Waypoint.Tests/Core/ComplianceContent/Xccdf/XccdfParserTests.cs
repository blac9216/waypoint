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

using System.Text;
using Waypoint.Core.ComplianceContent.Xccdf;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Issue #730 deliverable 2: "Safe XCCDF/STIG-zip parsing ... untrusted input
/// discipline (bounded sizes, no external entity resolution/XXE, ... malformed ->
/// actionable rejection, never a crash)." Every XCCDF document here is an INVENTED
/// miniature fixture only shaped like public DISA STIG XCCDF structure -- it is not
/// real STIG content (AGENTS.md/CLAUDE.md sanitization policy).
/// </summary>
public sealed class XccdfParserTests
{
	private const string ValidDocument = """
		<?xml version="1.0" encoding="UTF-8"?>
		<Benchmark xmlns="http://checklists.nist.gov/xccdf/1.2" id="xccdf_invented.example_benchmark_EX-1-0_STIG">
		  <title>Invented Example Product 1.0 Security Technical Implementation Guide</title>
		  <version update="3">2</version>
		  <Group id="G-000001">
		    <Rule id="SV-000001r1_rule" severity="high">
		      <title>The example service must require authentication.</title>
		      <ident system="http://cyber.mil/legacy">V-000001</ident>
		    </Rule>
		  </Group>
		  <Group id="G-000002">
		    <Rule id="SV-000002r1_rule" severity="medium">
		      <title>The example service must log administrative actions.</title>
		      <ident system="http://cyber.mil/legacy">V-000002</ident>
		    </Rule>
		  </Group>
		</Benchmark>
		""";

	[Fact]
	public void TryParse_ValidDocument_PopulatesBenchmarkAndRules()
	{
		XccdfDocument? document = XccdfParser.TryParse(ValidDocument, out string? error);

		Assert.Null(error);
		Assert.NotNull(document);
		Assert.Equal("xccdf_invented.example_benchmark_EX-1-0_STIG", document!.BenchmarkId);
		Assert.Equal("Invented Example Product 1.0 Security Technical Implementation Guide", document.Title);
		Assert.Equal("2", document.Version);
		Assert.Equal("3", document.Release);
		Assert.Equal(2, document.Rules.Count);

		XccdfRule first = Assert.Single(document.Rules, r => r.RuleId == "SV-000001r1_rule");
		Assert.Equal("V-000001", first.VulnId);
		Assert.Equal(BenchmarkRuleSeverities.High, first.Severity);
		Assert.Equal("The example service must require authentication.", first.Title);

		XccdfRule second = Assert.Single(document.Rules, r => r.RuleId == "SV-000002r1_rule");
		Assert.Equal(BenchmarkRuleSeverities.Medium, second.Severity);
	}

	[Fact]
	public void TryParse_NullOrEmpty_ReturnsActionableError()
	{
		Assert.Null(XccdfParser.TryParse(null, out string? errorForNull));
		Assert.Contains("empty or missing", errorForNull);

		Assert.Null(XccdfParser.TryParse("   ", out string? errorForWhitespace));
		Assert.Contains("empty or missing", errorForWhitespace);
	}

	[Fact]
	public void TryParse_OversizedDocument_IsRejectedRatherThanParsed()
	{
		string oversized = "<Benchmark id=\"x\">" + new string('a', XccdfParser.MaxDocumentBytes + 1) + "</Benchmark>";

		XccdfDocument? document = XccdfParser.TryParse(oversized, out string? error);

		Assert.Null(document);
		Assert.Contains("byte parse bound", error);
	}

	[Fact]
	public void TryParse_MalformedXml_ReturnsActionableErrorRatherThanThrowing()
	{
		const string malformed = "<Benchmark id=\"x\"><title>Unclosed";

		XccdfDocument? document = XccdfParser.TryParse(malformed, out string? error);

		Assert.Null(document);
		Assert.Contains("not valid/safe XML", error);
	}

	[Fact]
	public void TryParse_DoctypeDeclaration_IsRejectedNotSilentlyIgnored()
	{
		// The classic XXE shape: a DOCTYPE declaring an external/internal entity. Even
		// a harmless-looking DOCTYPE must be a parse error (DtdProcessing.Prohibit),
		// never silently accepted -- Prohibit, not Ignore, is what makes this a
		// rejection rather than a same-output no-op.
		const string withDoctype = """
			<?xml version="1.0"?>
			<!DOCTYPE Benchmark [<!ENTITY xxe "injected">]>
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>&xxe;</title>
			  <version>1</version>
			</Benchmark>
			""";

		XccdfDocument? document = XccdfParser.TryParse(withDoctype, out string? error);

		Assert.Null(document);
		Assert.Contains("not valid/safe XML", error);
	}

	[Fact]
	public void TryParse_ExternalEntityAttempt_NeverResolvesOrLeaksFileContent()
	{
		// A more explicit XXE attempt targeting a local file. Must fail exactly like
		// the internal-entity DOCTYPE case above -- DTD processing is prohibited before
		// any entity (internal or external) is ever considered.
		const string xxeAttempt = """
			<?xml version="1.0"?>
			<!DOCTYPE Benchmark [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
			<Benchmark id="xccdf_invented.example_benchmark_EX-1-0_STIG">
			  <title>&xxe;</title>
			  <version>1</version>
			</Benchmark>
			""";

		XccdfDocument? document = XccdfParser.TryParse(xxeAttempt, out string? error);

		Assert.Null(document);
		Assert.Contains("not valid/safe XML", error);
		Assert.DoesNotContain("root:", error);
	}

	[Fact]
	public void TryParse_MissingTopLevelBenchmarkElement_IsRejected()
	{
		const string wrongRoot = """<NotABenchmark id="x"><title>x</title><version>1</version></NotABenchmark>""";

		XccdfDocument? document = XccdfParser.TryParse(wrongRoot, out string? error);

		Assert.Null(document);
		Assert.Contains("top-level 'Benchmark' element", error);
	}

	[Fact]
	public void TryParse_MissingRequiredIdAttribute_IsRejected()
	{
		const string missingId = """<Benchmark><title>x</title><version>1</version></Benchmark>""";

		XccdfDocument? document = XccdfParser.TryParse(missingId, out string? error);

		Assert.Null(document);
		Assert.Contains("required 'id' attribute", error);
	}

	[Fact]
	public void TryParse_MissingTitleOrVersion_IsRejected()
	{
		Assert.Null(XccdfParser.TryParse("""<Benchmark id="x"><version>1</version></Benchmark>""", out string? titleError));
		Assert.Contains("required 'title'", titleError);

		Assert.Null(XccdfParser.TryParse("""<Benchmark id="x"><title>x</title></Benchmark>""", out string? versionError));
		Assert.Contains("required 'version'", versionError);
	}

	[Fact]
	public void TryParse_RuleWithoutSeverityAttribute_DefaultsToLowRatherThanRejecting()
	{
		const string noSeverity = """
			<Benchmark id="x"><title>t</title><version>1</version>
			  <Rule id="SV-1"><title>r</title></Rule>
			</Benchmark>
			""";

		XccdfDocument? document = XccdfParser.TryParse(noSeverity, out string? error);

		Assert.Null(error);
		Assert.NotNull(document);
		Assert.Equal(BenchmarkRuleSeverities.Low, Assert.Single(document!.Rules).Severity);
	}

	[Fact]
	public void TryParse_RuleWithoutId_IsSkippedRatherThanFailingWholeDocument()
	{
		const string oneRuleMissingId = """
			<Benchmark id="x"><title>t</title><version>1</version>
			  <Rule severity="high"><title>no id</title></Rule>
			  <Rule id="SV-2" severity="low"><title>has id</title></Rule>
			</Benchmark>
			""";

		XccdfDocument? document = XccdfParser.TryParse(oneRuleMissingId, out string? error);

		Assert.Null(error);
		Assert.NotNull(document);
		Assert.Equal("SV-2", Assert.Single(document!.Rules).RuleId);
	}

	[Fact]
	public void TryParse_TooManyRules_IsRejected()
	{
		StringBuilder builder = new();
		builder.Append("""<Benchmark id="x"><title>t</title><version>1</version>""");
		for (int i = 0; i <= XccdfParser.MaxRules; i++)
		{
			builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"""<Rule id="SV-{i}" severity="low"><title>r{i}</title></Rule>""");
		}

		builder.Append("</Benchmark>");

		XccdfDocument? document = XccdfParser.TryParse(builder.ToString(), out string? error);

		Assert.Null(document);
		Assert.Contains("rule parse bound", error);
	}
}
