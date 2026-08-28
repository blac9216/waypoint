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
using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// The <c>content-pull</c> <see cref="JobShape.Simple"/> job handler (issue #40, ADR-0017:
/// compliance-runner executes content-pull/content-import). Reads the singleton
/// <c>compliance_content</c> config (repository/ref), clones or fetches the working
/// tree at <see cref="ComplianceContentOptions.ContentPath"/>, checks out the
/// configured ref, and replaces the <c>profiles</c> inventory with what it finds --
/// mirroring <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/>'s
/// "not payload-driven; everything needed is configuration or resolved at execution
/// time" shape. No credential is threaded through this slice (see the PowerShell
/// module's doc comment) -- a private/token-gated content source is out of scope.
///
/// Every attempt -- success or failure -- is recorded via
/// <see cref="IComplianceContentRepository.RecordPullAsync"/> so pull history
/// (issue #40 AC "who/when/commit") always reflects what actually happened, including
/// a failed run, rather than only successes.
///
/// Issue #729 (epic #726 Wave 1 remainder): a successful pull runs the validated
/// <see cref="VendorHierarchyInterpreter"/>/<see cref="SemanticImportReconciler"/>
/// pipeline over the checkout's content entries and promotes every accepted
/// executable-leaf candidate whose bounded <c>inspec check</c> genuinely ran and passed
/// into the migration 0050 catalog tables -- but issue #1016 (epic #726, owner decision
/// 2026-08-28) moved WHERE that check runs and WHERE that pipeline executes: this
/// handler's own job only performs phase 1 (git clone/fetch/checkout + directory
/// enumeration, ComplianceContentOptions.ContentSyncTimeout-bounded) and then fans out
/// one <c>content-check</c> job per chunk of discovered profiles onto its own run,
/// through the ordinary queue and ADR-0020's capacity pool, instead of running every
/// chunk's `inspec check` itself in one long-lived invocation. The semantic-import/
/// promotion pipeline, revision staging (issue #731), and pull-history recording all
/// still happen exactly as before -- just inside <c>ContentPullReconcileService</c>,
/// run once every fanned-out check job for this pull has reached a terminal state
/// (see that type's doc comment for the reconcile/partial-failure contract). This
/// handler's own job reports <see cref="JobOutcomeKind.Succeeded"/> once sync+fan-out
/// complete; that is deliberately NOT the same moment the pull as a whole is done.
/// </summary>
public sealed class ContentPullJobHandler : IJobHandler
{
	private const string SyncCommand = "Sync-WaypointComplianceContentTree";

	/// <summary>
	/// Issue #1016 (epic #726), owner decision 2026-08-28: priority for a fanned-out
	/// <c>content-check</c> chunk job. Carries no per-target work either (same
	/// reasoning as <c>ComplianceContentController.ContentPullPriority</c>) -- highest
	/// priority so a pull's checks are not starved behind unrelated scan/download
	/// traffic once the capacity pool has room for them.
	/// </summary>
	internal const short ContentCheckPriority = 1;

	private readonly IPowerShellExecutor _executor;
	private readonly IComplianceContentRepository _content;
	private readonly IProfileRepository _profiles;
	private readonly IJobRunnerRepository _jobs;
	private readonly IOptions<ComplianceContentOptions> _options;
	private readonly IContentPullCheckFanOutRepository _checkFanOut;

	public ContentPullJobHandler(
		IPowerShellExecutor executor,
		IComplianceContentRepository content,
		IProfileRepository profiles,
		IJobRunnerRepository jobs,
		IOptions<ComplianceContentOptions> options,
		IContentPullCheckFanOutRepository checkFanOut)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(checkFanOut);

		_executor = executor;
		_content = content;
		_profiles = profiles;
		_jobs = jobs;
		_options = options;
		_checkFanOut = checkFanOut;
	}

	public string JobType => "content-pull";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		if (config is null)
		{
			return JobExecutionOutcome.Failed("No compliance-content repository is configured (PUT /compliance-content first).");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		// Issue #993: phase 1 is a small, content-size-INDEPENDENT invocation (git
		// clone/fetch/checkout + directory enumeration, no `inspec check` at all) --
		// bounded by ComplianceContentOptions.ContentSyncTimeout, not the fixed
		// 00:30:00 PowerShellOptions.DefaultInvocationTimeout that used to also have to
		// cover every leaf's check on top of this.
		Dictionary<string, object?> syncParameters = new(StringComparer.Ordinal)
		{
			["RepositoryUrl"] = config.RepositoryUrl,
			["RefType"] = config.RefType,
			["RefValue"] = config.RefValue,
			["ContentPath"] = _options.Value.ContentPath,
		};

		PowerShellRequest syncRequest = new(
			SyncCommand, PowerShellRequestKind.Command, syncParameters, context.Job.Id, context.Job.RunId,
			Timeout: _options.Value.ContentSyncTimeout);
		PowerShellExecutionResult syncResult = await _executor.ExecuteAsync(syncRequest, cancellationToken).ConfigureAwait(false);

		if (!syncResult.Succeeded)
		{
			string note = syncResult.FailureReason ?? "content-pull sync invocation failed with no failure reason.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		(string? commit, IReadOnlyList<ProfileUpsert> discoveredProfiles, IReadOnlyList<ProfileDirectoryEntry> profileDirectories) =
			ParseSyncOutput(syncResult.Output, config);
		if (commit is null)
		{
			const string note = "content-pull sync invocation returned no commit.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		await _profiles.ReplaceAllAsync(discoveredProfiles, cancellationToken).ConfigureAwait(false);

		// Issue #1016 (epic #726), owner decision 2026-08-28: phase 2 no longer runs the
		// bounded per-leaf `inspec check` (issue #989's per-unit protection, unchanged)
		// itself. It instead fans out one 'content-check' job per CHUNK of
		// ComplianceContentOptions.ContentPullChunkSize leaves onto this job's OWN run,
		// through the ordinary queue -- the same capacity-pool-admitted parallelism
		// (ADR-0020) scan's component jobs already use, instead of one long-lived
		// invocation running every chunk sequentially in-process. A run with no
		// discovered profiles at all fans out zero check jobs and reconciles
		// immediately (ContentPullReconcileService below treats "zero expected, zero
		// pending" as trivially ready).
		if (context.Job.RunId is not Guid runId)
		{
			const string note = "content-pull job has no run id; cannot fan out check jobs.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		int chunkSize = Math.Max(1, _options.Value.ContentPullChunkSize);
		int expectedCheckJobCount = 0;
		for (int offset = 0; offset < profileDirectories.Count; offset += chunkSize)
		{
			cancellationToken.ThrowIfCancellationRequested();

			IReadOnlyList<ProfileDirectoryEntry> chunk = profileDirectories.Skip(offset).Take(chunkSize).ToList();
			JobSpec checkSpec = new("content-check", ContentCheckPriority, TargetName: "compliance-content-check");
			IReadOnlyList<Guid> checkJobIds = await _jobs
				.FanOutAdditionalJobsAsync(runId, [checkSpec], actor, cancellationToken)
				.ConfigureAwait(false);
			Guid checkJobId = checkJobIds[0];

			await _checkFanOut.RecordFanOutAsync(
				runId, context.Job.Id, checkJobId, commit,
				chunk.Select(p => new ContentCheckProfileDirectory(p.ProfileKey, p.ProfileDirectory)).ToList(),
				cancellationToken).ConfigureAwait(false);

			expectedCheckJobCount++;
		}

		string fanOutProgressPayload = JsonSerializer.Serialize(new
		{
			commit,
			profile_count = discoveredProfiles.Count,
			check_job_count = expectedCheckJobCount,
		});
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, fanOutProgressPayload, cancellationToken)
			.ConfigureAwait(false);

		// This job's own row now reports Succeeded ("sync + fan-out completed"), but the
		// PULL as a whole (pull history, semantic import/promotion, revision staging) is
		// deliberately NOT recorded as complete here -- ContentPullReconcileService (API
		// process, mirrors RunPurgeFinalizeHostedService) performs that once every
		// fanned-out content-check job for this pull has reached a terminal state,
		// exactly the same atomic staging/promotion RunSemanticImportAsync always
		// performed, just moved to run once the check phase's parallel jobs finish
		// instead of inline at the end of one long-lived invocation. A reader of pull
		// history sees no new row until reconcile actually lands one -- the previous
		// pull's history stays the honest "last known" state during the fan-out window,
		// same "no partial success recorded" discipline the old single-pass code had.
		return JobExecutionOutcome.Succeeded(
			$"Pulled '{config.RefValue}' at {commit}; {discoveredProfiles.Count} profile(s) found; "
			+ $"{expectedCheckJobCount} check job(s) fanned out; awaiting reconcile.");
	}

	/// <summary>
	/// One profile directory discovered by the phase-1 sync call: <see cref="ProfileKey"/>
	/// is issue #617's content-root-relative identity, <see cref="ProfileDirectory"/> is
	/// the real absolute directory phase 2's chunked
	/// <c>Get-WaypointComplianceContentEntries</c> calls need to run the bounded
	/// <c>inspec check</c> and read manifest/control files -- carried across the two
	/// invocations explicitly (rather than recomputed) because it is the sync call's own
	/// filesystem walk that discovered it.
	/// </summary>
	private sealed record ProfileDirectoryEntry(string ProfileKey, string ProfileDirectory);

	/// <summary>
	/// Every profile discovered by a successful pull is labeled
	/// <see cref="ProfileStates.Pinned"/> when the config tracks a tag,
	/// <see cref="ProfileStates.Current"/> when it tracks a branch (issue #40 AC
	/// "current / update pending / pinned"). <see cref="ProfileStates.UpdatePending"/>
	/// is not produced by this handler -- it describes a profile whose recorded commit
	/// predates the latest available upstream commit, a comparison this slice's
	/// GET /compliance-content/check (part of the same PR's API surface) computes by
	/// diffing against upstream without mutating stored rows.
	///
	/// Issue #993: this now parses <c>Sync-WaypointComplianceContentTree</c>'s output
	/// only (Commit + Profiles, no ContentEntries/Controls -- those come from phase 2's
	/// chunked <c>Get-WaypointComplianceContentEntries</c> calls instead). Each parsed
	/// profile's real directory is returned alongside its <see cref="ProfileUpsert"/> so
	/// the caller can drive phase 2 without recomputing paths from profile_key.
	/// </summary>
	private static (string? Commit, IReadOnlyList<ProfileUpsert> Profiles, IReadOnlyList<ProfileDirectoryEntry> ProfileDirectories)
		ParseSyncOutput(IReadOnlyList<object?> output, ComplianceContentConfig config)
	{
		string state = config.RefType == ComplianceContentRefTypes.Tag ? ProfileStates.Pinned : ProfileStates.Current;

		foreach (object? item in output)
		{
			if (item is not System.Management.Automation.PSObject psObject)
			{
				continue;
			}

			string? commit = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Commit"]?.Value);
			if (string.IsNullOrWhiteSpace(commit))
			{
				continue;
			}

			List<ProfileUpsert> profiles = [];
			List<ProfileDirectoryEntry> directories = [];
			foreach (object? rawProfile in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["Profiles"]?.Value))
			{
				ProfileUpsert? parsed = TryParseProfile(rawProfile, commit, state);
				if (parsed is null)
				{
					continue;
				}

				profiles.Add(parsed);

				if (rawProfile is System.Management.Automation.PSObject profileObject)
				{
					string? profileDirectory = PowerShellValueUnwrap.UnwrapAs<string>(profileObject.Properties["_ProfileDirectory"]?.Value);
					if (!string.IsNullOrWhiteSpace(profileDirectory))
					{
						directories.Add(new ProfileDirectoryEntry(parsed.ProfileKey, profileDirectory));
					}
				}
			}

			return (commit, profiles, directories);
		}

		return (null, [], []);
	}

	private static ProfileUpsert? TryParseProfile(object? item, string commit, string state)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? profileKey = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["ProfileKey"]?.Value);
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			// One malformed profile row must not fail the whole pull -- same
			// "individual failures don't halt the batch" principle
			// CatalogIndexJobHandler.TryParseArtifact applies.
			return null;
		}

		string? name = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Name"]?.Value);
		string? version = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Version"]?.Value);
		return new ProfileUpsert(profileKey, string.IsNullOrWhiteSpace(name) ? profileKey : name, version, commit, state);
	}

	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}
}
