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

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>download</c> <see cref="Waypoint.Core.Jobs.JobShape.Simple"/> job handler
/// (issue #10, M1 vertical slice: <c>queued -&gt; running -&gt; done|failed</c> on the
/// owning job row; the finer <c>queued -&gt; downloading -&gt; verifying -&gt;
/// verified|failed</c> machine lives on the <c>downloads</c> row this handler drives).
///
/// Payload contract (JSON object, set by <c>DownloadsController.QueueDownloads</c>):
/// <c>{"download_id": "&lt;uuid&gt;", "depot_artifact_id": "&lt;uuid&gt;", "source_url":
/// "..."}</c>. No credential is decrypted here -- an already-indexed depot artifact's
/// fetch URL carries no separate secret in this slice (unlike <c>catalog-index</c>'s
/// depot token), so security.md control 4's decrypt-audit path does not apply.
///
/// Resume/retry is delegated entirely to the PowerShell layer
/// (<c>Invoke-WaypointDownload</c>, mirroring vcf-docker-download's
/// <c>Save-WebFile</c> -- partial-file resume via a <c>.resume.tmp</c> merge and its
/// own backoff loop); this handler's own <see cref="MaxHandlerRetries"/> only covers a
/// transport-level invocation failure (the PS call itself throwing/returning
/// unsuccessful), not byte-range retries, which the sibling-repository function already owns.
///
/// sha256 verification: after a successful invocation, this handler independently
/// hashes the file on disk and compares to <c>depot_artifacts.sha256</c> — trusting
/// only a self-reported "Success" from the download step would let a corrupted
/// transfer pass silently. A mismatch moves the file into a
/// <c>&lt;ArtifactStorePath&gt;/quarantine/</c> subdirectory (overwriting any stale
/// quarantined copy from a previous failed attempt, so a retry starts clean) and marks
/// both the download row and <c>depot_artifacts.status</c> <c>failed</c>.
/// </summary>
public sealed class DownloadJobHandler : IJobHandler
{
	private const int MaxHandlerRetries = 3;
	private const string InvocationCommand = "Invoke-WaypointDownload";

	private readonly IPowerShellExecutor _executor;
	private readonly IDownloadRepository _downloads;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IOptions<DownloadOptions> _options;

	public DownloadJobHandler(
		IPowerShellExecutor executor,
		IDownloadRepository downloads,
		IDepotArtifactRepository artifacts,
		IOptions<DownloadOptions> options)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(downloads);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(options);

		_executor = executor;
		_downloads = downloads;
		_artifacts = artifacts;
		_options = options;
	}

	public string JobType => "download";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		DownloadPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"download payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"download payload is invalid: {exception.Message}");
		}

		Download? download = await _downloads.GetAsync(payload.DownloadId, cancellationToken).ConfigureAwait(false);
		if (download is null)
		{
			return JobExecutionOutcome.Failed($"downloads row '{payload.DownloadId}' does not exist.");
		}

		DepotArtifact? artifact = await FindArtifactAsync(payload.DepotArtifactId, cancellationToken).ConfigureAwait(false);
		if (artifact is null)
		{
			await MarkFailedAsync(payload.DownloadId, payload.DepotArtifactId, "depot artifact no longer exists.", cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed("depot artifact no longer exists.");
		}

		string storeRoot = _options.Value.ArtifactStorePath;
		string destinationPath = Path.Combine(storeRoot, SanitizeFileName(artifact.ExternalId));

		await _downloads.UpdateProgressAsync(
			payload.DownloadId, DownloadStates.Downloading, bytesTotal: null, bytesDownloaded: null,
			downloadRateBps: null, etaSeconds: null, failureReason: null, cancellationToken).ConfigureAwait(false);
		await EmitProgressAsync(context, payload.DownloadId, DownloadStates.Downloading, 0, null, cancellationToken).ConfigureAwait(false);

		PowerShellExecutionOutput invocation = await InvokeDownloadWithRetryAsync(
			payload, destinationPath, context, cancellationToken).ConfigureAwait(false);

		if (!invocation.Succeeded)
		{
			await MarkFailedAsync(payload.DownloadId, payload.DepotArtifactId, invocation.FailureReason, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(invocation.FailureReason);
		}

		await _downloads.UpdateProgressAsync(
			payload.DownloadId, DownloadStates.Verifying, bytesTotal: invocation.Size, bytesDownloaded: invocation.Size,
			downloadRateBps: null, etaSeconds: 0, failureReason: null, cancellationToken).ConfigureAwait(false);
		await EmitProgressAsync(context, payload.DownloadId, DownloadStates.Verifying, invocation.Size, invocation.Size, cancellationToken).ConfigureAwait(false);

		bool verified = await VerifyChecksumAsync(destinationPath, artifact.Sha256, cancellationToken).ConfigureAwait(false);
		if (!verified)
		{
			string quarantinePath = await QuarantineAsync(destinationPath, cancellationToken).ConfigureAwait(false);
			string reason = $"checksum mismatch: downloaded file did not match depot_artifacts.sha256 (quarantined at '{quarantinePath}').";
			await MarkFailedAsync(payload.DownloadId, payload.DepotArtifactId, reason, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(reason);
		}

		await _downloads.UpdateProgressAsync(
			payload.DownloadId, DownloadStates.Verified, bytesTotal: invocation.Size, bytesDownloaded: invocation.Size,
			downloadRateBps: null, etaSeconds: 0, failureReason: null, cancellationToken).ConfigureAwait(false);
		await EmitProgressAsync(context, payload.DownloadId, DownloadStates.Verified, invocation.Size, invocation.Size, cancellationToken).ConfigureAwait(false);
		await _artifacts.UpsertAsync(
			new DepotArtifactUpsert(artifact.ExternalId, artifact.Sha256, "present", artifact.MetadataJson), cancellationToken).ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"Downloaded and verified '{artifact.ExternalId}' ({invocation.Size} bytes).");
	}

	/// <summary>
	/// Invokes the PS layer, retrying a transport-level failure (the invocation itself
	/// not succeeding — distinct from the byte-range resume/backoff
	/// <c>Invoke-WaypointDownload</c>/<c>Save-WebFile</c> already own) up to
	/// <see cref="MaxHandlerRetries"/> times, incrementing <c>downloads.retry_count</c>
	/// on each attempt after the first so it is visible on the queue view (data
	/// ledger: "rate, ETA, retries").
	/// </summary>
	private async Task<PowerShellExecutionOutput> InvokeDownloadWithRetryAsync(
		DownloadPayload payload, string destinationPath, JobExecutionContext context, CancellationToken cancellationToken)
	{
		string? lastFailureReason = null;
		for (int attempt = 0; attempt < MaxHandlerRetries; attempt++)
		{
			if (attempt > 0)
			{
				await _downloads.IncrementRetryCountAsync(payload.DownloadId, cancellationToken).ConfigureAwait(false);
			}

			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["Url"] = payload.SourceUrl,
				["OutFile"] = destinationPath,
				["RetryCount"] = MaxHandlerRetries,
				["Source"] = "depot",
			};

			PowerShellRequest request = new(InvocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

			if (!result.Succeeded)
			{
				lastFailureReason = result.FailureReason ?? "download invocation failed with no failure reason.";
				continue;
			}

			PowerShellExecutionOutput? output = TryParseOutput(result.Output);
			if (output is null)
			{
				lastFailureReason = "download invocation returned no result.";
				continue;
			}

			if (!output.Succeeded)
			{
				lastFailureReason = "download invocation reported failure.";
				continue;
			}

			return output;
		}

		return PowerShellExecutionOutput.Failure(lastFailureReason ?? "download failed after retries.");
	}

	/// <summary>
	/// Computes sha256 of the downloaded file and compares to the catalog's recorded
	/// hash. A null/blank recorded hash (an artifact indexed without a known hash)
	/// verifies unconditionally -- there is nothing to compare against, and refusing
	/// every such artifact would make the catalog's optional hash field mandatory in
	/// practice.
	/// </summary>
	private static async Task<bool> VerifyChecksumAsync(string filePath, string? expectedSha256, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(expectedSha256))
		{
			return true;
		}

		await using FileStream stream = File.OpenRead(filePath);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		string actual = Convert.ToHexString(hash);
		return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Moves a checksum-failed file into <c>&lt;store&gt;/quarantine/&lt;filename&gt;</c>,
	/// overwriting any prior quarantined copy of the same artifact so a retriable
	/// re-download never trips over a stale quarantined file left by an earlier
	/// attempt (issue #10 acceptance criterion: corrupted download is retriable).
	/// </summary>
	private Task<string> QuarantineAsync(string filePath, CancellationToken cancellationToken)
	{
		_ = cancellationToken; // no async I/O here today; kept for signature symmetry with the other private steps
		string quarantineDir = Path.Combine(_options.Value.ArtifactStorePath, "quarantine");
		Directory.CreateDirectory(quarantineDir);
		string quarantinePath = Path.Combine(quarantineDir, Path.GetFileName(filePath));

		if (File.Exists(quarantinePath))
		{
			File.Delete(quarantinePath);
		}

		File.Move(filePath, quarantinePath);
		return Task.FromResult(quarantinePath);
	}

	private async Task MarkFailedAsync(Guid downloadId, Guid depotArtifactId, string? reason, CancellationToken cancellationToken)
	{
		await _downloads.UpdateProgressAsync(
			downloadId, DownloadStates.Failed, bytesTotal: null, bytesDownloaded: null,
			downloadRateBps: null, etaSeconds: null, failureReason: reason, cancellationToken).ConfigureAwait(false);

		DepotArtifact? artifact = await FindArtifactAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
		if (artifact is not null)
		{
			await _artifacts.UpsertAsync(
				new DepotArtifactUpsert(artifact.ExternalId, artifact.Sha256, "failed", artifact.MetadataJson), cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<DepotArtifact?> FindArtifactAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		// IDepotArtifactRepository has no get-by-id today (issue #193 only needed
		// list+upsert) -- filter the catalog list scoped tightly enough that a single
		// artifact store does not need one. Acceptable at M1 scale; revisit if the
		// catalog grows large enough to make this scan expensive.
		(IReadOnlyList<DepotArtifact> items, _) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, cancellationToken).ConfigureAwait(false);
		return items.FirstOrDefault(item => item.Id == depotArtifactId);
	}

	private static async Task EmitProgressAsync(
		JobExecutionContext context, Guid downloadId, string state, long bytesDownloaded, long? bytesTotal, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new
		{
			download_id = downloadId,
			state,
			bytes_downloaded = bytesDownloaded,
			bytes_total = bytesTotal,
			percent = bytesTotal is > 0 ? Math.Clamp((int)(bytesDownloaded * 100 / bytesTotal.Value), 0, 100) : (int?)null,
		});

		await context.Events
			.EmitAsync(JobEventTypes.DownloadProgress, context.Job.Id, context.Job.RunId, payload, cancellationToken)
			.ConfigureAwait(false);
	}

	private static string SanitizeFileName(string externalId)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		char[] sanitized = externalId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
		return new string(sanitized);
	}

	private static DownloadPayload ParsePayload(string payloadJson)
	{
		using JsonDocument document = JsonDocument.Parse(payloadJson);
		JsonElement root = document.RootElement;

		if (!root.TryGetProperty("download_id", out JsonElement downloadIdElement) || !Guid.TryParse(downloadIdElement.GetString(), out Guid downloadId))
		{
			throw new ArgumentException("payload requires a GUID string 'download_id' property");
		}

		if (!root.TryGetProperty("depot_artifact_id", out JsonElement artifactIdElement) || !Guid.TryParse(artifactIdElement.GetString(), out Guid artifactId))
		{
			throw new ArgumentException("payload requires a GUID string 'depot_artifact_id' property");
		}

		if (!root.TryGetProperty("source_url", out JsonElement urlElement) || urlElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(urlElement.GetString()))
		{
			throw new ArgumentException("payload requires a string 'source_url' property");
		}

		return new DownloadPayload(downloadId, artifactId, urlElement.GetString()!);
	}

	private static PowerShellExecutionOutput? TryParseOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		// Issue #976: routed through the PowerShellValueUnwrap chokepoint for uniformity
		// -- this is a top-level pipeline-output property (already unwrapped by
		// PowerShellExecutor.Unwrap before TryParseOutput ever sees it), so it was never
		// actually exposed to #972's nested-property hazard, but Unwrap is idempotent on
		// an already-unwrapped value.
		bool success = PowerShellValueUnwrap.Unwrap(psObject.Properties["Success"]?.Value) is true;
		long size = PowerShellValueUnwrap.Unwrap(psObject.Properties["Size"]?.Value) switch
		{
			long l => l,
			int i => i,
			_ => 0L,
		};

		return success ? PowerShellExecutionOutput.Success(size) : PowerShellExecutionOutput.Failure("download invocation reported failure.");
	}

	private sealed record DownloadPayload(Guid DownloadId, Guid DepotArtifactId, string SourceUrl);

	private sealed record PowerShellExecutionOutput(bool Succeeded, long Size, string? FailureReason)
	{
		public static PowerShellExecutionOutput Success(long size) => new(true, size, null);

		public static PowerShellExecutionOutput Failure(string reason) => new(false, 0, reason);
	}
}
