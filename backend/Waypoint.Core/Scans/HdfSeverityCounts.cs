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
/// <b>Issue #1144</b>: <see cref="ControlsExecutionError"/> counts controls this reader
/// cannot turn into a genuine compliance verdict -- an <c>error</c> result (InSpec's
/// resource-raised-an-exception outcome), an unrecognized status string, or a mixed
/// result shape the reader does not specifically recognize. The classification is
/// <see cref="HdfControlClassifier"/>, the single shared rule
/// <see cref="HdfFindingsParser"/> also uses, so a control lands in the same bucket on
/// this preview and on the persisted <c>component_result_findings</c> rows behind
/// <c>GET /runs/{id}/component-results/summary</c>. That agreement is about the RULE and
/// holds for any control both surfaces see; the control SETS still differ in one
/// documented way -- <see cref="HdfFindingsParser"/> drops a control with a missing/blank
/// <c>id</c> (no identity to key a persisted finding on) while this counter counts every
/// control the report describes, which is <see cref="ControlsTotal"/>'s issue #1132
/// definition. An id-less errored control therefore lands in
/// <see cref="ControlsExecutionError"/> here and in no summary column. Malformed input
/// only, and the same asymmetry <see cref="ControlsTotal"/> already carries; do NOT
/// "fix" it by filtering here without changing <see cref="ControlsTotal"/>'s contract.
/// An errored control is NOT counted in
/// <see cref="CatIOpen"/>/<see cref="CatIIOpen"/>/<see cref="CatIIIOpen"/> (it never
/// produced a genuine compliance verdict, so it is not "open") and not counted in
/// <see cref="ControlsEvaluated"/> either -- exactly as
/// <see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/>
/// (<c>failed</c>-only) treats it on the persisted surface.
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
/// <c>results[].status</c> plus <c>impact</c>, which it hands to
/// <see cref="HdfControlClassifier"/> (a control classified <c>failed</c> counts as one
/// open finding; <c>execution_error</c>, <c>passed</c>, <c>skipped</c>/
/// <c>not_applicable</c>, and a control with no results at all do not). An empty <c>controls</c>
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
/// open counts either.
///
/// <b>Issue #1144</b>: this counter no longer carries a classification rule of its own.
/// It reads each control's <c>results[].status</c> rows and <c>impact</c> and hands
/// them to <see cref="HdfControlClassifier"/> -- the same call
/// <see cref="HdfFindingsParser"/> makes -- then buckets the returned
/// <see cref="Waypoint.Core.Scans.ComponentFindingStatuses"/> value: <c>failed</c> is
/// open (and evaluated), <c>passed</c> is evaluated, <c>execution_error</c> is
/// <see cref="HdfSeverityCounts.ControlsExecutionError"/>, and
/// <c>not_applicable</c>/<c>not_reviewed</c> are present but never evaluated. Before
/// this, the counter matched only the literal string <c>error</c> as an execution error
/// and treated EVERY unrecognized status as open, so a <c>passed</c>+<c>skipped</c>
/// control or an unknown status string read <c>execution_error</c> on the summary while
/// inflating <c>cat_i/ii/iii_open</c> here. There is now one rule and no second copy to
/// drift from -- do not reintroduce a local predicate.
/// </summary>
public static class HdfSeverityCounter
{
	private const string CriticalSeverity = "critical";
	private const string HighSeverity = "high";
	private const string MediumSeverity = "medium";

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

				// Issue #1144 round 2: ONE classification rule, shared verbatim with
				// HdfFindingsParser via HdfControlClassifier -- error beats failed
				// (worst-of), and every shape this reader does not recognize is an
				// execution error rather than an inflated CAT open count.
				switch (HdfControlClassifier.Classify(ResultStatuses(control), Impact(control)))
				{
					case ComponentFindingStatuses.ExecutionError:
						executionError++;
						break;
					case ComponentFindingStatuses.Failed:
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

						break;
					case ComponentFindingStatuses.Passed:
						evaluated++;
						break;
					default:
						// not_applicable / not_reviewed -- present, but never evaluated.
						break;
				}
			}
		}

		return new HdfSeverityCounts(catI, catII, catIII, total, evaluated, executionError);
	}

	/// <summary>
	/// The control's <c>results[].status</c> strings, in document order, for
	/// <see cref="HdfControlClassifier"/>. A missing or non-array <c>results</c>
	/// property yields an EMPTY list -- the same "never ran" shape
	/// <see cref="HdfFindingsParser"/> feeds the classifier, which maps it to
	/// <c>not_reviewed</c> (counted in <see cref="HdfSeverityCounts.ControlsTotal"/>,
	/// never in <see cref="HdfSeverityCounts.ControlsEvaluated"/>). A non-string
	/// <c>status</c> becomes a <c>null</c> entry, which the classifier treats as an
	/// unrecognized shape -- an execution error, never a clean or an open verdict.
	/// </summary>
	private static List<string?> ResultStatuses(JsonElement control)
	{
		if (!control.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		List<string?> statuses = [];
		foreach (JsonElement result in results.EnumerateArray())
		{
			if (result.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			statuses.Add(result.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
				? statusElement.GetString()
				: null);
		}

		return statuses;
	}

	/// <summary>The control's <c>impact</c>, or <c>null</c> when absent/non-numeric -- issue #1124's all-skipped split (impact 0.0 is the profile's own not-applicable decision). Read here only so this surface feeds <see cref="HdfControlClassifier"/> exactly what <see cref="HdfFindingsParser"/> does.</summary>
	private static double? Impact(JsonElement control) =>
		control.TryGetProperty("impact", out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed)
			? parsed
			: null;

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
