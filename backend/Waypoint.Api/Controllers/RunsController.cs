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

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md runs &amp; jobs surface: create runs, inspect run state and
/// per-job detail, and pause/resume/abort.
/// </summary>
[ApiController]
[Route("api/v1/runs")]
public sealed class RunsController : ControllerBase
{
	private const string RemediateRunType = "remediate";
	private const string RemediateConfirmation = "REMEDIATE";

	private readonly IJobQueueRepository _repository;

	public RunsController(IJobQueueRepository repository)
	{
		ArgumentNullException.ThrowIfNull(repository);
		_repository = repository;
	}

	/// <summary>
	/// In-action role check for gates finer than the endpoint's <c>Require*Role</c>
	/// attribute (which fixes the floor, not per-run-type requirements).
	/// </summary>
	private bool CallerHasAtLeast(WaypointRole minimum) =>
		Enum.TryParse(User.FindFirstValue(WaypointClaimTypes.Role), out WaypointRole role) && role >= minimum;

	/// <summary>
	/// Create a new run. Cyber+ for scan runs; remediation requires Admin plus the
	/// explicit <c>confirmation: "REMEDIATE"</c> body field — remediation is never
	/// implicit (docs/api-contract.md `/runs`, CLAUDE.md key constraints). The
	/// initiator is recorded from the authenticated identity, never from the body.
	/// </summary>
	[HttpPost]
	[RequireCyberRole]
	[ProducesResponseType(typeof(RunCreatedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<RunCreatedResponse>> CreateRun(
		RunCreateRequest request,
		CancellationToken cancellationToken)
	{
		if (string.Equals(request.RunType, RemediateRunType, StringComparison.Ordinal))
		{
			if (!CallerHasAtLeast(WaypointRole.Admin))
			{
				throw ApiException.Forbidden(
					"Remediation runs require the Admin role.",
					"Only an Admin may create a run with run_type 'remediate'.");
			}

			if (!string.Equals(request.Confirmation, RemediateConfirmation, StringComparison.Ordinal))
			{
				throw ApiException.Validation(
					"Remediation requires explicit confirmation.",
					$"Set \"confirmation\": \"{RemediateConfirmation}\" in the request body to create a remediation run.");
			}
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";
		Guid runId = await _repository.CreateRunAsync(
			request.RunType, request.Scope, request.CredentialId, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

		return Accepted(new RunCreatedResponse(runId.ToString()));
	}

	/// <summary>
	/// Get run detail with job counts. Viewer+ — any authenticated user can inspect
	/// runs.
	/// </summary>
	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunResponse>> GetRun(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		return Ok(MapRun(run));
	}

	/// <summary>
	/// List all jobs belonging to a run. Viewer+.
	/// </summary>
	[HttpGet("{id:guid}/jobs")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(JobResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<JobResponse>>> GetJobs(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(jobs.Select(MapJob).ToArray());
	}

	/// <summary>
	/// Pause dispatch for a run. Operator+.
	/// </summary>
	[HttpPost("{id:guid}/pause")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> PauseRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		bool paused = await _repository.PauseRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (!paused)
		{
			throw ApiException.Validation("Run cannot be paused.", $"Run '{id}' is in state '{state.State}'.");
		}

		// Re-fetch state after the action to return the post-action state.
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), newState?.State ?? "paused"));
	}

	/// <summary>
	/// Resume dispatch for a paused run. Operator+.
	/// </summary>
	[HttpPost("{id:guid}/resume")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> ResumeRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		bool resumed = await _repository.ResumeRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (!resumed)
		{
			throw ApiException.Validation("Run cannot be resumed.", $"Run '{id}' is in state '{state.State}'.");
		}

		// Re-fetch state after the action to return the post-action state.
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), newState?.State ?? "running"));
	}

	/// <summary>
	/// Abort a run. Operator+.
	/// </summary>
	[HttpPost("{id:guid}/abort")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> AbortRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		AbortRunResult result = await _repository.AbortRunAsync(id, cancellationToken).ConfigureAwait(false);
		_ = result; // AbortRunResult carries cancelled/in-flight job IDs for future enrichment
		return Ok(new RunActionResponse(id.ToString(), "aborted"));
	}

	// -- mapping helpers ---------------------------------------------------

	private static RunResponse MapRun(RunSummary run)
	{
		return new RunResponse(
			Id: run.Id.ToString(),
			RunType: run.RunType,
			State: run.State,
			Paused: run.Paused,
			Blocked: run.Blocked,
			BlockedReason: run.BlockedReason,
			Scope: run.ScopeJson,
			CredentialId: run.CredentialId?.ToString(),
			InitiatedBy: run.InitiatedBy,
			CreatedAt: run.CreatedAt!,
			StartedAt: run.StartedAt,
			CompletedAt: run.CompletedAt,
			JobCount: run.JobCount,
			JobCountQueued: run.JobCountQueued,
			JobCountRunning: run.JobCountRunning,
			JobCountCompleted: run.JobCountCompleted,
			JobCountFailed: run.JobCountFailed,
			JobCountBlocked: run.JobCountBlocked);
	}

	private static JobResponse MapJob(JobSummary job)
	{
		return new JobResponse(
			Id: job.Id.ToString(),
			RunId: job.RunId?.ToString(),
			JobType: job.JobType,
			TargetId: job.TargetId,
			TargetName: job.TargetName,
			State: job.State,
			Stage: job.Stage,
			Priority: job.Priority,
			AttemptCount: job.AttemptCount,
			CreatedAt: job.CreatedAt!,
			StartedAt: job.StartedAt,
			FinishedAt: job.FinishedAt);
	}
}
