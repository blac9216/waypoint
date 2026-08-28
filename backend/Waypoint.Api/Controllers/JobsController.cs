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

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.Scans;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Job-level (as opposed to run-level) endpoints: the per-job cancel (issue #291), a thin
/// wrapper over <see cref="IJobControlRepository.CancelJobAsync"/> (#277), the per-kind
/// artifact download (issue #299), and the STIG Manager upload retry (issue #311, the
/// shape issue #297 documents but leaves unbuilt -- reused narrowly here for exactly
/// this one action rather than a general job-retry route).
/// </summary>
[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController : ControllerBase
{
	private const string ScanJobType = "scan";

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

	/// <summary>Bounds for the findings page-size query param (issue #745) -- same clamp shape as every other limit/offset reader in this API.</summary>
	private const int MinFindingsLimit = 1;
	private const int MaxFindingsLimit = 500;
	private const int DefaultFindingsLimit = 100;

	private readonly IJobControlRepository _repository;
	private readonly IJobRunnerRepository _jobRunnerRepository;
	private readonly IComponentResultRepository _componentResults;
	private readonly IOptions<ScanOptions> _scanOptions;
	private readonly TargetRepository _targets;
	private readonly ScanUploadCoordinator _upload;

	public JobsController(
		IJobControlRepository repository,
		IJobRunnerRepository jobRunnerRepository,
		IComponentResultRepository componentResults,
		IOptions<ScanOptions> scanOptions,
		TargetRepository targets,
		ScanUploadCoordinator upload)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(jobRunnerRepository);
		ArgumentNullException.ThrowIfNull(componentResults);
		ArgumentNullException.ThrowIfNull(scanOptions);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(upload);
		_repository = repository;
		_jobRunnerRepository = jobRunnerRepository;
		_componentResults = componentResults;
		_scanOptions = scanOptions;
		_targets = targets;
		_upload = upload;
	}

	/// <summary>
	/// Cancels a single job, independent of its run's other jobs -- the same primitive
	/// <c>DownloadsController.CancelDownload</c> uses (#10/#277). Cyber+ per
	/// docs/api-contract.md's role matrix ("Control (pause/resume/abort/cancel/retry/
	/// repair-credential) a scan the caller initiated" -- PR #819's reconciliation,
	/// carried forward by issue #757's "Cyber controls owned live scans" owner
	/// decision): job-level cancel is the same tier as the run-level controls it sits
	/// alongside on the Live Run screen, and (issue #294) the same "own runs, Admin
	/// any" ownership scope --
	/// <see cref="RunsController.EnforceRunOwnership(System.Security.Claims.ClaimsPrincipal, RunQueueState)"/>
	/// applied to the run owning this job, resolved via <see cref="IJobControlRepository.GetJobAsync"/>
	/// -&gt; <see cref="JobSummary.RunId"/> -&gt; <see cref="IJobControlRepository.GetRunQueueStateAsync"/>.
	/// The ownership check runs before the cancel attempt, so a non-owning Cyber/
	/// Operator caller's call never reaches <see cref="IJobControlRepository.CancelJobAsync"/>.
	/// A job with no run (should not occur in practice; every job is fanned out from a
	/// run) is treated as ownerless -- Admin-only, same as a run with no recorded
	/// initiator.
	/// </summary>
	[HttpDelete("{id:guid}")]
	[RequireCyberRole]
	[ProducesResponseType(typeof(JobCancelResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<JobCancelResponse>> CancelJob(Guid id, CancellationToken cancellationToken)
	{
		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		RunQueueState? runState = job.RunId is { } runId
			? await _repository.GetRunQueueStateAsync(runId, cancellationToken).ConfigureAwait(false)
			: null;
		RunsController.EnforceRunOwnership(User, runState ?? new RunQueueState("unknown", false, false, null, InitiatedBy: null));

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

	/// <summary>
	/// Retries a <c>scan</c> job's STIG Manager upload (issue #311; un-stubs the Results
	/// screen's "Retry failed uploads" button, issue #300). Unlike issue #297's proposed
	/// general job-retry endpoint (which would move a <c>failed</c> job's <c>state</c>
	/// back to <c>queued</c> and re-run a whole pipeline stage), this retries only the
	/// post-convert upload action -- <c>state</c>/<c>stage</c> are untouched, because the
	/// job itself already reached its terminal (<c>uploaded</c>/<c>done</c>) or failed
	/// for reasons unrelated to the upload. Requires the CKL artifact to still exist on
	/// disk (a job whose convert stage never ran, or whose artifact was pruned, has
	/// nothing to retry). Never throws through to a 500 for an ordinary upload failure --
	/// same "never fail the caller" contract as the original attempt; the response
	/// simply reports the fresh outcome, including a repeat failure.
	/// </summary>
	[HttpPost("{id:guid}/stigman-upload-retry")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(JobUploadRetryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<JobUploadRetryResponse>> RetryUpload(Guid id, CancellationToken cancellationToken)
	{
		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		if (!string.Equals(job.JobType, ScanJobType, StringComparison.Ordinal))
		{
			throw ApiException.Validation("Only scan jobs have a STIG Manager upload to retry.", $"Job '{id}' is job_type '{job.JobType}'.");
		}

		if (job.TargetId is not { } targetIdText || !Guid.TryParse(targetIdText, out Guid targetId))
		{
			throw ApiException.Validation("Job has no target to resolve a STIG Manager connection for.", $"Job '{id}' has no target_id.");
		}

		Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			throw ApiException.NotFound("Target not found.", $"Target '{targetId}' no longer exists.");
		}

		string cklPath = ScanArtifactPaths.Ckl(_scanOptions.Value.ArtifactStorePath, id);
		if (!System.IO.File.Exists(cklPath))
		{
			throw new ApiException(
				System.Net.HttpStatusCode.Conflict, "no_ckl_artifact",
				"No CKL artifact exists for this job to retry uploading.", $"Job '{id}' has no CKL at the expected artifact path.");
		}

		StigManagerUploadResult result = await _upload.UploadAsync(id, target, cklPath, cancellationToken).ConfigureAwait(false);
		string status = result.Outcome switch
		{
			StigManagerUploadOutcome.Uploaded => JobUploadStatuses.Uploaded,
			StigManagerUploadOutcome.Conflict => JobUploadStatuses.Conflict,
			_ => JobUploadStatuses.Failed,
		};
		return Ok(new JobUploadRetryResponse(id.ToString(), status, result.Detail));
	}

	/// <summary>
	/// Issue #744 remainder: the append-only STIG Manager upload-attempt history for one
	/// job (migration 0062's <c>upload_attempts</c>, written by
	/// <see cref="Waypoint.Infrastructure.Scans.ScanUploadCoordinator"/> for both the
	/// first convert-stage upload and every later <see cref="RetryUpload"/> call).
	/// Viewer+, matching every other run/job read in this API. Oldest-first, mirroring
	/// <see cref="IJobRunnerRepository.GetUploadAttemptsAsync"/> -- the Results screen's
	/// attempt-history drill-down renders these directly without re-sorting. A job with
	/// no recorded attempts (never uploaded, or a non-scan job) returns an empty list,
	/// not a 404 -- the job itself existing is the only precondition, matching
	/// <see cref="GetComponentResultsSummary"/>'s "resource exists, evidence may not
	/// yet" convention on <see cref="RunsController"/>.
	/// </summary>
	[HttpGet("{id:guid}/upload-attempts")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(UploadAttemptResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<UploadAttemptResponse>>> GetUploadAttempts(Guid id, CancellationToken cancellationToken)
	{
		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		IReadOnlyList<UploadAttemptRecord> attempts = await _jobRunnerRepository.GetUploadAttemptsAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(attempts.Select(a => new UploadAttemptResponse(
			AttemptNumber: a.AttemptNumber,
			Endpoint: a.Endpoint,
			Collection: a.Collection,
			Status: a.Status,
			ErrorDetail: a.ErrorDetail,
			AttemptedAt: a.AttemptedAt)).ToArray());
	}

	/// <summary>
	/// Issue #745 remainder: the per-component finding list for a job's LATEST
	/// <c>component_results</c> attempt (migration 0063). Viewer+, matching every other
	/// run/job read. Statuses pass through exactly as recorded -- epic #726 §6's
	/// "failed, skipped, excluded, not-applicable, open, and passed states are not
	/// conflated" and the exactly-once <c>Not_Reviewed</c> rule hold because this
	/// endpoint performs no re-derivation, it only reads back what
	/// <c>HdfFindingsParser</c>/<c>ComponentResultRecordingService</c> already wrote.
	/// Limit/offset paged (<paramref name="limit"/> 1-500, default 100; <paramref name="offset"/>
	/// &gt;= 0) -- one attempt's finding count is bounded by one benchmark's control
	/// count, not an unboundedly growing history, so this follows `GET /runs`'s
	/// bounded-list idiom rather than `/runs/{id}/events/history`'s cursor. A job that
	/// exists but has no recorded component-result attempt at all (not yet claimed,
	/// legacy non-component job, or its evidence was purged) returns
	/// <c>items: []</c>/<c>X-Total-Count: 0</c> with null attempt fields --
	/// honest-empty, never a 404 -- because only the job's own existence is this
	/// endpoint's precondition, matching <see cref="GetUploadAttempts"/> and
	/// <see cref="RunsController.GetComponentResultsSummary"/>. The total matching-row
	/// count travels in the <c>X-Total-Count</c> response header per docs/api-contract.md
	/// Conventions (the <see cref="RunsController.ListRuns"/> precedent) -- never in
	/// the body.
	/// </summary>
	[HttpGet("{id:guid}/component-results/findings")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComponentResultFindingsResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ComponentResultFindingsResponse>> GetComponentResultFindings(
		Guid id, [FromQuery] int limit = DefaultFindingsLimit, [FromQuery] int offset = 0, CancellationToken cancellationToken = default)
	{
		if (limit < MinFindingsLimit || limit > MaxFindingsLimit)
		{
			throw ApiException.Validation(
				"limit must be between 1 and 500.", $"'{limit}' is out of range.");
		}

		if (offset < 0)
		{
			throw ApiException.Validation("offset must be zero or greater.", $"'{offset}' is negative.");
		}

		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		ComponentResultFindingsPage page = await _componentResults.GetLatestFindingsAsync(id, limit, offset, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = page.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return Ok(new ComponentResultFindingsResponse(
			JobId: id.ToString(),
			AttemptNumber: page.Result?.AttemptNumber,
			ComponentResultStatus: page.Result?.Status,
			OutputKind: page.Result?.OutputKind,
			StandardsNote: ResolveStandardsNote(page.Result?.OutputKind),
			Items: [.. page.Items.Select(f => new ComponentResultFindingResponse(
				ControlId: f.ControlId,
				RuleId: f.RuleId,
				Title: f.Title,
				Severity: f.Severity,
				Status: f.Status,
				Evidence: f.Evidence))],
			Limit: limit,
			Offset: offset));
	}

	/// <summary>
	/// Issue #745 remainder: artifact metadata (kind/path/digest/size) for a job's
	/// LATEST <c>component_results</c> attempt (migration 0063's
	/// <c>component_result_artifacts</c>). Viewer+. Metadata-only -- this endpoint never
	/// streams artifact bytes; byte download for the two downloadable kinds (`hdf`,
	/// `ckl`) remains <see cref="GetArtifact"/>. Unpaged (bounded by the closed 5-value
	/// <see cref="ComponentResultArtifactKinds"/> vocabulary). Same honest-empty
	/// convention as <see cref="GetComponentResultFindings"/>: job exists, no attempt
	/// recorded (or purged) yet -&gt; <c>items: []</c> with null attempt fields, not a
	/// 404.
	/// </summary>
	[HttpGet("{id:guid}/component-results/artifacts")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComponentResultArtifactsResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ComponentResultArtifactsResponse>> GetComponentResultArtifacts(Guid id, CancellationToken cancellationToken)
	{
		JobSummary? job = await _repository.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
		if (job is null)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{id}' does not exist.");
		}

		ComponentResultArtifactsList list = await _componentResults.GetLatestArtifactsAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new ComponentResultArtifactsResponse(
			JobId: id.ToString(),
			AttemptNumber: list.Result?.AttemptNumber,
			ComponentResultStatus: list.Result?.Status,
			OutputKind: list.Result?.OutputKind,
			StandardsNote: ResolveStandardsNote(list.Result?.OutputKind),
			Items: [.. list.Items.Select(a => new ComponentResultArtifactResponse(
				Kind: a.Kind,
				Path: a.Path,
				Digest: a.Digest,
				SizeBytes: a.SizeBytes))]));
	}

	/// <summary>
	/// Issue #743 AC "SRG results clearly state they are not DISA-published STIG
	/// results": the fixed statement for a result whose FROZEN plan item's catalog
	/// output kind is SRG (<c>hdf</c>); null for STIG (<c>hdf_ckl</c>) results and for
	/// legacy results with no plan linkage. Keyed on the frozen catalog kind, never the
	/// target's connection kind.
	/// </summary>
	private static string? ResolveStandardsNote(string? outputKind) =>
		string.Equals(outputKind, Waypoint.Core.ComplianceContent.CatalogOutputKinds.Hdf, StringComparison.Ordinal)
			? Waypoint.Core.Scans.SrgResultStatements.NotDisaPublished
			: null;
}
