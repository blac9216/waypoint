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

using System.Text.Json;

namespace Waypoint.Core.Scans;

/// <summary>
/// CAT I/II/III open (failed) finding counts for one HDF report -- <c>GET /runs/{id}/artifacts</c>
/// (issue #299), plus (issue #1132) the evaluated-control denominator: <see cref="ControlsTotal"/>
/// is every control this report describes, <see cref="ControlsEvaluated"/> is the subset that
/// actually produced a real outcome (open, i.e. genuinely failed, or genuinely passed) rather than
/// being skipped/not-applicable/errored/absent. <see cref="NoControlsEvaluated"/> is the "this looks
/// clean because nothing ran" signal a Results-table reader must not miss: a report with
/// controls present but zero of them evaluated renders identically to a fully-passing one on
/// the CAT counts alone (all zero either way), so a caller MUST check this denominator before
/// reading <c>0/0/0</c> as "clean" rather than "not evaluated". <see cref="Zero"/> is a
/// genuinely empty report (no controls at all, e.g. the scan stub's own fixture shape) --
/// <see cref="NoControlsEvaluated"/> is false for it, since there is nothing to have evaluated.
///
/// <b>Issue #1144</b>: <see cref="ControlsExecutionError"/> counts controls whose only
/// non-passed/non-skipped/non-not_applicable result is <c>error</c> (InSpec's
/// resource-raised-an-exception outcome) -- reconciled with
/// <see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/>, which is
/// <c>failed</c>-only. An errored control is NOT counted in <see cref="CatIOpen"/>/
/// <see cref="CatIIOpen"/>/<see cref="CatIIIOpen"/> (it never produced a genuine
/// compliance verdict, so it is not "open"), matching the persisted-findings surface
/// exactly: both now count an errored control as execution-error, not open.
/// </summary>
public sealed record HdfSeverityCounts(int CatIOpen, int CatIIOpen, int CatIIIOpen, int ControlsTotal, int ControlsEvaluated, int ControlsExecutionError = 0)
{
	public static readonly HdfSeverityCounts Zero = new(0, 0, 0, 0, 0, 0);

	/// <summary>True when this report describes at least one control but none of them produced a real pass/fail outcome (issue #1132) -- distinct from a genuinely empty report.</summary>
	public bool NoControlsEvaluated => ControlsTotal > 0 && ControlsEvaluated == 0;
}

/// <summary>
/// Reads just enough of an HDF (Heimdall Data Format) JSON report to count open (failed)
/// controls per CAT severity -- deliberately narrow, the same "read only what this needs,
/// not the whole schema" discipline as <see cref="Waypoint.Core.ConfigDocs.AttestationYaml"/>:
/// this does not adopt or validate the rest of the MITRE SAF/InSpec HDF schema (that
/// belongs to Broadcom/MITRE), it only walks <c>profiles[].controls[]</c> for
/// <c>tags.severity</c> (InSpec's <c>low</c>/<c>medium</c>/<c>high</c>/<c>critical</c>,
/// mapped to CAT III/II/I/I -- the STIG severity convention) and
/// <c>results[].status</c> (a control with at least one non-<c>passed</c>,
/// non-<c>skipped</c> result -- i.e. <c>failed</c> or <c>error</c> -- counts as one open
/// finding; a control whose only results are <c>passed</c>/<c>skipped</c>/
/// <c>not_applicable</c>, or with no results at all, does not). An empty <c>controls</c>
/// array (the scan stub's own fixture shape) is a genuine zero. A missing OR malformed
/// file, by contrast, is <b>uncountable</b>: <see cref="CountOpenFindings"/> returns
/// <c>null</c> (never throws, never a fabricated zero) so the caller can present "could
/// not count" distinctly from "zero open findings" -- a corrupt HDF must never render as
/// a clean, compliant row (issue #299 round-1 blocker). This is a best-effort summary for
/// the Results table, not a compliance authority.
///
/// <b>Issue #1124</b>: a <c>skipped</c> control (including an applicable one that
/// could not execute) deliberately does NOT count as open here, same as it does not
/// count as open in the findings vocabulary (<see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/>)
/// -- Not_Reviewed is "not evaluated", not "failed", so it must not inflate the CAT
/// open counts either. This preview intentionally does not distinguish genuine
/// not-applicable from could-not-execute (that distinction lives in
/// <see cref="HdfFindingsParser"/>'s persisted findings, which this narrow CAT-only
/// preview does not read); do not "fix" that here by making skipped count as open.
///
/// <b>Issue #1144</b>: a control with an <c>error</c> result (InSpec's
/// resource-raised-an-exception outcome) likewise does NOT count as open -- it used to
/// (any non-passed/skipped/not_applicable status counted as open, <c>error</c>
/// included), which disagreed with <see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/>
/// (<c>failed</c>-only) and made the same control read "open" here but
/// "execution_error, not open" on the persisted-findings surface. <c>error</c> now
/// counts toward <see cref="HdfSeverityCounts.ControlsExecutionError"/> instead,
/// checked with the same worst-of priority <see cref="HdfFindingsParser.MapStatus"/>
/// already uses (a control with both an <c>error</c> and a <c>failed</c> result is
/// execution-error, not open) -- so both surfaces now classify an errored control
/// identically.
/// </summary>
public static class HdfSeverityCounter
{
	private const string CriticalSeverity = "critical";
	private const string HighSeverity = "high";
	private const string MediumSeverity = "medium";
	private const string PassedStatus = "passed";
	private const string SkippedStatus = "skipped";
	private const string NotApplicableStatus = "not_applicable";
	private const string ErrorStatus = "error";

	/// <summary>
	/// Parses the HDF JSON at <paramref name="hdfPath"/> and returns its CAT I/II/III open
	/// counts, or <c>null</c> when the file is absent, unreadable, or not parseable as an
	/// HDF report ("uncountable" -- distinct from a genuine all-zero count). Never throws.
	/// </summary>
	public static HdfSeverityCounts? CountOpenFindings(string? hdfPath)
	{
		if (string.IsNullOrWhiteSpace(hdfPath) || !File.Exists(hdfPath))
		{
			return null;
		}

		try
		{
			using FileStream stream = File.OpenRead(hdfPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			return CountOpenFindings(document.RootElement);
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static HdfSeverityCounts? CountOpenFindings(JsonElement root)
	{
		int catI = 0, catII = 0, catIII = 0, total = 0, evaluated = 0, executionError = 0;

		if (!root.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Array)
		{
			// Well-formed JSON but not an HDF report shape -- uncountable, not zero.
			return null;
		}

		foreach (JsonElement profile in profiles.EnumerateArray())
		{
			if (!profile.TryGetProperty("controls", out JsonElement controls) || controls.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (JsonElement control in controls.EnumerateArray())
			{
				total++;

				// Issue #1144: error takes priority over failed, same worst-of order
				// HdfFindingsParser.MapStatus uses -- a control with both an error and
				// a failed result is execution-error, not open.
				if (HasErrorResult(control))
				{
					executionError++;
					continue;
				}

				bool open = IsOpen(control);
				if (open)
				{
					evaluated++;
					switch (Severity(control))
					{
						case CriticalSeverity:
						case HighSeverity:
							catI++;
							break;
						case MediumSeverity:
							catII++;
							break;
						default:
							catIII++;
							break;
					}
				}
				else if (HasPassedResult(control))
				{
					evaluated++;
				}
			}
		}

		return new HdfSeverityCounts(catI, catII, catIII, total, evaluated, executionError);
	}

	/// <summary>Issue #1144: true when at least one result on this control is <c>error</c> -- checked BEFORE <see cref="IsOpen"/> so an errored control is never also counted as open.</summary>
	private static bool HasErrorResult(JsonElement control)
	{
		if (!control.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
		{
			return false;
		}

		foreach (JsonElement result in results.EnumerateArray())
		{
			if (result.TryGetProperty("status", out JsonElement statusElement)
				&& statusElement.ValueKind == JsonValueKind.String
				&& string.Equals(statusElement.GetString(), ErrorStatus, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Issue #1144: open means genuinely <c>failed</c> -- matches <see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/> exactly. Callers check <see cref="HasErrorResult"/> first, so <c>error</c> never reaches here as "open".</summary>
	private static bool IsOpen(JsonElement control)
	{
		if (!control.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
		{
			return false;
		}

		foreach (JsonElement result in results.EnumerateArray())
		{
			string? status = result.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
				? statusElement.GetString()
				: null;
			if (status is not (PassedStatus or SkippedStatus or NotApplicableStatus or ErrorStatus) && status is not null)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Issue #1132: true when at least one result on this control genuinely
	/// <c>passed</c> -- the other half of "evaluated" alongside <see cref="IsOpen"/>
	/// (failed/error). A control whose only results are <c>skipped</c>/
	/// <c>not_applicable</c>, or with no results at all, contributes to neither and is
	/// therefore counted in <see cref="HdfSeverityCounts.ControlsTotal"/> but not
	/// <see cref="HdfSeverityCounts.ControlsEvaluated"/>.
	/// </summary>
	private static bool HasPassedResult(JsonElement control)
	{
		if (!control.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
		{
			return false;
		}

		foreach (JsonElement result in results.EnumerateArray())
		{
			if (result.TryGetProperty("status", out JsonElement statusElement)
				&& statusElement.ValueKind == JsonValueKind.String
				&& string.Equals(statusElement.GetString(), PassedStatus, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static string Severity(JsonElement control)
	{
		if (control.TryGetProperty("tags", out JsonElement tags)
			&& tags.TryGetProperty("severity", out JsonElement severityElement)
			&& severityElement.ValueKind == JsonValueKind.String
			&& severityElement.GetString() is { } severity)
		{
			return severity.ToLowerInvariant();
		}

		return string.Empty;
	}
}
