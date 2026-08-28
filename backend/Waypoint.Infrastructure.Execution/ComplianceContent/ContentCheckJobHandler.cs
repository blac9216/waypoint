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

using System.Linq;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// The <c>content-check</c> <see cref="JobShape.Simple"/> job handler (issue #1016,
/// epic #726, owner decision 2026-08-28: reuse the job-queue's existing parallelism
/// instead of in-process concurrency). Runs the bounded per-leaf <c>inspec check</c>
/// pass (issue #989's per-unit protection, unchanged) for exactly the chunk of profile
/// directories <see cref="ContentPullJobHandler"/> recorded for this job in
/// <c>content_pull_checks</c> (migration 0073) -- the same
/// <c>Get-WaypointComplianceContentEntries</c> PowerShell call the pre-#1016 handler
/// made directly, one invocation per chunk, unchanged shape and timeout math
/// (<c>ContentPullChunkSize x InspecCheckTimeoutSeconds + ContentPullChunkOverheadSeconds</c>).
///
/// This handler never stages/promotes anything -- it durably records each profile's
/// parsed content plus its honest check outcome (<c>content_pull_check_results</c>) and
/// replaces that profile's <c>profile_controls</c> rows (same write the pre-#1016
/// single-pass handler made, just scoped to this job's own chunk instead of the whole
/// pull). <see cref="ContentPullReconcileService"/> reads every sibling chunk job's
/// results back once they are all terminal and performs the actual semantic-import/
/// promotion/staging pass -- see that type's doc comment for the reconcile contract,
/// including how a chunk that fails outright (this handler returns
/// <see cref="JobExecutionOutcome.Failed"/>) is handled: its profiles simply have no
/// recorded result row, which reconcile's existing fail-closed rule already quarantines
/// with an honest "check did not run" reason -- no separate failure-handling path is
/// needed here.
/// </summary>
public sealed class ContentCheckJobHandler : IJobHandler
{
	private const string EntriesCommand = "Get-WaypointComplianceContentEntries";

	private readonly IPowerShellExecutor _executor;
	private readonly IContentPullCheckFanOutRepository _checkFanOut;
	private readonly IProfileRepository _profiles;
	private readonly IProfileControlRepository _profileControls;
	private readonly IOptions<ComplianceContentOptions> _options;

	public ContentCheckJobHandler(
		IPowerShellExecutor executor,
		IContentPullCheckFanOutRepository checkFanOut,
		IProfileRepository profiles,
		IProfileControlRepository profileControls,
		IOptions<ComplianceContentOptions> options)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(checkFanOut);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(profileControls);
		ArgumentNullException.ThrowIfNull(options);

		_executor = executor;
		_checkFanOut = checkFanOut;
		_profiles = profiles;
		_profileControls = profileControls;
		_options = options;
	}

	public string JobType => "content-check";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		ContentPullCheckFanOut? fanOut = await _checkFanOut
			.GetFanOutForCheckJobAsync(context.Job.Id, cancellationToken).ConfigureAwait(false);
		if (fanOut is null)
		{
			return JobExecutionOutcome.Failed(
				$"No content_pull_checks row recorded for content-check job {context.Job.Id} -- cannot determine its profile chunk.");
		}

		int chunkSize = Math.Max(1, fanOut.ProfileDirectories.Count);
		TimeSpan chunkTimeout = TimeSpan.FromSeconds(
			(chunkSize * (double)_options.Value.InspecCheckTimeoutSeconds) + _options.Value.ContentPullChunkOverheadSeconds);

		Dictionary<string, object?> profileKeysByDirectory = new(StringComparer.Ordinal);
		foreach (ContentCheckProfileDirectory entry in fanOut.ProfileDirectories)
		{
			profileKeysByDirectory[entry.ProfileDirectory] = entry.ProfileKey;
		}

		Dictionary<string, object?> entriesParameters = new(StringComparer.Ordinal)
		{
			["ProfileDirectories"] = fanOut.ProfileDirectories.Select(p => p.ProfileDirectory).ToArray(),
			["ProfileKeysByDirectory"] = profileKeysByDirectory,
			["InspecCheckTimeoutSeconds"] = _options.Value.InspecCheckTimeoutSeconds,
		};

		PowerShellRequest entriesRequest = new(
			EntriesCommand, PowerShellRequestKind.Command, entriesParameters, context.Job.Id, context.Job.RunId,
			Timeout: chunkTimeout);
		PowerShellExecutionResult entriesResult = await _executor.ExecuteAsync(entriesRequest, cancellationToken).ConfigureAwait(false);

		if (!entriesResult.Succeeded)
		{
			// Issue #993 AC 3 (unchanged): a genuine overrun is an honest, actionable job
			// failure. Nothing has been recorded for this chunk's profiles -- reconcile's
			// fail-closed rule quarantines them with "check did not run" once every
			// sibling job for this pull is terminal, same discipline as before #1016.
			return JobExecutionOutcome.Failed(
				entriesResult.FailureReason ?? "content-check entries invocation failed with no failure reason.");
		}

		// A profile referenced by this chunk was already upserted into `profiles` by
		// ContentPullJobHandler's phase-1 sync (which ran before any content-check job
		// was fanned out), so its surrogate id always exists by the time this job claims
		// and runs -- read the inventory once, not once per entry.
		Dictionary<string, Guid> profileIdsByKey = (await _profiles.ListAsync(cancellationToken).ConfigureAwait(false))
			.ToDictionary(p => p.ProfileKey, p => p.Id, StringComparer.Ordinal);

		foreach (object? item in entriesResult.Output)
		{
			VendorContentEntry? entry = ContentCheckEntryParser.TryParseContentEntry(item);
			if (entry is null)
			{
				continue;
			}

			await _checkFanOut.RecordCheckResultAsync(
				context.Job.Id,
				new ContentCheckResultRecord(
					entry.ProfileKey, entry.RawYaml, entry.HasControlsDirectory, entry.HasFilesDirectory, entry.ControlFileNames,
					entry.InspecCheckRan, entry.InspecCheckPassed, entry.InspecCheckDetail),
				cancellationToken).ConfigureAwait(false);

			// Same profile_controls write the pre-#1016 single-pass handler made --
			// scoped to this job's own chunk.
			if (profileIdsByKey.TryGetValue(entry.ProfileKey, out Guid profileId))
			{
				List<ProfileControlUpsert> controls = ContentCheckEntryParser.TryParseControls(item);
				await _profileControls.ReplaceForProfileAsync(profileId, controls, cancellationToken).ConfigureAwait(false);
			}
		}

		return JobExecutionOutcome.Succeeded(
			$"Checked {fanOut.ProfileDirectories.Count} profile(s) for content-pull job {fanOut.ContentPullJobId}.");
	}
}
