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
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #39 (epic #558) backend: the three ADR-0015 install paths for the
/// operator-gated <c>vcf-download-tool</c>, as jobs the download-runner executes and
/// verifies before activating. The project never publishes this binary (ADR-0015); every
/// path here writes verified state into the persistent managed-tool volume rather than
/// anything shipped in source or images.
///
/// <c>POST .../install</c> (local-repository), <c>POST .../upload</c> (manual upload),
/// and <c>POST .../fetch</c> (depot, connected-mode only) each queue exactly one
/// <c>tool-install</c> job, mirroring <c>DownloadsController</c>'s one-run-per-request
/// shape.
/// </summary>
[ApiController]
[Route("api/v1/downloads/tool")]
public sealed class ManagedToolController : ControllerBase
{
	/// <summary>Same low-urgency tier as an ordinary artifact download (<c>DownloadsController.DownloadPriority</c>) -- an install must never starve a scan.</summary>
	private const short ToolInstallPriority = 6;

	// Single source of truth for the upload endpoint's size ceiling. Two OTHER limits
	// must be kept in lockstep with this constant (issue #641 -- they had drifted):
	//  - deploy/nginx/conf.d/default.conf, the `location = /api/v1/downloads/tool/upload`
	//    block's `client_max_body_size 512m` (nginx has no way to reference a C#
	//    constant; its comment points back here).
	//  - [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)] on Upload()
	//    below -- the multipart FORM reader's own cap, separate from RequestSizeLimit's
	//    connection/body-read cap. Both attributes reference this constant so they
	//    cannot silently diverge from each other; only nginx must be changed by hand.
	private const long MaxUploadBytes = 512L * 1024 * 1024;

	private readonly IJobControlRepository _jobs;
	private readonly IManagedToolInstallRepository _installs;
	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IApplianceStateRepository _applianceState;

	public ManagedToolController(
		IJobControlRepository jobs,
		IManagedToolInstallRepository installs,
		IOptions<ManagedToolOptions> options,
		IApplianceStateRepository applianceState)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(installs);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(applianceState);
		_jobs = jobs;
		_installs = installs;
		_options = options;
		_applianceState = applianceState;
	}

	/// <summary>
	/// Install path 1 (ADR-0015): the operator-provisioned local indexed repository.
	/// Works in both connected and disconnected mode -- the same file-based source
	/// either side of an air gap. Operator+, matching <c>DownloadsController.QueueDownloads</c>'s
	/// floor (a write that starts real work, not just visibility).
	/// </summary>
	[HttpPost("install")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(ManagedToolInstallQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<ManagedToolInstallQueuedResponse>> Install(
		InstallManagedToolRequest request, CancellationToken cancellationToken)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.SourcePath))
		{
			throw ApiException.Validation("source_path is required.");
		}

		return await QueueInstallAsync(ManagedToolInstallSources.LocalRepository, request.SourcePath, request.Version, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Install path 3 (ADR-0015): manual upload. Stages the uploaded file under
	/// <see cref="ManagedToolOptions.UploadStagingPath"/> with a generated, collision-free
	/// name (never the client-supplied file name verbatim -- avoids path-traversal or
	/// overwrite-another-upload surprises) and queues the same <c>tool-install</c> job
	/// type the local-repository path uses, with <c>source: "upload"</c>. The operator
	/// supplies SHA-256 or legacy MD5 copied from the authenticated Broadcom support
	/// record; the runner verifies every supplied value before activation.
	/// </summary>
	[HttpPost("upload")]
	[RequireOperatorRole]
	[RequestSizeLimit(MaxUploadBytes)]
	// Issue #641: RequestSizeLimit above only raises Kestrel's connection/body-read
	// ceiling. The IFormFile-bound multipart reader enforces its OWN, separate cap --
	// FormOptions.MultipartBodyLengthLimit, defaulting to 128 MiB -- and throws before
	// the file is staged if a part exceeds it. The real vcf-download-tool artifact is
	// 383-490 MB, so without this attribute every real upload 400s here even though it
	// cleared nginx and Kestrel. Scoped to this action (not a global FormOptions
	// change) so unrelated form endpoints keep the conservative 128 MiB default. See
	// MaxUploadBytes's own comment above for the three places this ceiling must agree.
	[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
	[ProducesResponseType(typeof(ManagedToolInstallQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<ManagedToolInstallQueuedResponse>> Upload(
		IFormFile? artifact, [FromForm] string? sha256, [FromForm] string? md5,
		[FromForm] string? version, CancellationToken cancellationToken)
	{
		if (artifact is null || artifact.Length == 0)
		{
			throw ApiException.Validation("An 'artifact' file is required.");
		}

		sha256 = NormalizeChecksum(sha256, 64, "sha256");
		md5 = NormalizeChecksum(md5, 32, "md5");
		if (sha256 is null && md5 is null)
		{
			throw ApiException.Validation("A published 'sha256' or legacy 'md5' checksum is required.");
		}

		ManagedToolOptions options = _options.Value;

		// Issue #621 (re-scoped per #630 review): the backend's UploadStagingPath
		// must be a writable mounted volume shared with the download-runner
		// (deploy/compose.yaml's dedicated `tool-upload-staging` volume,
		// chowned by this image's entrypoint -- see backend/docker-entrypoint.sh)
		// so the tool-install job can later read what this request stages. This is
		// deliberately NOT the `managed-tool` tool store: the backend never mounts
		// that, keeping the API off the verified tool binary and the release-key
		// trust anchor (ADR-0014 §7). A missing mount or an ownership mismatch
		// surfaces here as UnauthorizedAccessException/IOException; map it to a
		// clean 503 instead of letting it fall through as an unhandled 500 that
		// leaks a stack trace.
		try
		{
			Directory.CreateDirectory(options.UploadStagingPath);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			throw ApiException.Unavailable(
				"The upload-staging location is not writable on this appliance.",
				"Confirm the tool-upload-staging volume is mounted and writable by the backend service (see deploy/compose.yaml and deploy/README.md).");
		}

		string stagedName = $"{Guid.NewGuid():N}-{Path.GetFileName(artifact.FileName)}";
		string stagedArtifactPath = Path.Combine(options.UploadStagingPath, stagedName);

		try
		{
			await using (FileStream artifactStream = System.IO.File.Create(stagedArtifactPath))
			{
				await artifact.CopyToAsync(artifactStream, cancellationToken).ConfigureAwait(false);
			}

		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
		{
			throw ApiException.Unavailable(
				"The upload-staging location is not writable on this appliance.",
				"Confirm the tool-upload-staging volume is mounted and writable by the backend service (see deploy/compose.yaml and deploy/README.md).");
		}

		return await QueueInstallAsync(ManagedToolInstallSources.Upload, stagedName, version, cancellationToken, sha256, md5).ConfigureAwait(false);
	}

	/// <summary>
	/// Install path 2 (ADR-0015): connected-mode-only depot fetch, authenticated with
	/// the stored depot-token credential. Refuses with <see cref="ApiException.ModeUnavailable"/>
	/// before any job is even queued when the appliance is disconnected -- the same
	/// clean-refusal bar the job handler itself also enforces (defense in depth
	/// against a job that outlives a mode flip between being queued and claimed).
	/// <c>source_path</c> plays no role here (the depot URL is server-side
	/// configuration); only an optional <c>version</c> is accepted.
	/// </summary>
	[HttpPost("fetch")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(ManagedToolInstallQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<ManagedToolInstallQueuedResponse>> Fetch(
		FetchManagedToolRequest? request, CancellationToken cancellationToken)
	{
		ApplianceState? state = await _applianceState.GetAsync(cancellationToken).ConfigureAwait(false);
		if (!string.Equals(state?.Mode, "connected", StringComparison.Ordinal))
		{
			throw ApiException.ModeUnavailable(
				"Depot-fetch install requires connected mode.",
				"Use the local-repository or manual-upload install path while disconnected.");
		}

		return await QueueInstallAsync(ManagedToolInstallSources.Depot, sourcePath: "depot", request?.Version, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>Install history, newest first, including rejected attempts (issue #39 acceptance criterion). Viewer+, matching every other read on the downloads surface.</summary>
	[HttpGet("installs")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ManagedToolInstallResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ManagedToolInstallResponse>>> ListInstalls(CancellationToken cancellationToken)
	{
		IReadOnlyList<ManagedToolInstall> items = await _installs.ListAsync(limit: 200, cancellationToken).ConfigureAwait(false);
		return Ok(items.Select(ManagedToolInstallResponse.FromDomain).ToArray());
	}

	private async Task<ActionResult<ManagedToolInstallQueuedResponse>> QueueInstallAsync(
		string source, string sourcePath, string? version, CancellationToken cancellationToken,
		string? expectedSha256 = null, string? expectedMd5 = null)
	{
		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		Guid runId = await _jobs.CreateRunAsync("tool-install", "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

		string payload = JsonSerializer.Serialize(new
		{
			source,
			source_path = sourcePath,
			version,
			expected_sha256 = expectedSha256,
			expected_md5 = expectedMd5,
			initiated_by = initiatedBy,
		});

		JobSpec spec = new("tool-install", ToolInstallPriority, Payload: payload);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, [spec], initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new ManagedToolInstallQueuedResponse(runId.ToString(), jobIds[0].ToString()));
	}

	private static string? NormalizeChecksum(string? value, int length, string field)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string normalized = value.Trim().ToLowerInvariant();
		if (normalized.Length != length || !Regex.IsMatch(normalized, "\\A[0-9a-f]+\\z", RegexOptions.CultureInvariant))
		{
			throw ApiException.Validation($"'{field}' must be exactly {length} hexadecimal characters.");
		}
		return normalized;
	}
}
