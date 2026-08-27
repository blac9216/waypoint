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

using Waypoint.Core.Scans;
using Xunit;

namespace Waypoint.Tests.Core.Scans;

/// <summary>
/// Issue #745: HDF-to-finding parsing matrix, malformed-input discipline, and the
/// epic #726 §6 exactly-once Not_Reviewed rule -- all invented fixtures (AGENTS.md),
/// never captured real HDF/InSpec output.
/// </summary>
public sealed class HdfFindingsParserTests
{
	private const string InventedControlId = "invented-control-01";

	[Fact]
	public void Parse_NullOrWhitespace_IsRejected()
	{
		HdfParseResult result = HdfFindingsParser.Parse((string?)null);
		Assert.False(result.Success);
		Assert.NotNull(result.RejectionReason);
		Assert.Empty(result.Findings);
	}

	[Fact]
	public void Parse_NotJson_IsRejectedNotCrashed()
	{
		HdfParseResult result = HdfFindingsParser.Parse("{ this is not valid json ");
		Assert.False(result.Success);
		Assert.Contains("not valid JSON", result.RejectionReason);
	}

	[Fact]
	public void Parse_ValidJsonButNotHdfShaped_IsRejected()
	{
		HdfParseResult result = HdfFindingsParser.Parse("""{"unrelated": "document"}""");
		Assert.False(result.Success);
		Assert.Contains("profiles", result.RejectionReason, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_TruncatedDocument_IsRejectedNotCrashed()
	{
		// A document that starts as valid HDF shape but is cut off mid-stream --
		// exercises the "bounded, malformed input never crashes" discipline against a
		// realistic truncation rather than only a trivial non-JSON string.
		string truncated = """{"profiles": [{"controls": [{"id": "SV-1", "tags": {"severity":""";
		HdfParseResult result = HdfFindingsParser.Parse(truncated);
		Assert.False(result.Success);
	}

	[Fact]
	public void Parse_EmptyControlsArray_IsGenuineZeroSuccess()
	{
		string hdf = BuildHdf([]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		Assert.Empty(result.Findings);
	}

	[Theory]
	[InlineData("critical", ComponentFindingSeverities.CatI)]
	[InlineData("high", ComponentFindingSeverities.CatI)]
	[InlineData("medium", ComponentFindingSeverities.CatII)]
	[InlineData("low", ComponentFindingSeverities.CatIII)]
	[InlineData(null, ComponentFindingSeverities.CatIII)]
	public void Parse_SeverityMapsToCatVocabulary(string? severity, string expectedSeverity)
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, severity, [("passed", "ok")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		ComponentResultFinding finding = Assert.Single(result.Findings);
		Assert.Equal(expectedSeverity, finding.Severity);
	}

	[Fact]
	public void Parse_AllPassedResults_IsPassed()
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, "medium", [("passed", "ok"), ("passed", "also ok")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.Equal(ComponentFindingStatuses.Passed, Assert.Single(result.Findings).Status);
	}

	[Fact]
	public void Parse_AnyFailedResult_IsFailed()
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, "high", [("passed", "ok"), ("failed", "invented failure detail")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		ComponentResultFinding finding = Assert.Single(result.Findings);
		Assert.Equal(ComponentFindingStatuses.Failed, finding.Status);
		Assert.Contains("invented failure detail", finding.Evidence);
	}

	[Fact]
	public void Parse_AnyErrorResult_IsExecutionError_EvenAlongsideFailed()
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, "high", [("failed", "invented failure"), ("error", "invented resource error")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.Equal(ComponentFindingStatuses.ExecutionError, Assert.Single(result.Findings).Status);
	}

	[Fact]
	public void Parse_AllSkippedResults_IsNotApplicable()
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, "low", [("skipped", "invented n/a reason")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.Equal(ComponentFindingStatuses.NotApplicable, Assert.Single(result.Findings).Status);
	}

	[Fact]
	public void Parse_UnrecognizedStatusString_IsExecutionErrorNeverFabricatedPass()
	{
		string hdf = BuildHdf([ControlJson(InventedControlId, "low", [("something_unrecognized", "invented")])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.Equal(ComponentFindingStatuses.ExecutionError, Assert.Single(result.Findings).Status);
	}

	/// <summary>Epic #726 §6: a control with an empty results array never ran -- exactly one Not_Reviewed finding, never omitted, never Not_Applicable.</summary>
	[Fact]
	public void Parse_EmptyResultsArray_IsExactlyOneNotReviewed()
	{
		string hdf = """{"profiles": [{"controls": [{"id": "invented-control-01", "tags": {"severity": "high"}, "results": []}]}]}""";
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		ComponentResultFinding finding = Assert.Single(result.Findings);
		Assert.Equal(ComponentFindingStatuses.NotReviewed, finding.Status);
	}

	/// <summary>Same rule, no <c>results</c> key at all (a different malformed-but-not-crashing shape than an empty array).</summary>
	[Fact]
	public void Parse_MissingResultsProperty_IsExactlyOneNotReviewed()
	{
		string hdf = """{"profiles": [{"controls": [{"id": "invented-control-01", "tags": {"severity": "high"}}]}]}""";
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		Assert.Equal(ComponentFindingStatuses.NotReviewed, Assert.Single(result.Findings).Status);
	}

	/// <summary>
	/// Fixture-monoculture guard (docs/testing.md): the exactly-once rule must hold
	/// across MULTIPLE controls in the same profile, not just a single-control fixture
	/// -- a bug that only drops the LAST unreachable control in a list would pass a
	/// single-control suite.
	/// </summary>
	[Fact]
	public void Parse_MultipleControls_MixOfRanAndNeverRan_EachAccountedForExactlyOnce()
	{
		string hdf = BuildHdf(
		[
			ControlJson("SV-1", "high", [("passed", "ok")]),
			ControlJson("SV-2", "medium", []),
			ControlJson("SV-3", "low", [("failed", "invented")]),
		]);

		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		Assert.Equal(3, result.Findings.Count);
		Assert.Equal(ComponentFindingStatuses.Passed, result.Findings.Single(f => f.ControlId == "SV-1").Status);
		Assert.Equal(ComponentFindingStatuses.NotReviewed, result.Findings.Single(f => f.ControlId == "SV-2").Status);
		Assert.Equal(ComponentFindingStatuses.Failed, result.Findings.Single(f => f.ControlId == "SV-3").Status);
	}

	[Fact]
	public void Parse_ControlWithNoId_IsSkippedNotFabricated()
	{
		string hdf = """{"profiles": [{"controls": [{"tags": {"severity": "high"}, "results": [{"status": "passed"}]}]}]}""";
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		Assert.Empty(result.Findings);
	}

	[Fact]
	public void Parse_XccdfRuleIdFromTagsRid_IsCaptured()
	{
		string hdf = """{"profiles": [{"controls": [{"id": "invented-control-01", "tags": {"severity": "high", "rid": "SV-invented-r1_rule"}, "results": [{"status": "passed"}]}]}]}""";
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.Equal("SV-invented-r1_rule", Assert.Single(result.Findings).RuleId);
	}

	[Fact]
	public void Parse_EvidenceIsBoundedInLength()
	{
		string longMessage = new('x', 5000);
		string hdf = BuildHdf([ControlJson(InventedControlId, "high", [("failed", longMessage)])]);
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		string? evidence = Assert.Single(result.Findings).Evidence;
		Assert.NotNull(evidence);
		Assert.True(evidence!.Length < longMessage.Length);
		Assert.EndsWith("...(truncated)", evidence, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_NonObjectControlsAndProfiles_AreSkippedNotCrashed()
	{
		string hdf = """{"profiles": [42, {"controls": ["not-an-object", {"id": "SV-ok", "tags": {"severity": "low"}, "results": [{"status": "passed"}]}]}]}""";
		HdfParseResult result = HdfFindingsParser.Parse(hdf);
		Assert.True(result.Success);
		ComponentResultFinding finding = Assert.Single(result.Findings);
		Assert.Equal("SV-ok", finding.ControlId);
	}

	private static string BuildHdf(IReadOnlyList<string> controlsJson) =>
		"""{"profiles": [{"controls": [""" + string.Join(",", controlsJson) + "]}]}";

	private static string ControlJson(string controlId, string? severity, IReadOnlyList<(string Status, string Message)> results)
	{
		string severityJson = severity is null ? "null" : $"\"{severity}\"";
		string resultsJson = string.Join(",", results.Select(r => $$"""{"status": "{{r.Status}}", "code_desc": "{{r.Message}}"}"""));
		return $$"""{"id": "{{controlId}}", "title": "invented title", "tags": {"severity": {{severityJson}}}, "results": [{{resultsJson}}]}""";
	}
}
