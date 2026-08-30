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
	/// Combined download readiness (issue #560, extended by issue #690): the Activation
	/// Code and legacy Download Token credentials' health, reported INDEPENDENTLY (never
	/// collapsed into one flag -- issue #690 AC), plus the managed
	/// <c>vcf-download-tool</c>'s installed state. Viewer+, matching every other read on
	/// this controller -- this is operational chrome (what's missing before a download
	/// can run), not privileged data.
	///
	/// The legacy Download Token is reported for visibility (an operator migrating off
	/// it should see its status) but never gates <see cref="DownloadReadinessResponse.Ready"/>
	/// or contributes a <c>missing_prerequisites</c> entry -- nothing in this codebase's
	/// connected-fetch path resolves it (only <see cref="CredentialTypes.DepotActivationCode"/>
	/// authenticates <c>vcf-download-tool</c> commands).
	///
	/// Tool presence is read from the most recent download-runner heartbeat's
	/// <c>worker_registry.tool_present</c> column (any row reporting a non-null value;
	/// several download-runner replicas would all report the same shared managed-tool
	/// volume) rather than a direct filesystem check -- the API process never mounts
	/// that volume (deploy/compose.yaml), only the download-runner does. A stale
	/// or absent heartbeat (no download-runner has ever reported) reads as
	/// <c>tool_installed: null</c> -- "unknown," not "installed" or "missing" -- so the
	/// UI can distinguish "no runner has weighed in yet" from a real negative.
	/// </summary>
	[HttpGet("readiness")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(DownloadReadinessResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DownloadReadinessResponse>> GetReadiness(CancellationToken cancellationToken)
	{
		CredentialResponse? activationCode = await _credentials
			.FindByTypeAsync(_catalogOptions.Value.DepotActivationCodeCredentialType, cancellationToken)
			.ConfigureAwait(false);
		CredentialResponse? legacyToken = await _credentials
			.FindByTypeAsync(CredentialTypes.LegacyDownloadToken, cancellationToken)
			.ConfigureAwait(false);

		bool activationCodeConfigured = activationCode is { HasSecret: true };
		string? activationCodeHealth = activationCode?.Health;
		bool legacyTokenConfigured = legacyToken is { HasSecret: true };
		string? legacyTokenHealth = legacyToken?.Health;

		IReadOnlyList<WorkerHeartbeat> workers = await _workerRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
		bool? toolInstalled = workers
			.Where(worker => worker.ToolPresent is not null)
			.Select(worker => worker.ToolPresent)
			.FirstOrDefault();

		List<string> missing = [];
		if (!activationCodeConfigured)
		{
			missing.Add("activation_code");
		}
		else if (activationCodeHealth == CredentialHealthStates.AuthFailing)
		{
			missing.Add("activation_code_auth_failing");
		}

		if (toolInstalled != true)
		{
			missing.Add("tool_not_installed");
		}

		bool ready = missing.Count == 0;
		return Ok(new DownloadReadinessResponse(
			ready, activationCodeConfigured, activationCodeHealth, legacyTokenConfigured, legacyTokenHealth, toolInstalled, missing));
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

	/// <summary>
	/// Queue a connected VCFDT binaries download for a catalog selection: either one or
	/// more depot artifact ids, or a whole release (<see cref="ReleaseSelector"/>,
	/// resolved to its member artifacts here at enqueue time -- grill decision R2-2).
	/// Issue #1479 (epic #1181, split from #795): the same one-run-N-jobs scan-style
	/// fanout <see cref="QueueDownloads"/> uses (grill decision Q18), but onto its own
	/// <c><see cref="RunTypes.BinariesDownload"/></c> run/job type -- distinct from the
	/// legacy <c>download</c> path above, which #1040 removes entirely. This slice only
	/// enqueues <c>queued</c> jobs; the download-runner handler that claims and executes
	/// them (invoking the installed tool's <c>binaries download --id ...</c>) is #1482 --
	/// see <see cref="JobCapabilities.Download"/> and
	/// <c>Waypoint.DownloadRunner.DownloadRunnerJobTypes</c> for why this job type is
	/// reserved but not yet claimable by any runner. Operator+, matching
	/// <see cref="QueueDownloads"/>'s floor.
	///
	/// Exactly one selection mode is required: neither, or both
	/// <see cref="QueueBinariesDownloadRequest.DepotArtifactIds"/> and
	/// <see cref="QueueBinariesDownloadRequest.Release"/> supplied together, is a 400 --
	/// an ambiguous request is rejected up front rather than silently preferring one. An
	/// unknown artifact id, or a release with zero matching artifacts, is a 404 before
	/// any run is created (the same "validate the whole batch first, fan out atomically
	/// after" shape <see cref="QueueDownloads"/> uses, so a bad selection can never leave
	/// a half-created run). Both selection modes resolve the full matching set via
	/// <see cref="ListAllArtifactsAsync"/> rather than a single capped page, and a
	/// repeated id in <c>depot_artifact_ids</c> is deduped (first-seen order) to one
	/// job, not fanned out as a race on the same target.
	/// </summary>
	[HttpPost("binaries")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(BinariesDownloadQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<BinariesDownloadQueuedResponse>> QueueBinariesDownload(
		QueueBinariesDownloadRequest request, CancellationToken cancellationToken)
	{
		bool hasIds = request?.DepotArtifactIds is { Count: > 0 };
		bool hasRelease = request?.Release is { Product.Length: > 0, Version.Length: > 0 };
		if (hasIds == hasRelease)
		{
			throw ApiException.Validation(
				"Exactly one of depot_artifact_ids or release (with product and version) is required.");
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		List<DepotArtifact> resolved;
		if (hasRelease)
		{
			ReleaseSelector release = request!.Release!;
			List<DepotArtifact> members = await ListAllArtifactsAsync(
				new DepotArtifactFilter(release.Product, release.Version, null), cancellationToken).ConfigureAwait(false);
			if (members.Count == 0)
			{
				throw ApiException.NotFound(
					"Release not found.", $"No depot artifacts match product '{release.Product}' version '{release.Version}'.");
			}

			resolved = members;
		}
		else
		{
			List<DepotArtifact> catalog = await ListAllArtifactsAsync(
				new DepotArtifactFilter(null, null, null), cancellationToken).ConfigureAwait(false);
			Dictionary<Guid, DepotArtifact> byId = catalog.ToDictionary(item => item.Id);

			// Dedupe by id, preserving first-seen order, so a duplicate entry in
			// depot_artifact_ids (e.g. ["X","X"]) fans out exactly one job for that
			// artifact rather than two jobs racing on the same TargetId.
			resolved = [];
			HashSet<Guid> seen = [];
			foreach (string rawId in request!.DepotArtifactIds!)
			{
				if (!Guid.TryParse(rawId, out Guid artifactId) || !byId.TryGetValue(artifactId, out DepotArtifact? artifact))
				{
					throw ApiException.NotFound("Depot artifact not found.", $"Depot artifact '{rawId}' does not exist.");
				}

				if (seen.Add(artifactId))
				{
					resolved.Add(artifact);
				}
			}
		}

		// One run for the whole batch (ADR-0008), then one binaries-download job per
		// artifact via the same shared FanOutJobsAsync path QueueDownloads uses.
		Guid runId = await _jobs.CreateRunAsync(RunTypes.BinariesDownload, "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

		List<JobSpec> specs = [];
		foreach (DepotArtifact artifact in resolved)
		{
			string payload = JsonSerializer.Serialize(new
			{
				depot_artifact_id = artifact.Id,
				external_id = artifact.ExternalId,
			});
			specs.Add(new JobSpec(RunTypes.BinariesDownload, DownloadPriority, TargetId: artifact.Id, TargetName: artifact.ExternalId, Payload: payload));
		}

		await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new BinariesDownloadQueuedResponse(runId.ToString(), resolved.Select(artifact => artifact.Id.ToString()).ToArray()));
	}

	/// <summary>
	/// Pages through <see cref="IDepotArtifactRepository.ListAsync"/> at <see cref="PageRequest"/>'s
	/// <c>MaxLimit</c> (200) per call until every filtered row has been fetched, using the
	/// method's own filtered <c>TotalCount</c> as the stopping signal rather than trusting
	/// a single page. Used by <see cref="QueueBinariesDownload"/> so neither a whole-release
	/// selection (AC 3: "resolves to its member artifacts at enqueue time") nor an id-list
	/// selection against a catalog past the first page silently drops members past the
	/// per-page cap and enqueues a partial run under a 202 as if it were complete.
	/// </summary>
	private async Task<List<DepotArtifact>> ListAllArtifactsAsync(DepotArtifactFilter filter, CancellationToken cancellationToken)
	{
		List<DepotArtifact> all = [];
		int offset = 0;
		while (true)
		{
			(IReadOnlyList<DepotArtifact> page, long totalCount) = await _artifacts
				.ListAsync(filter, new PageRequest { Limit = 200, Offset = offset }, cancellationToken)
				.ConfigureAwait(false);
			all.AddRange(page);
			if (page.Count == 0 || all.Count >= totalCount)
			{
				break;
			}

			offset += page.Count;
		}

		return all;
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
