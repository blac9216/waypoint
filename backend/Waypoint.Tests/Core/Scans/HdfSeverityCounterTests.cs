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
