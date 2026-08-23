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
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Secrets;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md <c>/downloads</c> surface (issue #10, M1 vertical slice):
/// <c>POST /downloads</c> queues N artifacts as ONE run containing N <c>download</c>
/// jobs (one per artifact), <c>GET /downloads</c> lists the queue (rate/ETA/retries per
/// the data ledger), and <c>DELETE /downloads/{id}</c> cancels a single download's job.
/// The one-run-N-jobs fan-out follows ADR-0008 ("a user-initiated Run expands to one
/// Job per target/component") and reuses the same run-creation + <c>FanOutJobsAsync</c>
/// path every scan/catalog run uses -- <c>POST</c> returns that single run_id honestly,
/// and #12's queue UI gets one batch object. The two guarantees this shape relies on are
/// already core job-engine capabilities: the Continue policy (one job
/// failing/quarantining never halts its siblings) is inherent to the engine -- nothing
/// aborts a run on a single job's failure -- and per-download cancel maps to a single-job
/// cancel (<see cref="IJobControlRepository.CancelJobAsync"/>) that never touches sibling
/// jobs in the same run. A running download's job now stops promptly too (issue #234):
/// CancelJobAsync sets <c>cancel_requested</c> on it and the dispatcher's heartbeat loop
/// observes that flag on its next tick, same as the run-scoped abort check.
/// </summary>
[ApiController]
[Route("api/v1/downloads")]
public sealed class DownloadsController : ControllerBase
{
	/// <summary>No per-target priority tiers apply to a depot download (domain-model.md's six scan priorities are for scan/remediate targets); runs at the lowest urgency so it never starves a scan.</summary>
	private const short DownloadPriority = 6;

	private readonly IDownloadRepository _downloads;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobControlRepository _jobs;
	private readonly CredentialRepository _credentials;
	private readonly IWorkerRegistryReader _workerRegistry;
	private readonly IOptions<CatalogOptions> _catalogOptions;

	public DownloadsController(
		IDownloadRepository downloads,
		IDepotArtifactRepository artifacts,
		IJobControlRepository jobs,
		CredentialRepository credentials,
		IWorkerRegistryReader workerRegistry,
		IOptions<CatalogOptions> catalogOptions)
	{
		ArgumentNullException.ThrowIfNull(downloads);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(workerRegistry);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		_downloads = downloads;
		_artifacts = artifacts;
		_jobs = jobs;
		_credentials = credentials;
		_workerRegistry = workerRegistry;
		_catalogOptions = catalogOptions;
	}

	/// <summary>
	/// Combined download readiness (issue #560): depot-token credential health plus the
	/// managed <c>vcf-download-tool</c>'s installed state. Viewer+, matching every other
	/// read on this controller -- this is operational chrome (what's missing before a
	/// download can run), not privileged data.
	///
	/// Tool presence is read from the most recent download-runner heartbeat's
	/// <c>worker_registry.tool_present</c> column (any row reporting a non-null value;
	/// several download-runner replicas would all report the same shared managed-tool
	/// volume) rather than a direct filesystem check -- the API process never mounts
	/// that volume (deploy/docker-compose.yml), only the download-runner does. A stale
	/// or absent heartbeat (no download-runner has ever reported) reads as
	/// <c>tool_installed: null</c> -- "unknown," not "installed" or "missing" -- so the
	/// UI can distinguish "no runner has weighed in yet" from a real negative.
	/// </summary>
	[HttpGet("readiness")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(DownloadReadinessResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DownloadReadinessResponse>> GetReadiness(CancellationToken cancellationToken)
	{
		CredentialResponse? depotToken = await _credentials
			.FindByTypeAsync(_catalogOptions.Value.DepotTokenCredentialType, cancellationToken)
			.ConfigureAwait(false);

		bool depotTokenConfigured = depotToken is { HasSecret: true };
		string? depotTokenHealth = depotToken?.Health;

		IReadOnlyList<WorkerHeartbeat> workers = await _workerRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
		bool? toolInstalled = workers
			.Where(worker => worker.ToolPresent is not null)
			.Select(worker => worker.ToolPresent)
			.FirstOrDefault();

		List<string> missing = [];
		if (!depotTokenConfigured)
		{
			missing.Add("depot_token");
		}
		else if (depotTokenHealth == CredentialHealthStates.AuthFailing)
		{
			missing.Add("depot_token_auth_failing");
		}

		if (toolInstalled != true)
		{
			missing.Add("tool_not_installed");
		}

		bool ready = missing.Count == 0;
		return Ok(new DownloadReadinessResponse(ready, depotTokenConfigured, depotTokenHealth, toolInstalled, missing));
	}

	/// <summary>
	/// Queue downloads for one or more indexed depot artifacts as a single run of N jobs.
	/// Operator+, matching api-contract.md ("POST: artifact ids -> queued `download` jobs
	/// (Operator+)") and docs/domain-model.md's Roles table ("Operator: Cyber + ...
	/// download/catalog/content-library management"). Issue #30: this was Admin-gated as
	/// an M1 stopgap before the Operator role existed (pre-RBAC); RBAC has now landed, so
	/// it widens to the contract's documented floor. An unknown artifact id fails the
	/// whole request with a 404 before any run is created -- the batch is validated up
	/// front, then fanned out atomically, so a bad id can never leave a half-created run.
	/// </summary>
	[HttpPost]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(DownloadsQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<DownloadsQueuedResponse>> QueueDownloads(
		QueueDownloadsRequest request, CancellationToken cancellationToken)
	{
		if (request?.DepotArtifactIds is null || request.DepotArtifactIds.Count == 0)
		{
			throw ApiException.Validation("At least one depot_artifact_id is required.");
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		(IReadOnlyList<DepotArtifact> catalog, _) = await _artifacts
			.ListAsync(new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, cancellationToken)
			.ConfigureAwait(false);
		Dictionary<Guid, DepotArtifact> byId = catalog.ToDictionary(item => item.Id);

		// Resolve every requested artifact and stage its download row + job spec first, so
		// an unknown id is a clean 404 before we create the run (no orphaned run/jobs).
		List<DepotArtifact> resolved = [];
		foreach (string rawId in request.DepotArtifactIds)
		{
			if (!Guid.TryParse(rawId, out Guid artifactId) || !byId.TryGetValue(artifactId, out DepotArtifact? artifact))
			{
				throw ApiException.NotFound("Depot artifact not found.", $"Depot artifact '{rawId}' does not exist.");
			}

			resolved.Add(artifact);
		}

		// One run for the whole batch (ADR-0008), then one download job per artifact via
		// the shared FanOutJobsAsync path -- identical to every scan/catalog run's fan-out.
		Guid runId = await _jobs.CreateRunAsync("download", "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

		List<Guid> downloadIds = [];
		List<JobSpec> specs = [];
		foreach (DepotArtifact artifact in resolved)
		{
			Guid downloadId = await _downloads.CreateAsync(artifact.Id, jobId: null, initiatedBy, cancellationToken).ConfigureAwait(false);
			downloadIds.Add(downloadId);

			string payload = JsonSerializer.Serialize(new
			{
				download_id = downloadId,
				depot_artifact_id = artifact.Id,
				source_url = BuildSourceUrl(artifact),
			});
			specs.Add(new JobSpec("download", DownloadPriority, TargetId: artifact.Id, TargetName: artifact.ExternalId, Payload: payload));
		}

		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);
		for (int i = 0; i < downloadIds.Count; i++)
		{
			await _downloads.SetJobAsync(downloadIds[i], jobIds[i], runId, cancellationToken).ConfigureAwait(false);
		}

		return Accepted(new DownloadsQueuedResponse(runId.ToString(), downloadIds.Select(id => id.ToString()).ToArray()));
	}

	/// <summary>List the download queue, newest-first. Viewer+, matching <c>CatalogController.ListArtifacts</c>.</summary>
	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(DownloadResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<DownloadResponse>>> ListDownloads(
		[FromQuery] PageRequest page, CancellationToken cancellationToken)
	{
		(IReadOnlyList<Download> items, long totalCount) = await _downloads.ListAsync(page, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = totalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(items.Select(DownloadResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Cancel a single download within its run. Operator+, matching POST's floor (issue
	/// #30 widened both from the Admin-only M1 stopgap now that RBAC has landed).
	/// Cancels only this download's own <c>download</c> job
	/// via <see cref="IJobControlRepository.CancelJobAsync"/>, never aborting the run or
	/// touching a sibling artifact's job queued in the same batch. A queued job is
	/// cancelled cleanly and immediately; a job already running stops cooperatively at the
	/// dispatcher's next heartbeat tick (issue #234) rather than running to completion. The
	/// download row is marked <c>cancelled</c> either way -- the API response does not wait
	/// for the in-flight job to actually stop, matching the queued case's fire-and-forget
	/// shape.
	/// </summary>
	[HttpDelete("{id:guid}")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(DownloadCancelledResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DownloadCancelledResponse>> CancelDownload(Guid id, CancellationToken cancellationToken)
	{
		Download? download = await _downloads.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (download is null)
		{
			throw ApiException.NotFound("Download not found.", $"Download '{id}' does not exist.");
		}

		if (download.JobId is Guid jobId)
		{
			// Per-job cancel: idempotent and no-op against an already-running/terminal
			// job, so it is safe even if the download finished between the GetAsync above
			// and here. It never aborts the run, so sibling downloads keep dispatching.
			await _jobs.CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		}

		bool alreadyTerminal = download.State is DownloadStates.Verified or DownloadStates.Failed or DownloadStates.Cancelled;
		if (!alreadyTerminal)
		{
			await _downloads.UpdateProgressAsync(
				id, DownloadStates.Cancelled, bytesTotal: null, bytesDownloaded: null,
				downloadRateBps: null, etaSeconds: null, failureReason: "Cancelled by request.", cancellationToken).ConfigureAwait(false);
		}

		Download? updated = await _downloads.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new DownloadCancelledResponse(id.ToString(), updated?.State ?? DownloadStates.Cancelled));
	}

	private static string BuildSourceUrl(DepotArtifact artifact)
	{
		// M1 scope: the depot artifact's own external_id IS the source location on the
		// already-indexed offline depot share (see CatalogIndexJobHandler / Get-FileManifest) --
		// there is no separate "download URL" concept yet, so the handler receives a
		// file:// URI under the configured depot path rather than an HTTP(S) endpoint.
		// A future connected-mode "fetch from Broadcom depot" path would populate this
		// differently; that is out of scope here (see docs/roadmap.md M1 vs later slices).
		return artifact.ExternalId;
	}
}
