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
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Job-level (as opposed to run-level) endpoints: the per-job cancel (issue #291), a thin
/// wrapper over <see cref="IJobQueueRepository.CancelJobAsync"/> (#277), and the per-kind
/// artifact download (issue #299).
/// </summary>
[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController : ControllerBase
{
	/// <summary>
	/// The closed set of artifact kinds this route serves (docs/api-contract.md
	/// `/jobs/{id}/artifacts/{kind}`). <c>kind</c> is validated against this set BEFORE it
	/// ever reaches a file path -- it is never interpolated into
	/// <see cref="ScanArtifactPaths"/> as a raw user-supplied path segment, only used to
	/// pick which of the two fixed, server-computed paths to serve.
	/// </summary>
	private static readonly Dictionary<string, string> ContentTypesByKind = new(StringComparer.Ordinal)
	{
		["hdf"] = "application/json",
		["ckl"] = "application/xml",
	};

	private readonly IJobQueueRepository _repository;
	private readonly IOptions<ScanOptions> _scanOptions;

	public JobsController(IJobQueueRepository repository, IOptions<ScanOptions> scanOptions)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(scanOptions);
		_repository = repository;
		_scanOptions = scanOptions;
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

	/// <summary>
	/// Streams one artifact file for a job (docs/api-contract.md `/jobs/{id}/artifacts/{kind}`:
	/// "CKL/HDF download"). Viewer+, matching every other run/job read. <paramref name="kind"/>
	/// is validated against the closed <see cref="ContentTypesByKind"/> set and used only to
	/// select which fixed, server-computed path (<see cref="ScanArtifactPaths"/>) to serve --
	/// never concatenated into a path itself, so there is no path-traversal surface here
	/// regardless of what the caller supplies. 404 covers both "job does not exist" and "the
	/// job exists but this artifact has not been produced yet" (e.g. requesting <c>ckl</c>
	/// before the convert stage has run) -- the two are indistinguishable to the caller by
	/// design, matching how every other not-found resource in this API responds.
	/// </summary>
	[HttpGet("{id:guid}/artifacts/{kind}")]
	[RequireViewerRole]
	[ProducesResponseType(StatusCodes.Status200OK)]
	public async Task<IActionResult> GetArtifact(Guid id, string kind, CancellationToken cancellationToken)
	{
		if (!ContentTypesByKind.TryGetValue(kind, out string? contentType))
		{
			throw ApiException.Validation(
				"kind is not a supported artifact type.",
				$"'{kind}' is not supported. Expected one of: {string.Join(", ", ContentTypesByKind.Keys)}.");
		}

		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		string artifactStorePath = _scanOptions.Value.ArtifactStorePath;
		string? path = kind switch
		{
			"hdf" => ScanArtifactPaths.ResolveHdf(artifactStorePath, id),
			"ckl" => ScanArtifactPaths.Ckl(artifactStorePath, id),
			_ => null,
		};

		if (path is null || !System.IO.File.Exists(path))
		{
			throw ApiException.NotFound(
				"Artifact not found.",
				$"Job '{id}' has no '{kind}' artifact yet.");
		}

		byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		return File(bytes, contentType, Path.GetFileName(path));
	}
}
