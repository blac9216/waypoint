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

using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Job-level (as opposed to run-level) endpoints -- currently just the per-job cancel
/// (issue #291), a thin wrapper over <see cref="IJobQueueRepository.CancelJobAsync"/>
/// (#277). <c>GET /jobs/{id}/artifacts/{kind}</c> is a documented future addition
/// (docs/api-contract.md) not yet implemented; this controller is where it belongs
/// once it lands.
/// </summary>
[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController : ControllerBase
{
	private readonly IJobQueueRepository _repository;

	public JobsController(IJobQueueRepository repository)
	{
		ArgumentNullException.ThrowIfNull(repository);
		_repository = repository;
	}

	/// <summary>
	/// Cancels a single job, independent of its run's other jobs -- the same primitive
	/// <c>DownloadsController.CancelDownload</c> uses (#10/#277). Operator+ per
	/// docs/api-contract.md's pause/resume/abort gate: job-level cancel is the same
	/// tier as the run-level controls it sits alongside on the Live Run screen. Unlike
	/// pause/resume/abort there is no ownership check here -- a job row does not carry
	/// its run's <c>initiated_by</c> without an extra lookup, and #277's only existing
	/// caller (downloads) is already Admin-gated one level up; this endpoint is the
	/// first to expose the primitive directly; ownership scoping is deferred (issue
	/// TBD, see PR notes) rather than assumed here.
	/// </summary>
	[HttpDelete("{id:guid}")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(JobCancelResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<JobCancelResponse>> CancelJob(Guid id, CancellationToken cancellationToken)
	{
		JobCancelOutcome outcome = await _repository.CancelJobAsync(id, cancellationToken).ConfigureAwait(false);

		switch (outcome)
		{
			case JobCancelOutcome.NotFound:
				throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
			case JobCancelOutcome.NotCancellable:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "not_cancellable",
					"Job cannot be cancelled.", $"Job '{id}' is already in a terminal state.");
			case JobCancelOutcome.CancelRequested:
				return Ok(new JobCancelResponse(id.ToString(), "cancel_requested"));
			case JobCancelOutcome.Cancelled:
			default:
				return Ok(new JobCancelResponse(id.ToString(), "cancelled"));
		}
	}
}
