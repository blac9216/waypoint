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
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Wires HDF parsing + component-result persistence (migration 0063, issue #745) into
/// <c>ScanJobHandler</c>'s completion path -- ADDITIVE only, per the issue's own scope
/// note: existing file-based results (<c>RunArtifactProjectionService</c>,
/// <c>HdfSeverityCounter</c>) are untouched this slice; this service only writes a
/// second, domain-owned evidence trail alongside them.
///
/// A job whose claim carries no <see cref="ClaimedJob.ScanPlanItemId"/> (the legacy
/// target-granular fan-out path, or any non-scan job type) is a deliberate no-op --
/// there is no scan_plan_item to key a component_results row to, and widening this to
/// legacy jobs is explicitly out of scope for this slice (ADR-0024's component-job
/// layer is what created scan_plan_item_id in the first place).
///
/// This never throws out of <see cref="RecordAsync"/> and never fails the scan run --
/// same "recording evidence must not be allowed to break execution" discipline as
/// <c>ScanUploadCoordinator.UploadAsync</c>. A recording failure is logged and
/// swallowed; the job's own outcome (succeeded/failed) is decided entirely by
/// <c>ScanJobHandler</c> before and after this call.
/// </summary>
public sealed partial class ComponentResultRecordingService
{
	private readonly IComponentResultRepository _results;
	private readonly ILogger<ComponentResultRecordingService> _logger;

	public ComponentResultRecordingService(IComponentResultRepository results, ILogger<ComponentResultRecordingService> logger)
	{
		ArgumentNullException.ThrowIfNull(results);
		ArgumentNullException.ThrowIfNull(logger);
		_results = results;
		_logger = logger;
	}

	/// <summary>
	/// Records a successful (HDF-parseable) attempt. <paramref name="hdfPath"/> is the
	/// HDF file this job's pipeline actually produced (attested when present, else
	/// raw -- the same "best available" choice <see cref="Waypoint.Core.Scans.ScanArtifactPaths.ResolveHdf"/>
	/// makes); a malformed/unreadable HDF is recorded as a single
	/// <see cref="ComponentResultStatuses.ExecutionError"/> result with exactly one
	/// synthetic <see cref="ComponentFindingStatuses.NotReviewed"/> finding (epic #726
	/// §6's exactly-once rule at the whole-component level: a component that could not
	/// be evaluated at all must still appear, never be silently absent from the run's
	/// evidence graph).
	/// </summary>
	public async Task RecordCompletedAsync(
		ClaimedJob job,
		string? hdfPath,
		string? attestedHdfPath,
		string? cklPath,
		CancellationToken cancellationToken)
	{
		if (job.RunId is null || job.ScanPlanItemId is not { } scanPlanItemId)
		{
			return;
		}

		try
		{
			Guid? componentId = await _results.GetComponentIdForPlanItemAsync(scanPlanItemId, cancellationToken).ConfigureAwait(false);
			if (componentId is null)
			{
				LogPlanItemMissing(job.Id, scanPlanItemId);
				return;
			}

			int attemptNumber = await _results.NextAttemptNumberAsync(job.Id, cancellationToken).ConfigureAwait(false);
			List<ComponentResultArtifact> artifacts = [];

			string? bestHdfPath = attestedHdfPath is { } attested && File.Exists(attested) ? attested : hdfPath;
			HdfParseResult parseResult = bestHdfPath is { } path && File.Exists(path)
				? HdfFindingsParser.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
				: HdfParseResult.Rejected("no HDF report was produced for this attempt.");

			IReadOnlyList<ComponentResultFinding> findings = parseResult.Success
				? parseResult.Findings
				: [new ComponentResultFinding("component", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, parseResult.RejectionReason)];
			string status = parseResult.Success ? ComponentResultStatuses.Completed : ComponentResultStatuses.ExecutionError;

			if (hdfPath is { } rawFile && File.Exists(rawFile))
			{
				artifacts.Add(await BuildArtifactAsync(ComponentResultArtifactKinds.HdfRaw, rawFile, cancellationToken).ConfigureAwait(false));
			}

			if (attestedHdfPath is { } attestedFile && File.Exists(attestedFile))
			{
				artifacts.Add(await BuildArtifactAsync(ComponentResultArtifactKinds.HdfAttested, attestedFile, cancellationToken).ConfigureAwait(false));
			}

			if (cklPath is { } ckl && File.Exists(ckl))
			{
				artifacts.Add(await BuildArtifactAsync(ComponentResultArtifactKinds.Ckl, ckl, cancellationToken).ConfigureAwait(false));
			}

			ComponentResultRecord record = new(
				RunId: job.RunId.Value,
				JobId: job.Id,
				ScanPlanItemId: scanPlanItemId,
				ComponentId: componentId.Value,
				AttemptNumber: attemptNumber,
				Status: status,
				Detail: parseResult.Success ? null : parseResult.RejectionReason,
				Findings: findings,
				Artifacts: artifacts);

			await _results.RecordAsync(record, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or NpgsqlException)
		{
			LogRecordingFailed(job.Id, ex);
		}
	}

	/// <summary>
	/// Records a component that never executed at all (auth failure, target
	/// unreachable, missing credential -- any <c>FailScanAsync</c>/skip path). Epic
	/// #726 §6: present exactly once as <see cref="ComponentFindingStatuses.NotReviewed"/>,
	/// never omitted.
	/// </summary>
	public async Task RecordExecutionErrorAsync(ClaimedJob job, string sanitizedDetail, CancellationToken cancellationToken)
	{
		if (job.RunId is null || job.ScanPlanItemId is not { } scanPlanItemId)
		{
			return;
		}

		try
		{
			Guid? componentId = await _results.GetComponentIdForPlanItemAsync(scanPlanItemId, cancellationToken).ConfigureAwait(false);
			if (componentId is null)
			{
				LogPlanItemMissing(job.Id, scanPlanItemId);
				return;
			}

			int attemptNumber = await _results.NextAttemptNumberAsync(job.Id, cancellationToken).ConfigureAwait(false);
			ComponentResultRecord record = new(
				RunId: job.RunId.Value,
				JobId: job.Id,
				ScanPlanItemId: scanPlanItemId,
				ComponentId: componentId.Value,
				AttemptNumber: attemptNumber,
				Status: ComponentResultStatuses.ExecutionError,
				Detail: sanitizedDetail,
				Findings: [new ComponentResultFinding("component", null, null, ComponentFindingSeverities.CatIII, ComponentFindingStatuses.NotReviewed, sanitizedDetail)],
				Artifacts: []);

			await _results.RecordAsync(record, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or NpgsqlException)
		{
			LogRecordingFailed(job.Id, ex);
		}
	}

	private static async Task<ComponentResultArtifact> BuildArtifactAsync(string kind, string path, CancellationToken cancellationToken)
	{
		byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		return new ComponentResultArtifact(kind, Path.GetFileName(path), digest, bytes.LongLength);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to record component result for job {JobId} -- scan outcome is unaffected.")]
	private partial void LogRecordingFailed(Guid jobId, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} names scan_plan_item {ScanPlanItemId} which no longer resolves to a component -- result not recorded.")]
	private partial void LogPlanItemMissing(Guid jobId, Guid scanPlanItemId);
}
