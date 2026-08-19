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

using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Audit;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/audit` surface (issue #512, epic #14): Cyber+ read-only query
/// over the append-only <c>audit_log</c> table (migration 0001/0020) every writer in
/// this codebase already inserts into (<c>CredentialSecretStore</c>: <c>secret.decrypted</c>;
/// <c>JobQueueRepository</c>: <c>job.retried</c>, <c>credential.swapped</c>, run
/// initiations; <c>CredentialRepository</c>: <c>credential.deleted</c>). Filterable by
/// kind (<c>event_type</c>)/actor/time window, stable-ordered
/// (<c>occurred_at DESC, id DESC</c>) and paged like every other list endpoint
/// (<c>?limit/offset</c> + <c>X-Total-Count</c>), plus a CSV export of the exact same
/// filtered set via <c>?format=csv</c> -- domain-model's "Cyber ... full audit history"
/// capability.
/// </summary>
[ApiController]
[Route("api/v1/audit")]
public sealed class AuditController : ControllerBase
{
	private readonly IAuditRepository _audit;

	public AuditController(IAuditRepository audit)
	{
		ArgumentNullException.ThrowIfNull(audit);
		_audit = audit;
	}

	/// <summary>
	/// Returns JSON by default; <c>?format=csv</c> streams the identical filtered,
	/// stably-ordered set as <c>text/csv</c> instead (docs/api-contract.md `/audit`:
	/// "CSV export of the current filter") -- one query surface, two representations,
	/// so the export can never drift from what the caller was just looking at.
	/// </summary>
	[HttpGet]
	[RequireCyberRole]
	[ProducesResponseType(typeof(AuditEntryResponse[]), StatusCodes.Status200OK)]
	public async Task<IActionResult> List(
		[FromQuery(Name = "kind")] string? eventType,
		[FromQuery] string? actor,
		[FromQuery(Name = "from")] string? from,
		[FromQuery(Name = "to")] string? to,
		[FromQuery] string? format,
		[FromQuery] PageRequest page,
		CancellationToken cancellationToken)
	{
		AuditQuery query = new(eventType, actor, ParseTimestamp(from, "from"), ParseTimestamp(to, "to"));

		bool asCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
		int limit = asCsv ? int.MaxValue : page.Limit;
		int offset = asCsv ? 0 : page.Offset;

		AuditListResult result = await _audit.ListAsync(query, limit, offset, cancellationToken).ConfigureAwait(false);

		if (asCsv)
		{
			return File(BuildCsv(result.Items), "text/csv", "audit-log.csv");
		}

		Response.Headers["X-Total-Count"] = result.TotalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(result.Items.Select(MapEntry).ToArray());
	}

	private static DateTimeOffset? ParseTimestamp(string? value, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
		{
			throw ApiException.Validation(
				$"{parameterName} is not a valid timestamp.",
				$"\"{parameterName}\" must be an ISO-8601 timestamp, e.g. 2026-08-19T00:00:00Z.");
		}

		return parsed;
	}

	private static AuditEntryResponse MapEntry(AuditEntry entry)
	{
		return new AuditEntryResponse(
			Id: entry.Id.ToString(),
			EventType: entry.EventType,
			Actor: entry.Actor,
			CredentialId: entry.CredentialId?.ToString(),
			JobId: entry.JobId?.ToString(),
			RunId: entry.RunId?.ToString(),
			Detail: entry.DetailJson,
			OccurredAt: entry.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// RFC 4180-ish minimal CSV. Every user-influenceable field (<c>event_type</c>,
	/// <c>actor</c>, <c>detail</c>) is run through <see cref="CsvField"/>, which both
	/// neutralizes spreadsheet formula injection (CWE-1236) and applies RFC-4180
	/// quoting. GUID/timestamp columns are machine-generated and written directly.
	/// </summary>
	private static byte[] BuildCsv(IReadOnlyList<AuditEntry> entries)
	{
		StringBuilder builder = new();
		builder.AppendLine("id,event_type,actor,credential_id,job_id,run_id,detail,occurred_at");

		foreach (AuditEntry entry in entries)
		{
			builder.Append(entry.Id).Append(',')
				.Append(CsvField(entry.EventType)).Append(',')
				.Append(CsvField(entry.Actor)).Append(',')
				.Append(entry.CredentialId).Append(',')
				.Append(entry.JobId).Append(',')
				.Append(entry.RunId).Append(',')
				.Append(CsvField(entry.DetailJson)).Append(',')
				.Append(entry.OccurredAt.ToString("O", CultureInfo.InvariantCulture))
				.Append('\n');
		}

		return Encoding.UTF8.GetBytes(builder.ToString());
	}

	/// <summary>
	/// Renders a single user-derived cell safe for CSV export. First neutralizes
	/// spreadsheet formula injection (CWE-1236) by prefixing a single quote when the
	/// value leads with a formula trigger (<c>= + - @</c>, tab, or CR), so the cell is
	/// treated as text rather than executed as a formula in Excel/LibreOffice; then
	/// applies RFC-4180 quoting for embedded commas/quotes/newlines. All CSV exports
	/// route user-influenceable columns through this one helper so they inherit the
	/// guard.
	/// </summary>
	private static string CsvField(string value)
	{
		if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
		{
			value = "'" + value;
		}

		bool needsQuoting = value.Contains(',', StringComparison.Ordinal)
			|| value.Contains('"', StringComparison.Ordinal)
			|| value.Contains('\n', StringComparison.Ordinal);
		return needsQuoting ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
	}
}
