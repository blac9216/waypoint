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
/// schedules for read-only job types only.
///
/// RBAC (issue #517 review): a schedule is a deferred, recurring instance of a direct
/// action, so its write floor MUST equal the role that action's own endpoint requires --
/// otherwise the scheduling surface becomes a privilege-escalation path. The four
/// schedulable job types are NOT uniformly Cyber-gated: <c>scan</c> is Cyber (matching
/// <c>RunsController.CreateRun</c>'s scan floor), but <c>discover</c>,
/// <c>credential-test</c>, and <c>catalog-index</c> are Admin at their direct endpoints
/// (<c>POST /targets/{id}/discover</c>, <c>POST /credentials/{id}/test</c>,
/// <c>POST /catalog/sync</c> -- all <c>[RequireAdminRole]</c>), consistent with the
/// domain-model Roles table scoping Cyber to "no config, credentials, downloads, or
/// remediation" and catalog/download management to Operator+/Admin. The
/// <c>[RequireCyberRole]</c> attribute on each write action is only the COARSE floor
/// (the lowest schedulable role, <c>scan</c>); the per-job_type floor is enforced
/// imperatively via <see cref="ScheduleJobTypes.RequiredRole"/> in
/// <see cref="EnsureRoleFor"/>, which is the single data-driven source of truth. Create
/// checks the requested type; update and delete re-check the EXISTING schedule's type
/// (the update contract carries no <c>job_type</c>, so a type is immutable once created,
/// but the existing type still governs -- a Cyber user must not modify, pause/resume, or
/// delete an Admin's <c>discover</c>/<c>credential-test</c>/<c>catalog-index</c> schedule).
/// Reads are Viewer+, matching every other list/get endpoint in this codebase.
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
		EnsureRoleFor(request.JobType);

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

		// The update contract carries no job_type (a schedule's type is immutable), so the
		// existing type governs. This gates pause/resume too -- both flow through PUT's
		// `enabled` field -- so a Cyber user cannot pause/resume an Admin's Admin-typed schedule.
		EnsureRoleFor(existing.JobType);

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
		// Re-check the caller's role against the existing schedule's job_type before
		// deleting: a Cyber user must not delete an Admin's Admin-typed schedule (same
		// escalation hole the create/update paths close).
		Schedule? existing = await _schedules.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			throw NotFoundError(id);
		}

		EnsureRoleFor(existing.JobType);

		return await _schedules.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? NoContent() : throw NotFoundError(id);
	}

	/// <summary>
	/// Enforces the per-job_type write floor from <see cref="ScheduleJobTypes.RequiredRole"/>
	/// against the caller, refining the coarse <c>[RequireCyberRole]</c> attribute. Throws
	/// 403 <c>forbidden</c> when the caller is below the type's floor -- e.g. a Cyber user
	/// touching a <c>discover</c>/<c>credential-test</c>/<c>catalog-index</c> schedule they
	/// could not trigger directly. Assumes <paramref name="jobType"/> has already passed
	/// <see cref="ValidateJobType"/>.
	/// </summary>
	private void EnsureRoleFor(string jobType)
	{
		WaypointRole required = ScheduleJobTypes.RequiredRole(jobType);
		if (!User.HasRoleAtLeast(required))
		{
			throw ApiException.Forbidden(
				$"Scheduling a '{jobType}' job requires the {required} role.",
				$"The '{jobType}' job type is gated to {required}+ at its direct endpoint, so its schedules carry the same floor (docs/domain-model.md Roles).");
		}
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
