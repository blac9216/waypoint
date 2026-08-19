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
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Scheduling;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/schedules` surface (issue #31, epic #14): cron-style
/// schedules for read-only job types only. Role mapping (docs/domain-model.md Roles
/// table): scans are "read-only in effect", and Cyber is the role the table already
/// grants "initiate scans (using the target's assigned service credential)" to -- a
/// schedule is exactly a deferred, recurring instance of the same read-only initiation,
/// always under a stored/service credential (docs/domain-model.md Scheduling: "execute
/// under the target's service credential"; ADR-0011's ad hoc personal-credential tier
/// is scan-run-only and explicitly excluded from scheduling). So every schedule
/// mutation here (create/update/delete) is <see cref="RequireCyberRoleAttribute"/>,
/// matching <c>RunsController.CreateRun</c>'s floor for a <c>scan</c> run type, not
/// <c>RequireOperatorRoleAttribute</c> -- Operator's extra grant over Cyber is ad hoc
/// personal credentials and download/catalog management, neither of which applies to a
/// scheduled, service-credential-only, read-only job. Reads are Viewer+, matching every
/// other list/get endpoint in this codebase.
///
/// The server-side read-only rejection this controller performs
/// (<see cref="ValidateJobType"/>) is the create-side half of a guarantee also enforced
/// claim-side by the runner allowlists (migration 0024,
/// <c>JobQueueRepositoryAllowlistClaimTests</c>) -- see issue #31's "Validation notes"
/// for why both checks exist rather than relying on either alone.
/// </summary>
[ApiController]
[Route("api/v1/schedules")]
public sealed class SchedulesController : ControllerBase
{
	private readonly IScheduleRepository _schedules;

	public SchedulesController(IScheduleRepository schedules)
	{
		ArgumentNullException.ThrowIfNull(schedules);
		_schedules = schedules;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ScheduleResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ScheduleResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Schedule> schedules = await _schedules.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(schedules.Select(MapSchedule).ToArray());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ScheduleResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ScheduleResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Schedule? schedule = await _schedules.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return schedule is null ? throw NotFoundError(id) : Ok(MapSchedule(schedule));
	}

	/// <summary>
	/// Creates a schedule. Rejects any <paramref name="request"/> whose
	/// <c>job_type</c> is outside <see cref="ScheduleJobTypes.All"/> with a clear
	/// <c>validation_failed</c> error -- "server-rejects `remediate` etc. — domain rule"
	/// (docs/api-contract.md), enforced here BY DESIGN (a fixed closed set,
	/// <see cref="ScheduleJobTypes"/>), not by configuration, matching
	/// <c>schedules_job_type_check</c>'s equally fixed CHECK constraint.
	/// </summary>
	[HttpPost]
	[RequireCyberRole]
	[ProducesResponseType(typeof(ScheduleResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<ScheduleResponse>> Create([FromBody] ScheduleCreateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			throw ApiException.Validation("name is required.", "Set a non-empty \"name\" in the request body.");
		}

		ValidateJobType(request.JobType);

		CronExpression cron = ParseCron(request.CronExpression);
		DateTimeOffset nextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow);

		string scopeJson = string.IsNullOrWhiteSpace(request.Scope) ? "{}" : request.Scope;
		string createdBy = User.GetRequiredUsername();

		Guid? id = await _schedules.CreateAsync(
			request.Name, request.JobType, request.CronExpression, scopeJson, request.CredentialId,
			nextRunAt, createdBy, cancellationToken).ConfigureAwait(false);

		if (id is not Guid createdId)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A schedule named '{request.Name}' already exists.");
		}

		Schedule created = (await _schedules.GetAsync(createdId, cancellationToken).ConfigureAwait(false))!;
		return CreatedAtAction(nameof(Get), new { id = createdId }, MapSchedule(created));
	}

	[HttpPut("{id:guid}")]
	[RequireCyberRole]
	[ProducesResponseType(typeof(ScheduleResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ScheduleResponse>> Update(Guid id, [FromBody] ScheduleUpdateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		Schedule? existing = await _schedules.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			throw NotFoundError(id);
		}

		DateTimeOffset? nextRunAt = null;
		if (!string.IsNullOrWhiteSpace(request.CronExpression))
		{
			CronExpression cron = ParseCron(request.CronExpression);
			nextRunAt = cron.GetNextOccurrence(DateTimeOffset.UtcNow);
		}

		ScheduleWriteOutcome outcome = await _schedules.UpdateAsync(
			id,
			string.IsNullOrWhiteSpace(request.Name) ? null : request.Name,
			string.IsNullOrWhiteSpace(request.CronExpression) ? null : request.CronExpression,
			string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope,
			request.CredentialId,
			request.ClearCredential,
			request.Enabled,
			nextRunAt,
			cancellationToken).ConfigureAwait(false);

		switch (outcome)
		{
			case ScheduleWriteOutcome.NotFound:
				throw NotFoundError(id);
			case ScheduleWriteOutcome.NameTaken:
				throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A schedule named '{request.Name}' already exists.");
		}

		Schedule updated = (await _schedules.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(MapSchedule(updated));
	}

	[HttpDelete("{id:guid}")]
	[RequireCyberRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		return await _schedules.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? NoContent() : throw NotFoundError(id);
	}

	private static void ValidateJobType(string jobType)
	{
		if (string.IsNullOrWhiteSpace(jobType) || !ScheduleJobTypes.IsValid(jobType))
		{
			throw new ApiException(
				HttpStatusCode.BadRequest, "unsupported_job_type",
				"job_type is not schedulable.",
				$"\"job_type\" must be one of: {string.Join(", ", ScheduleJobTypes.All)}. Remediation, downloads, bundle import/apply, and updates are excluded from scheduling by design (docs/domain-model.md Scheduling).");
		}
	}

	private static CronExpression ParseCron(string cronExpression)
	{
		try
		{
			return CronExpression.Parse(cronExpression);
		}
		catch (FormatException exception)
		{
			throw ApiException.Validation("cron_expression is not valid.", exception.Message);
		}
	}

	private static ScheduleResponse MapSchedule(Schedule schedule)
	{
		return new ScheduleResponse(
			Id: schedule.Id.ToString(),
			Name: schedule.Name,
			JobType: schedule.JobType,
			CronExpression: schedule.CronExpression,
			Scope: schedule.ScopeJson,
			CredentialId: schedule.CredentialId?.ToString(),
			Enabled: schedule.Enabled,
			PausedReason: schedule.PausedReason,
			NextRunAt: schedule.NextRunAt?.ToString("O", CultureInfo.InvariantCulture),
			LastRunAt: schedule.LastRunAt?.ToString("O", CultureInfo.InvariantCulture),
			LastRunId: schedule.LastRunId?.ToString(),
			LastResult: schedule.LastResult,
			CreatedBy: schedule.CreatedBy,
			CreatedAt: schedule.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
			UpdatedAt: schedule.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No schedule exists with id '{id}'.");
}
