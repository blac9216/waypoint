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

using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>One projected artifact row for a run's scan job, control-plane shaped (no
/// wire/JSON attributes -- <c>RunsController</c> maps this to <c>RunArtifactResponse</c>).</summary>
public sealed record RunArtifactRow(
	Guid JobId,
	string Target,
	string? Benchmark,
	bool CountsAvailable,
	int? CatIOpen,
	int? CatIIOpen,
	int? CatIIIOpen,
	/// <summary>Issue #1132: total controls this HDF describes, null exactly when <see cref="CountsAvailable"/> is false.</summary>
	int? ControlsTotal,
	/// <summary>Issue #1132: controls that produced a real pass/fail outcome -- see <see cref="Waypoint.Core.Scans.HdfSeverityCounts.NoControlsEvaluated"/>. A Results-table reader must check this, not just the CAT counts, before reading a row as clean.</summary>
	int? ControlsEvaluated,
	/// <summary>Issue #1144: controls <see cref="Waypoint.Core.Scans.HdfControlClassifier"/> classifies as <c>execution_error</c> -- an <c>error</c> result, an unrecognized status string, or an unrecognized mixed result shape -- null exactly when <see cref="CountsAvailable"/> is false. Not folded into the CAT open counts: such a control never produced a genuine compliance verdict, matching <see cref="Waypoint.Core.Scans.ComponentFindingStatuses.IsOpen"/>'s <c>failed</c>-only definition. The persisted-findings surface uses the SAME shared classifier, so the two agree by construction rather than by parallel maintenance.</summary>
	int? ControlsExecutionError,
	IReadOnlyList<string> ArtifactKinds,
	string UploadStatus,
	string? UploadDetail);

/// <summary>
/// Issue #414: control-plane projection of a run's per-target scan artifacts
/// (docs/api-contract.md `/runs/{id}/artifacts`, issue #299), extracted out of
/// <see cref="Waypoint.Api.Controllers.RunsController"/>. Reads only job rows
/// (<see cref="IJobControlRepository"/>) and filesystem artifact presence -- no
/// claim/lease/execution responsibility.
/// </summary>
public sealed class RunArtifactProjectionService
{
	private const string ScanJobType = "scan";
	private const string UnknownTargetLabel = "unknown";

	private readonly IJobControlRepository _repository;
	private readonly TargetRepository _targets;
	private readonly IOptions<ScanOptions> _scanOptions;

	public RunArtifactProjectionService(
		IJobControlRepository repository, TargetRepository targets, IOptions<ScanOptions> scanOptions)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(scanOptions);
		_repository = repository;
		_targets = targets;
		_scanOptions = scanOptions;
	}

	/// <summary>
	/// Builds one <see cref="RunArtifactRow"/> per <c>scan</c> job in <paramref name="runId"/>
	/// -- other job types in the run are simply absent from the list, not represented as
	/// an empty/zeroed row (see <see cref="BuildArtifactRowAsync"/> for per-row detail).
	/// </summary>
	public async Task<IReadOnlyList<RunArtifactRow>> GetArtifactsAsync(Guid runId, CancellationToken cancellationToken)
	{
		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(runId, cancellationToken).ConfigureAwait(false);
		List<RunArtifactRow> rows = [];
		foreach (JobSummary job in jobs)
		{
			if (!string.Equals(job.JobType, ScanJobType, StringComparison.Ordinal))
			{
				continue;
			}

			rows.Add(await BuildArtifactRowAsync(job, cancellationToken).ConfigureAwait(false));
		}

		return rows;
	}

	/// <summary>
	/// Builds one <see cref="RunArtifactRow"/> for a <c>scan</c> job: resolves the
	/// target's kind to look up <see cref="ScanOptions.BenchmarkMetadata"/> for a
	/// benchmark label, checks the artifact store for whichever of the HDF/CKL files
	/// currently exist, and parses CAT I/II/III counts from the HDF when present.
	/// </summary>
	private async Task<RunArtifactRow> BuildArtifactRowAsync(JobSummary job, CancellationToken cancellationToken)
	{
		string artifactStorePath = _scanOptions.Value.ArtifactStorePath;
		string? hdfPath = ScanArtifactPaths.ResolveHdf(artifactStorePath, job.Id);
		string cklPath = ScanArtifactPaths.Ckl(artifactStorePath, job.Id);

		List<string> kinds = [];
		if (hdfPath is not null)
		{
			kinds.Add("hdf");
		}

		if (File.Exists(cklPath))
		{
			kinds.Add("ckl");
		}

		// null == uncountable (HDF absent, or present-but-unparseable): the wire must NOT
		// present that as CAT I/II/III = 0 (issue #299 round-1 blocker). counts_available
		// gates the counts; a corrupt HDF reads as "could not count", never as compliant.
		HdfSeverityCounts? counts = HdfSeverityCounter.CountOpenFindings(hdfPath);

		string? benchmark = null;
		if (job.TargetId is { } targetIdText && Guid.TryParse(targetIdText, out Guid targetId))
		{
			Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
			if (target is not null && _scanOptions.Value.BenchmarkMetadata.TryGetValue(target.Kind, out ScanBenchmarkMetadata? metadata))
			{
				benchmark = metadata.Title ?? metadata.BenchmarkId;
			}
		}

		return new RunArtifactRow(
			JobId: job.Id,
			Target: job.TargetName ?? job.TargetId ?? UnknownTargetLabel,
			Benchmark: benchmark,
			CountsAvailable: counts is not null,
			CatIOpen: counts?.CatIOpen,
			CatIIOpen: counts?.CatIIOpen,
			CatIIIOpen: counts?.CatIIIOpen,
			ControlsTotal: counts?.ControlsTotal,
			ControlsEvaluated: counts?.ControlsEvaluated,
			ControlsExecutionError: counts?.ControlsExecutionError,
			ArtifactKinds: kinds,
			UploadStatus: StigManagerUploadStatus(job),
			UploadDetail: job.UploadStatus is null ? null : job.UploadDetail);
	}

	/// <summary>
	/// STIG Manager upload status (issue #311): <c>job.UploadStatus</c> (<c>jobs.upload_status</c>)
	/// is the real HTTP outcome once the convert stage has attempted an upload -- used
	/// as-is when present. A null column (SRG-only run, or a job that never reached
	/// convert) falls back to the original issue #299 job-state derivation so an
	/// unattempted upload still reads as "pending"/"not-uploaded" rather than a missing
	/// field.
	/// </summary>
	private static string StigManagerUploadStatus(JobSummary job)
	{
		if (job.UploadStatus is { } uploadStatus)
		{
			return uploadStatus switch
			{
				JobUploadStatuses.Uploaded => "uploaded",
				JobUploadStatuses.Conflict => "conflict",
				JobUploadStatuses.Pending => "pending",
				_ => "not-uploaded",
			};
		}

		return job.State switch
		{
			JobStates.Uploaded or JobStates.Done => "uploaded",
			JobStates.Failed or JobStates.AuthFailed => "not-uploaded",
			_ => "pending",
		};
	}
}
