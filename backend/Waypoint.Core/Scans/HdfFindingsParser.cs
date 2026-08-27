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

/// <summary>Outcome of a parse attempt -- distinguishes a genuinely parseable (possibly empty) HDF from one that could not be parsed at all (issue #745 AC: "malformed -&gt; actionable rejection, never crash").</summary>
public sealed record HdfParseResult(bool Success, IReadOnlyList<ComponentResultFinding> Findings, string? RejectionReason)
{
	public static HdfParseResult Ok(IReadOnlyList<ComponentResultFinding> findings) => new(true, findings, null);

	public static HdfParseResult Rejected(string reason) => new(false, [], reason);
}

/// <summary>
/// Parses an HDF (Heimdall Data Format) JSON report into <see cref="ComponentResultFinding"/>
/// rows mapped as closely as this narrow reader can to XCCDF identity (issue #745,
/// epic #726 §6). This deliberately reads the same narrow slice of the HDF schema
/// <see cref="HdfSeverityCounter"/> already does (<c>profiles[].controls[]</c>,
/// <c>tags.severity</c>, <c>results[].status</c>) rather than adopting the full
/// MITRE SAF/InSpec schema, plus <c>tags.rid</c>/<c>tags.gid</c> (the XCCDF rule/group
/// id an InSpec-STIG-mapped control carries) and <c>results[].message</c> for a
/// bounded evidence string.
///
/// <b>Untrusted-input discipline</b>: <see cref="Parse(string)"/> and
/// <see cref="Parse(JsonElement)"/> never throw. A missing/malformed document, or a
/// well-formed document that is not HDF-shaped, is an actionable
/// <see cref="HdfParseResult.RejectionReason"/> -- never a crash, and never a
/// fabricated empty success (an empty <c>controls</c> array IS a genuine success with
/// zero findings; a MISSING <c>profiles</c> array, non-string <c>status</c>, or a
/// truncated/invalid document is a rejection). Evidence text is bounded to
/// <see cref="MaxEvidenceLength"/> characters so a runaway InSpec message can never
/// balloon a Postgres row.
///
/// <b>Epic #726 §6 exactly-once rule</b>: a control whose <c>results</c> array is
/// present but EMPTY (InSpec ran the control's resource block but no assertion ever
/// executed -- e.g. the target resource could not be reached) is synthesized as
/// exactly one <see cref="ComponentFindingStatuses.NotReviewed"/> finding rather than
/// silently contributing zero findings. This is the "applicable control that cannot
/// execute remains present exactly once as Not_Reviewed... never omitted or marked
/// Not_Applicable" rule, reconciled with the fact that InSpec's OWN
/// <c>not_applicable</c> impact-zero controls are genuinely <see cref="ComponentFindingStatuses.NotApplicable"/>
/// (a real, intentional skip decided by the profile's own control logic, not a
/// failure to execute) -- the two are told apart by whether <c>results</c> is
/// non-empty: InSpec always emits at least one result row (impact 0 controls
/// included) when the control's `describe` block actually ran, so an empty array is
/// the "never ran" signal, not a fourth spelling of not_applicable.
/// </summary>
public static class HdfFindingsParser
{
	private const int MaxEvidenceLength = 2000;
	private const string CriticalSeverity = "critical";
	private const string HighSeverity = "high";
	private const string MediumSeverity = "medium";

	public static HdfParseResult Parse(string? hdfJson)
	{
		if (string.IsNullOrWhiteSpace(hdfJson))
		{
			return HdfParseResult.Rejected("HDF document is empty or missing.");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(hdfJson);
			return Parse(document.RootElement);
		}
		catch (JsonException ex)
		{
			return HdfParseResult.Rejected($"HDF document is not valid JSON: {ex.Message}");
		}
	}

	public static HdfParseResult Parse(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("profiles", out JsonElement profiles)
			|| profiles.ValueKind != JsonValueKind.Array)
		{
			return HdfParseResult.Rejected("HDF document has no 'profiles' array -- not a recognizable HDF report.");
		}

		List<ComponentResultFinding> findings = [];
		foreach (JsonElement profile in profiles.EnumerateArray())
		{
			if (profile.ValueKind != JsonValueKind.Object
				|| !profile.TryGetProperty("controls", out JsonElement controls)
				|| controls.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (JsonElement control in controls.EnumerateArray())
			{
				if (control.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				ComponentResultFinding? finding = ParseControl(control);
				if (finding is not null)
				{
					findings.Add(finding);
				}
			}
		}

		return HdfParseResult.Ok(findings);
	}

	private static ComponentResultFinding? ParseControl(JsonElement control)
	{
		string? controlId = ReadString(control, "id");
		if (string.IsNullOrWhiteSpace(controlId))
		{
			// A control with no id at all cannot be identified for any downstream
			// view -- skip rather than fabricate an identity. This is the one case
			// this parser silently drops a control, and only because there is no
			// possible identity to attach a row to.
			return null;
		}

		string severity = MapSeverity(ReadTagString(control, "severity"));
		string? ruleId = ReadTagString(control, "rid") ?? ReadTagString(control, "gid");
		string? title = ReadString(control, "title");

		if (!control.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
		{
			// No results array at all is the same "never ran" shape as an empty one.
			return new ComponentResultFinding(controlId, ruleId, title, severity, ComponentFindingStatuses.NotReviewed, "No results reported for this control.");
		}

		List<(string? Status, string? Message)> rows = [];
		foreach (JsonElement result in results.EnumerateArray())
		{
			if (result.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			rows.Add((ReadString(result, "status"), ReadString(result, "code_desc") ?? ReadString(result, "message")));
		}

		if (rows.Count == 0)
		{
			return new ComponentResultFinding(controlId, ruleId, title, severity, ComponentFindingStatuses.NotReviewed, "No results reported for this control.");
		}

		string status = MapStatus(rows);
		string? evidence = BuildEvidence(rows);

		return new ComponentResultFinding(controlId, ruleId, title, severity, status, evidence);
	}

	/// <summary>
	/// A control with ANY failed/errored result is Failed/ExecutionError overall
	/// (worst-of, matching HdfSeverityCounter's own "at least one non-passed,
	/// non-skipped, non-not_applicable result counts as open" rule); otherwise the
	/// first row's own status (passed/skipped/not_applicable) applies -- InSpec never
	/// mixes passed and not_applicable rows for the same control, and a genuinely
	/// mixed or unrecognized status string is treated as ExecutionError rather than
	/// silently defaulting to Passed (never a fabricated clean result).
	/// </summary>
	private static string MapStatus(List<(string? Status, string? Message)> rows)
	{
		bool anyError = rows.Any(r => string.Equals(r.Status, "error", StringComparison.Ordinal));
		if (anyError)
		{
			return ComponentFindingStatuses.ExecutionError;
		}

		bool anyFailed = rows.Any(r => string.Equals(r.Status, "failed", StringComparison.Ordinal));
		if (anyFailed)
		{
			return ComponentFindingStatuses.Failed;
		}

		if (rows.All(r => string.Equals(r.Status, "passed", StringComparison.Ordinal)))
		{
			return ComponentFindingStatuses.Passed;
		}

		if (rows.All(r => string.Equals(r.Status, "skipped", StringComparison.Ordinal)))
		{
			return ComponentFindingStatuses.NotApplicable;
		}

		// Any other shape (unknown status string, or a mix this parser does not
		// specifically recognize) is uncountable-as-clean: report ExecutionError
		// rather than guessing, per the "never a fabricated clean result" rule above.
		return ComponentFindingStatuses.ExecutionError;
	}

	private static string? BuildEvidence(List<(string? Status, string? Message)> rows)
	{
		(string? Status, string? Message) worst = rows.FirstOrDefault(r => string.Equals(r.Status, "error", StringComparison.Ordinal))
			is var errorRow && errorRow.Message is not null
				? errorRow
				: rows.FirstOrDefault(r => string.Equals(r.Status, "failed", StringComparison.Ordinal));

		string? message = worst.Message ?? rows[0].Message;
		if (string.IsNullOrWhiteSpace(message))
		{
			return null;
		}

		return message.Length > MaxEvidenceLength ? string.Concat(message.AsSpan(0, MaxEvidenceLength), "...(truncated)") : message;
	}

	private static string MapSeverity(string? rawSeverity) => rawSeverity?.ToLowerInvariant() switch
	{
		CriticalSeverity or HighSeverity => ComponentFindingSeverities.CatI,
		MediumSeverity => ComponentFindingSeverities.CatII,
		_ => ComponentFindingSeverities.CatIII,
	};

	private static string? ReadString(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string? ReadTagString(JsonElement control, string propertyName) =>
		control.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Object
			? ReadString(tags, propertyName)
			: null;
}
