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
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md <c>/downloads</c> surface (issue #10, M1 vertical slice):
/// <c>POST /downloads</c> queues one <c>download</c> job per requested artifact,
/// <c>GET /downloads</c> lists the queue (rate/ETA/retries per the data ledger), and
/// <c>DELETE /downloads/{id}</c> cancels one. Each queued artifact gets its own
/// single-job run (mirroring <c>CatalogController.Sync</c>'s fan-out shape but 1:1
/// rather than N:1) so that cancelling one artifact's download aborts only that run,
/// never a sibling artifact queued in the same request -- the per-job Continue policy
/// (ADR-0008) this way falls out of "N independent runs" rather than needing its own
/// cancel-one-job-of-a-run primitive.
/// </summary>
[ApiController]
[Route("api/v1/downloads")]
public sealed class DownloadsController : ControllerBase
{
	/// <summary>No per-target priority tiers apply to a depot download (domain-model.md's six scan priorities are for scan/remediate targets); runs at the lowest urgency so it never starves a scan.</summary>
	private const short DownloadPriority = 6;

	private readonly IDownloadRepository _downloads;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobQueueRepository _jobs;

	public DownloadsController(IDownloadRepository downloads, IDepotArtifactRepository artifacts, IJobQueueRepository jobs)
	{
		ArgumentNullException.ThrowIfNull(downloads);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(jobs);
		_downloads = downloads;
		_artifacts = artifacts;
		_jobs = jobs;
	}

	/// <summary>
	/// Queue downloads for one or more indexed depot artifacts. Admin-gated, matching
	/// <c>CatalogController.Sync</c>'s <c>POST /catalog/sync</c> convention. An unknown
	/// artifact id in the request fails that one entry with a 404-shaped detail in the
	/// response rather than rejecting the whole batch -- CLAUDE.md's "individual target
	/// failures must not halt a run" applies at request-validation time too.
	/// </summary>
	[HttpPost]
	[RequireAdminRole]
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

		List<string> downloadIds = [];
		string? runIdForResponse = null;
		foreach (string rawId in request.DepotArtifactIds)
		{
			if (!Guid.TryParse(rawId, out Guid artifactId) || !byId.TryGetValue(artifactId, out DepotArtifact? artifact))
			{
				throw ApiException.NotFound("Depot artifact not found.", $"Depot artifact '{rawId}' does not exist.");
			}

			Guid downloadId = await _downloads.CreateAsync(artifact.Id, jobId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

			string payload = JsonSerializer.Serialize(new
			{
				download_id = downloadId,
				depot_artifact_id = artifact.Id,
				source_url = BuildSourceUrl(artifact),
			});

			Guid runId = await _jobs.CreateRunAsync("download", "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);
			JobSpec[] specs = [new JobSpec("download", DownloadPriority, TargetId: artifact.Id, TargetName: artifact.ExternalId, Payload: payload)];
			IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

			await _downloads.SetJobAsync(downloadId, jobIds[0], runId, cancellationToken).ConfigureAwait(false);

			downloadIds.Add(downloadId.ToString());
			runIdForResponse ??= runId.ToString();
		}

		return Accepted(new DownloadsQueuedResponse(runIdForResponse!, downloadIds));
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
	/// Cancel a queued or in-flight download. Admin, matching this resource's POST
	/// gate -- aborts the download's owning single-job run (see class doc comment),
	/// which cooperatively cancels the in-flight <c>download</c> job and never touches
	/// a sibling artifact's run.
	/// </summary>
	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DownloadCancelledResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DownloadCancelledResponse>> CancelDownload(Guid id, CancellationToken cancellationToken)
	{
		Download? download = await _downloads.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (download is null)
		{
			throw ApiException.NotFound("Download not found.", $"Download '{id}' does not exist.");
		}

		if (download.RunId is Guid runId)
		{
			// Aborting is a no-op against an already-terminal run (AbortRunAsync's own
			// contract) -- safe to call even if the download already finished/failed
			// between the GetAsync above and here.
			await _jobs.AbortRunAsync(runId, cancellationToken).ConfigureAwait(false);
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
