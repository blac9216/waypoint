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
/// Issue #1132: <see cref="HdfSeverityCounter"/>'s evaluated-control denominator --
/// the CAT preview must not render a wholly-unevaluated component's <c>0/0/0</c> the
/// same as a genuinely clean one. All fixtures are invented (AGENTS.md), never
/// captured real HDF/InSpec output.
/// </summary>
public sealed class HdfSeverityCounterTests
{
	[Fact]
	public void CountOpenFindings_AllControlsSkipped_ReportsZeroEvaluatedNotZeroOpen()
	{
		// Round-12 shape: every control on a real ESX 9.1 scan skipped because the
		// target could not be reached -- CAT counts must stay 0 (nothing FAILED), but
		// the evaluated denominator must show nothing was actually checked either.
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "high", [("skipped", "No ESX hosts found by name or in target vCenter; skipping test.")]),
			ControlJson("invented-control-02", "medium", [("skipped", "No ESX hosts found by name or in target vCenter; skipping test.")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.CatIOpen);
		Assert.Equal(0, counts.CatIIOpen);
		Assert.Equal(0, counts.CatIIIOpen);
		Assert.Equal(2, counts.ControlsTotal);
		Assert.Equal(0, counts.ControlsEvaluated);
		Assert.True(counts.NoControlsEvaluated);
	}

	[Fact]
	public void CountOpenFindings_MixOfPassedAndFailed_CountsBothAsEvaluated()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "high", [("passed", "invented ok")]),
			ControlJson("invented-control-02", "critical", [("failed", "invented not ok")]),
			ControlJson("invented-control-03", "low", [("skipped", "invented not applicable"), ]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(1, counts.CatIOpen);
		Assert.Equal(3, counts.ControlsTotal);
		Assert.Equal(2, counts.ControlsEvaluated);
		Assert.False(counts.NoControlsEvaluated);
	}

	[Fact]
	public void CountOpenFindings_EmptyControlsArray_IsGenuineZeroNotUnevaluated()
	{
		string hdf = BuildHdf([]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.ControlsTotal);
		Assert.Equal(0, counts.ControlsEvaluated);
		// Nothing to have evaluated -- distinct from "controls exist but none ran".
		Assert.False(counts.NoControlsEvaluated);
	}

	/// <summary>
	/// Issue #1144: a control whose only result is <c>error</c> (InSpec's
	/// resource-raised-an-exception outcome) must NOT count as open -- it used to
	/// (any non-passed/skipped/not_applicable status counted as open), which
	/// disagreed with <see cref="ComponentFindingStatuses.IsOpen"/> (<c>failed</c>-only)
	/// and made the same control read "open" here but "execution_error" on the
	/// persisted-findings surface. It now counts toward <see cref="HdfSeverityCounts.ControlsExecutionError"/>
	/// and is excluded from <see cref="HdfSeverityCounts.ControlsEvaluated"/> -- an
	/// errored control produced no genuine verdict, so it is not "evaluated" either.
	/// </summary>
	[Fact]
	public void CountOpenFindings_ErroredControl_IsExecutionErrorNotOpen()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "critical", [("error", "invented: undefined method on nil resource")]),
			ControlJson("invented-control-02", "high", [("failed", "invented genuine failure")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		// Only the genuinely-failed control is open -- the errored one is not.
		Assert.Equal(1, counts.CatIOpen);
		Assert.Equal(2, counts.ControlsTotal);
		Assert.Equal(1, counts.ControlsEvaluated);
		Assert.Equal(1, counts.ControlsExecutionError);
	}

	/// <summary>Issue #1144: a control with BOTH an error and a failed result is execution-error, not open -- matches HdfFindingsParser.MapStatus's own worst-of priority (error beats failed).</summary>
	[Fact]
	public void CountOpenFindings_ControlWithBothErrorAndFailedResults_IsExecutionErrorNotOpen()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "high", [("error", "invented: raised mid-check"), ("failed", "invented failure after the error")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.CatIOpen);
		Assert.Equal(1, counts.ControlsExecutionError);
		Assert.Equal(0, counts.ControlsEvaluated);
	}

	[Fact]
	public void CountOpenFindings_MissingFile_ReturnsNullUncountable()
	{
		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings("/nonexistent/invented/path.json");
		Assert.Null(counts);
	}

	[Fact]
	public void Zero_ReportsGenuineEmptyNotUnevaluated()
	{
		Assert.Equal(0, HdfSeverityCounts.Zero.ControlsTotal);
		Assert.False(HdfSeverityCounts.Zero.NoControlsEvaluated);
	}

	/// <summary>
	/// Issue #1144 review round 1, finding 2 -- the reviewer's first exact shape: an
	/// ordinary InSpec control with one <c>passed</c> and one <c>skipped</c> result (an
	/// unsupported <c>describe</c> block). Before <see cref="HdfControlClassifier"/> this
	/// read "evaluated, not errored" here but <c>execution_error</c> on the persisted
	/// findings. Both surfaces are asserted together so the reconciliation cannot silently
	/// regress on one side.
	/// </summary>
	[Fact]
	public void CountOpenFindings_PassedAndSkippedMixedControl_IsExecutionErrorOnBothSurfaces()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "high", [("passed", "invented ok"), ("skipped", "invented: unsupported describe block")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.CatIOpen);
		Assert.Equal(0, counts.CatIIOpen);
		Assert.Equal(0, counts.CatIIIOpen);
		Assert.Equal(1, counts.ControlsTotal);
		Assert.Equal(0, counts.ControlsEvaluated);
		Assert.Equal(1, counts.ControlsExecutionError);

		AssertPersistedStatus(hdf, ComponentFindingStatuses.ExecutionError);
	}

	/// <summary>
	/// Issue #1144 review round 1, finding 2 -- the reviewer's second exact shape: a status
	/// string this reader does not recognize. It used to be counted into
	/// <c>cat_i/ii/iii_open</c> (every unrecognized status was "open"), which is precisely
	/// the inflation this issue set out to remove; it is now an execution error on both
	/// surfaces. Nothing unrecognized may land in a CAT open count.
	/// </summary>
	[Fact]
	public void CountOpenFindings_UnrecognizedStatusString_IsExecutionErrorNotOpen_OnBothSurfaces()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "critical", [("invented_unknown_status", "invented: a status this reader does not know")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.CatIOpen);
		Assert.Equal(0, counts.CatIIOpen);
		Assert.Equal(0, counts.CatIIIOpen);
		Assert.Equal(1, counts.ControlsTotal);
		Assert.Equal(0, counts.ControlsEvaluated);
		Assert.Equal(1, counts.ControlsExecutionError);

		AssertPersistedStatus(hdf, ComponentFindingStatuses.ExecutionError);
	}

	/// <summary>
	/// Issue #1144 review round 1, finding 2 -- the reviewer's third exact shape:
	/// <c>failed</c> + <c>error</c> on one control. Worst-of ordering puts error first on
	/// both surfaces, so the control is an execution error and contributes nothing to the
	/// CAT open counts.
	/// </summary>
	[Fact]
	public void CountOpenFindings_FailedAndErrorMixedControl_IsExecutionErrorOnBothSurfaces()
	{
		string hdf = BuildHdf(
		[
			ControlJson("invented-control-01", "high", [("failed", "invented genuine failure"), ("error", "invented: raised mid-check")]),
		]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(0, counts.CatIOpen);
		Assert.Equal(1, counts.ControlsTotal);
		Assert.Equal(0, counts.ControlsEvaluated);
		Assert.Equal(1, counts.ControlsExecutionError);

		AssertPersistedStatus(hdf, ComponentFindingStatuses.ExecutionError);
	}

	/// <summary>
	/// Issue #1291 / #1144 round 2's documented divergence: a control with a missing
	/// <c>id</c> is counted by <see cref="HdfSeverityCounter"/> (every control the
	/// report describes) but dropped by <see cref="HdfFindingsParser"/> (no identity to
	/// key a persisted finding on) -- here the <c>error</c> shape from the caveat's own
	/// worked example. Both surfaces are asserted on ONE document so a change that makes
	/// either side treat the id-less control like the other fails this test.
	/// </summary>
	[Fact]
	public void CountOpenFindings_IdLessErroredControl_IsCountedByCounterButDroppedByParser()
	{
		string idLessErrored = """{"title": "invented title", "tags": {"severity": "high"}, "results": [{"status": "error", "code_desc": "invented: undefined method on nil resource"}]}""";
		string ordinary = ControlJson("invented-control-01", "high", [("passed", "invented ok")]);
		string hdf = BuildHdf([ordinary, idLessErrored]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(2, counts.ControlsTotal);
		Assert.Equal(1, counts.ControlsEvaluated);
		Assert.Equal(1, counts.ControlsExecutionError);

		HdfParseResult parsed = HdfFindingsParser.Parse(hdf);
		Assert.True(parsed.Success);
		ComponentResultFinding finding = Assert.Single(parsed.Findings);
		Assert.Equal("invented-control-01", finding.ControlId);
	}

	/// <summary>
	/// Issue #1291: the same divergence for a <c>failed</c> id-less control -- the
	/// caveat's general statement ("not a promise the two totals match"), not just its
	/// errored worked example. The id-less control inflates <see cref="HdfSeverityCounts.CatIOpen"/>
	/// and <see cref="HdfSeverityCounts.ControlsEvaluated"/> here while contributing no
	/// finding at all to the persisted surface.
	/// </summary>
	[Fact]
	public void CountOpenFindings_IdLessFailedControl_IsCountedInCatIOpenButDroppedByParser()
	{
		string idLessFailed = """{"title": "invented title", "tags": {"severity": "high"}, "results": [{"status": "failed", "code_desc": "invented genuine failure"}]}""";
		string ordinary = ControlJson("invented-control-01", "low", [("passed", "invented ok")]);
		string hdf = BuildHdf([ordinary, idLessFailed]);

		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(WriteTempHdf(hdf));

		Assert.NotNull(counts);
		Assert.Equal(2, counts.ControlsTotal);
		Assert.Equal(2, counts.ControlsEvaluated);
		Assert.Equal(1, counts.CatIOpen);
		Assert.Equal(0, counts.ControlsExecutionError);

		HdfParseResult parsed = HdfFindingsParser.Parse(hdf);
		Assert.True(parsed.Success);
		ComponentResultFinding finding = Assert.Single(parsed.Findings);
		Assert.Equal("invented-control-01", finding.ControlId);
	}

	/// <summary>
	/// Issue #1144: the reconciliation stated as a property rather than three examples --
	/// for a single-control report, the CAT preview's bucket and the persisted finding's
	/// status are two readings of the ONE <see cref="HdfControlClassifier"/> verdict.
	/// </summary>
	private static void AssertPersistedStatus(string hdf, string expectedStatus)
	{
		HdfParseResult parsed = HdfFindingsParser.Parse(hdf);
		Assert.True(parsed.Success);
		ComponentResultFinding finding = Assert.Single(parsed.Findings);
		Assert.Equal(expectedStatus, finding.Status);
	}

	private static string WriteTempHdf(string json)
	{
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-hdf-severity-test-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, json);
		return path;
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
