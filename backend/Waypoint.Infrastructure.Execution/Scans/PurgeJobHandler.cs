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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Core.Scans;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// The <c>purge</c> <see cref="JobShape.Simple"/> job handler (issue #594, epic #577):
/// deletes the on-disk scan-artifact files (<see cref="ScanArtifactPaths.RawHdf"/>,
/// <see cref="ScanArtifactPaths.AttestedHdf"/>, <see cref="ScanArtifactPaths.Ckl"/>) for
/// every job id named in the payload, then reports its outcome back into
/// <c>run_purges</c> (migration 0042) so <c>RunPurgeService</c> can finalize the purge
/// on its next call. Runs on <c>compliance-runner</c> only
/// (<see cref="JobCapabilities.Compliance"/>) -- it is the only execution domain with
/// read-write access to the scan-artifact volume (ADR-0014 §7; the API mounts it
/// read-only, deploy/compose.yaml).
///
/// Payload contract: <c>{"job_ids": ["&lt;guid&gt;", ...]}</c> -- the target run's scan
/// job ids, resolved by <c>RunPurgeService</c> from <c>jobs.run_id</c> before enqueueing
/// (not re-derived here: this handler has no notion of which run it is cleaning up, only
/// which job ids' files to delete, keeping its blast radius exactly the enumerated set).
///
/// Path confinement (the issue's Risk note: "All deletion paths must be server-derived
/// and confined beneath configured artifact roots"): every path this handler touches is
/// built exclusively from <see cref="ScanArtifactPaths"/>'s own job-id-keyed naming
/// convention against the configured <see cref="ScanOptions.ArtifactStorePath"/> root --
/// never from a client-supplied path string -- and <see cref="IsConfined"/> re-verifies
/// the resolved full path is still beneath that root before any <see cref="File.Delete(string)"/>,
/// so even a hypothetical future payload shape carrying attacker-influenced text cannot
/// walk outside the artifact store. Missing files (a job that never reached the attest
/// or convert stage, or files already removed by a prior partial run of this same
/// handler) are tolerated, not treated as failures -- see <see cref="TryDeleteIfPresent"/>.
///
/// Cancellation (issue #784): the per-target-job loop checks the execution token before
/// each job id's files, so a retention hold placed after this job was already claimed --
/// which can only reach the runner as a cooperative <c>cancel_requested</c> -- stops the
/// remaining deletions. This narrows, it does not close, that window: whatever was
/// deleted before the checkpoint is gone. A cancelled pass still reports its outcome
/// (<c>artifacts_phase = 'failed'</c>, retryable) so the partial deletion is visible
/// rather than leaving <c>run_purges</c> stuck at <c>running</c>. See
/// <see cref="RunPurgeOutcome.Held"/> for the full, case-by-case boundary.
/// </summary>
public sealed partial class PurgeJobHandler : IJobHandler
{
	// snake_case, matching the payload RunPurgeService serializes (job_ids) -- the
	// Web default (camelCase) would silently leave JobIds null, same pitfall
	// ManagedToolInstallJobHandler's PayloadOptions comment already documents.
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IRunPurgeRepository _purges;
	private readonly IOptions<ScanOptions> _scanOptions;
	private readonly ILogger<PurgeJobHandler> _logger;

	public PurgeJobHandler(IRunPurgeRepository purges, IOptions<ScanOptions> scanOptions, ILogger<PurgeJobHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(purges);
		ArgumentNullException.ThrowIfNull(scanOptions);
		ArgumentNullException.ThrowIfNull(logger);

		_purges = purges;
		_scanOptions = scanOptions;
		_logger = logger;
	}

	public string JobType => "purge";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		PurgePayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<PurgePayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed purge payload: {exception.Message}");
		}

		if (payload is null || payload.JobIds is null || payload.JobIds.Count == 0)
		{
			return JobExecutionOutcome.Failed("purge payload requires a non-empty 'job_ids' array.");
		}

		// The payload does not carry the target run's own id -- run_purges is keyed by
		// the TARGET run, not this wrapper purge job's run, so RunPurgeService resolves
		// which run_purges row this outcome belongs to from ITS OWN state
		// (run_purges.artifact_job_id = this job's id), not from anything this handler
		// reports. This handler's sole responsibility is the file deletion + reporting
		// the per-file outcome against that same job id; see FindRunIdForJobAsync.
		string artifactRoot = _scanOptions.Value.ArtifactStorePath;
		int deleted = 0;
		int failed = 0;
		List<string> errors = [];

		bool cancelled = false;

		foreach (string rawJobId in payload.JobIds)
		{
			// Issue #784: cooperative cancellation checkpoint. A retention hold placed
			// while this job is ALREADY CLAIMED cancels the job DB-side
			// (RunRetentionHoldService.PlaceHoldAsync -> cancel_requested), which the
			// dispatcher turns into a cancelled token at its next heartbeat tick.
			// Observing that token here stops the remaining files from being deleted.
			// It NARROWS -- it cannot close -- the window: the checkpoint is per target
			// job id, so files already deleted before it is reached are gone, and a hold
			// that lands mid-deletion is not seen until the next heartbeat. That residue
			// is stated as-is in RunPurgeOutcome.Held; it is not claimed away.
			if (cancellationToken.IsCancellationRequested)
			{
				cancelled = true;
				errors.Add("purge cancelled before all artifact files were deleted (retention hold placed, or job cancelled).");
				break;
			}

			if (!Guid.TryParse(rawJobId, out Guid jobId))
			{
				failed++;
				errors.Add($"'{rawJobId}' is not a valid job id.");
				continue;
			}

			foreach (string path in new[]
			{
				ScanArtifactPaths.RawHdf(artifactRoot, jobId),
				ScanArtifactPaths.AttestedHdf(artifactRoot, jobId),
				ScanArtifactPaths.Ckl(artifactRoot, jobId),
			})
			{
				(bool ok, string? error) = TryDeleteIfPresent(path, artifactRoot);
				if (ok)
				{
					deleted++;
				}
				else
				{
					failed++;
					errors.Add(error!);
				}
			}
		}

		// A cancelled pass still reports its outcome, and does so on an uncancelled token:
		// the report is what makes the partial deletion visible (artifacts_phase =
		// 'failed', retryable) instead of leaving run_purges stuck at 'running' forever
		// with no record of how far the deletion got.
		CancellationToken reportToken = cancelled ? CancellationToken.None : cancellationToken;

		Guid? targetRunId = await FindRunIdForJobAsync(context.Job.Id, reportToken).ConfigureAwait(false);
		if (targetRunId is null)
		{
			// The run_purges row this job belongs to was not found by
			// artifact_job_id -- should not happen under normal operation
			// (RunPurgeService always creates/updates the row before enqueueing this
			// job), but fail closed rather than silently drop the outcome report.
			return JobExecutionOutcome.Failed(
				$"No run_purges row references purge job '{context.Job.Id}' as its artifact_job_id -- outcome could not be recorded.");
		}

		bool succeeded = failed == 0 && !cancelled;
		await _purges.ReportArtifactOutcomeAsync(
			targetRunId.Value, succeeded, deleted, succeeded ? null : string.Join("; ", errors), reportToken).ConfigureAwait(false);

		if (cancelled)
		{
			LogCancelled(_logger, context.Job.Id, deleted);
			return JobExecutionOutcome.Failed($"purge cancelled after deleting {deleted} artifact file(s); the remaining files were left in place.");
		}

		if (!succeeded)
		{
			LogPartialFailure(_logger, context.Job.Id, deleted, failed, string.Join("; ", errors));
			return JobExecutionOutcome.Failed($"{failed} artifact file(s) could not be deleted: {string.Join("; ", errors)}");
		}

		return JobExecutionOutcome.Succeeded($"Deleted {deleted} artifact file(s) (including already-absent files) across {payload.JobIds.Count} job(s).");
	}

	/// <summary>
	/// Deletes <paramref name="path"/> if it exists, first re-confirming the resolved
	/// full path is still beneath <paramref name="artifactRoot"/> -- defense in depth
	/// per the issue's Risk note, even though <see cref="ScanArtifactPaths"/> already
	/// only ever composes paths under that root. An absent file is success (not every
	/// job reaches every pipeline stage), matching <see cref="ScanArtifactPaths.ResolveHdf"/>'s
	/// own "missing is a normal, not exceptional, state" convention.
	/// </summary>
	private static (bool Ok, string? Error) TryDeleteIfPresent(string path, string artifactRoot)
	{
		if (!IsConfined(path, artifactRoot))
		{
			return (false, $"refused to delete '{path}': resolves outside the configured artifact root.");
		}

		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			return (true, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (false, $"'{Path.GetFileName(path)}': {exception.Message}");
		}
	}

	private static bool IsConfined(string path, string artifactRoot)
	{
		string fullPath = Path.GetFullPath(path);
		string fullRoot = Path.GetFullPath(artifactRoot);
		return fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
	}

	/// <summary>
	/// Resolves the target run whose <c>run_purges.artifact_job_id</c> is this purge
	/// job's own id -- the reverse lookup <see cref="IRunPurgeRepository"/> does not
	/// expose directly (it is keyed by target run id everywhere else, since every other
	/// caller already knows it). Implemented as a thin scan over
	/// <see cref="IRunPurgeRepository.GetStatusAsync"/> is not viable (no by-job-id
	/// index) -- see <see cref="IRunPurgeRepository.FindRunIdByArtifactJobIdAsync"/>.
	/// </summary>
	private Task<Guid?> FindRunIdForJobAsync(Guid purgeJobId, CancellationToken cancellationToken) =>
		_purges.FindRunIdByArtifactJobIdAsync(purgeJobId, cancellationToken);

	[LoggerMessage(Level = LogLevel.Warning, Message = "purge job {JobId} deleted {Deleted} artifact file(s) but failed on {Failed}: {Errors}")]
	private static partial void LogPartialFailure(ILogger logger, Guid jobId, int deleted, int failed, string errors);

	[LoggerMessage(Level = LogLevel.Warning, Message = "purge job {JobId} was cancelled after deleting {Deleted} artifact file(s); remaining files left in place")]
	private static partial void LogCancelled(ILogger logger, Guid jobId, int deleted);

	private sealed record PurgePayload(List<string>? JobIds);
}
