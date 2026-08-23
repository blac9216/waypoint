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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #39 (epic #558) backend part 1: the three ADR-0015 install paths for the
/// operator-gated <c>vcf-download-tool</c>, as jobs the download-runner executes and
/// verifies before activating. The project never publishes this binary (ADR-0015); every
/// path here writes verified state into the persistent managed-tool volume rather than
/// anything shipped in source or images.
///
/// <c>POST .../install</c> (local-repository) and <c>POST .../upload</c> (manual upload)
/// both queue exactly one <c>tool-install</c> job, mirroring <c>DownloadsController</c>'s
/// one-run-per-request shape. The connected-mode depot-fetch path is deferred to a
/// follow-up issue (see this PR's body) -- not exposed here.
/// </summary>
[ApiController]
[Route("api/v1/downloads/tool")]
public sealed class ManagedToolController : ControllerBase
{
	/// <summary>Same low-urgency tier as an ordinary artifact download (<c>DownloadsController.DownloadPriority</c>) -- an install must never starve a scan.</summary>
	private const short ToolInstallPriority = 6;

	private const long MaxUploadBytes = 512L * 1024 * 1024;

	private readonly IJobControlRepository _jobs;
	private readonly IManagedToolInstallRepository _installs;
	private readonly IOptions<ManagedToolOptions> _options;

	public ManagedToolController(
		IJobControlRepository jobs,
		IManagedToolInstallRepository installs,
		IOptions<ManagedToolOptions> options)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(installs);
		ArgumentNullException.ThrowIfNull(options);
		_jobs = jobs;
		_installs = installs;
		_options = options;
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
	/// type the local-repository path uses, with <c>source: "upload"</c>. Signature
	/// verification happens in the job, identically to every other path -- an upload is
	/// not activated just because an operator has Operator role; it still must carry a
	/// valid Broadcom signature.
	/// </summary>
	[HttpPost("upload")]
	[RequireOperatorRole]
	[RequestSizeLimit(MaxUploadBytes)]
	[ProducesResponseType(typeof(ManagedToolInstallQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<ManagedToolInstallQueuedResponse>> Upload(
		IFormFile? artifact, IFormFile? signature, [FromForm] string? version, CancellationToken cancellationToken)
	{
		if (artifact is null || artifact.Length == 0)
		{
			throw ApiException.Validation("An 'artifact' file is required.");
		}

		if (signature is null || signature.Length == 0)
		{
			throw ApiException.Validation("A detached 'signature' file is required -- an unsigned upload is never accepted, even before verification runs.");
		}

		ManagedToolOptions options = _options.Value;
		Directory.CreateDirectory(options.UploadStagingPath);

		string stagedName = $"{Guid.NewGuid():N}-{Path.GetFileName(artifact.FileName)}";
		string stagedArtifactPath = Path.Combine(options.UploadStagingPath, stagedName);
		string stagedSignaturePath = stagedArtifactPath + ".sig";

		await using (FileStream artifactStream = System.IO.File.Create(stagedArtifactPath))
		{
			await artifact.CopyToAsync(artifactStream, cancellationToken).ConfigureAwait(false);
		}

		await using (FileStream signatureStream = System.IO.File.Create(stagedSignaturePath))
		{
			await signature.CopyToAsync(signatureStream, cancellationToken).ConfigureAwait(false);
		}

		return await QueueInstallAsync(ManagedToolInstallSources.Upload, stagedName, version, cancellationToken).ConfigureAwait(false);
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
		string source, string sourcePath, string? version, CancellationToken cancellationToken)
	{
		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		Guid runId = await _jobs.CreateRunAsync("tool-install", "{}", credentialId: null, initiatedBy, cancellationToken).ConfigureAwait(false);

		string payload = JsonSerializer.Serialize(new
		{
			source,
			source_path = sourcePath,
			version,
			initiated_by = initiatedBy,
		});

		JobSpec spec = new("tool-install", ToolInstallPriority, Payload: payload);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(runId, [spec], initiatedBy, cancellationToken).ConfigureAwait(false);

		return Accepted(new ManagedToolInstallQueuedResponse(runId.ToString(), jobIds[0].ToString()));
	}
}
