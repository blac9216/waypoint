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

/// <summary>CAT I/II/III open (failed) finding counts for one HDF report -- <c>GET /runs/{id}/artifacts</c> (issue #299).</summary>
public sealed record HdfSeverityCounts(int CatIOpen, int CatIIOpen, int CatIIIOpen)
{
	public static readonly HdfSeverityCounts Zero = new(0, 0, 0);
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
/// </summary>
public static class HdfSeverityCounter
{
	private const string CriticalSeverity = "critical";
	private const string HighSeverity = "high";
	private const string MediumSeverity = "medium";
	private const string PassedStatus = "passed";
	private const string SkippedStatus = "skipped";
	private const string NotApplicableStatus = "not_applicable";

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
		int catI = 0, catII = 0, catIII = 0;

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
				if (!IsOpen(control))
				{
					continue;
				}

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
		}

		return new HdfSeverityCounts(catI, catII, catIII);
	}

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
			if (status is not (PassedStatus or SkippedStatus or NotApplicableStatus) && status is not null)
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
