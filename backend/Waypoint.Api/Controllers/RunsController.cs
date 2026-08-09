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
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md runs &amp; jobs surface: create runs, inspect run state and
/// per-job detail, and pause/resume/abort.
/// </summary>
[ApiController]
[Route("api/v1/runs")]
public sealed class RunsController : ControllerBase
{
	private const string RemediateRunType = "remediate";
	private const string RemediateConfirmation = "REMEDIATE";
	private const string ScanRunType = "scan";
	private const string ScanJobType = "scan";
	private const string PersonalCredentialKind = "personal";

	private const string ScanArtifactHdfKind = "hdf";
	private const string ScanArtifactCklKind = "ckl";
	private const string UnknownTargetLabel = "unknown";

	private readonly IJobQueueRepository _repository;
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly IEphemeralCredentialCache _ephemeralCredentials;
	private readonly ConfigDocRepository _configDocs;
	private readonly AttestationSnapshotRepository _attestationSnapshots;
	private readonly IOptions<ScanOptions> _scanOptions;

	public RunsController(
		IJobQueueRepository repository,
		SiteRepository sites,
		TargetRepository targets,
		IEphemeralCredentialCache ephemeralCredentials,
		ConfigDocRepository configDocs,
		AttestationSnapshotRepository attestationSnapshots,
		IOptions<ScanOptions> scanOptions)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(ephemeralCredentials);
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(attestationSnapshots);
		ArgumentNullException.ThrowIfNull(scanOptions);
		_repository = repository;
		_sites = sites;
		_targets = targets;
		_ephemeralCredentials = ephemeralCredentials;
		_configDocs = configDocs;
		_attestationSnapshots = attestationSnapshots;
		_scanOptions = scanOptions;
	}

	/// <summary>
	/// In-action role check for gates finer than the endpoint's <c>Require*Role</c>
	/// attribute (which fixes the floor, not per-run-type requirements).
	/// </summary>
	private bool CallerHasAtLeast(WaypointRole minimum) =>
		Enum.TryParse(User.FindFirstValue(WaypointClaimTypes.Role), out WaypointRole role) && role >= minimum;

	/// <summary>
	/// Enforces docs/api-contract.md's pause/resume/abort scope: "Operator+ (own
	/// runs), Admin any." Admin bypasses the check entirely. A non-Admin caller must
	/// match the run's recorded initiator; a run with no recorded initiator
	/// (<see cref="RunQueueState.InitiatedBy"/> null — system/scheduled run) is
	/// Admin-only, since there is no owner to compare against.
	///
	/// Shared with <see cref="JobsController"/>'s per-job cancel (issue #294), which
	/// applies the same "own runs, Admin any" scope to the run owning the job being
	/// cancelled -- hence <c>internal</c> rather than <c>private</c>.
	/// </summary>
	internal static void EnforceRunOwnership(ClaimsPrincipal user, RunQueueState state)
	{
		if (Enum.TryParse(user.FindFirstValue(WaypointClaimTypes.Role), out WaypointRole role) && role >= WaypointRole.Admin)
		{
			return;
		}

		if (state.InitiatedBy is null)
		{
			throw ApiException.Forbidden(
				"This run has no recorded initiator.",
				"Runs with no recorded initiator (system/scheduled runs) may only be paused, resumed, or aborted by an Admin.");
		}

		string caller = user.GetRequiredUsername();
		if (!string.Equals(caller, state.InitiatedBy, StringComparison.Ordinal))
		{
			throw ApiException.Forbidden(
				"You do not own this run.",
				"Only the run's initiator or an Admin may pause, resume, or abort it.");
		}
	}

	private void EnforceRunOwnership(RunQueueState state) => EnforceRunOwnership(User, state);

	/// <summary>
	/// Create a new run. Cyber+ for scan runs; remediation requires Admin plus the
	/// explicit <c>confirmation: "REMEDIATE"</c> body field — remediation is never
	/// implicit (docs/api-contract.md `/runs`, CLAUDE.md key constraints). The
	/// initiator is recorded from the authenticated identity, never from the body.
	/// A <c>scan</c> run additionally validates its scope and fans out one <c>scan</c>
	/// job per target (issue #273) before the run is created; every other run type
	/// keeps the pre-#273 behavior of passing <c>scope</c> through uninterpreted (no
	/// job rows created here -- their initiators, e.g. <c>DownloadsController</c>, own
	/// their own fan-out). <c>credential</c> (ADR-0011 ad hoc flow, issue #276) requires
	/// Operator+ on top of the Cyber+ floor and only applies to scan runs -- see
	/// <see cref="ValidateEphemeralCredentialRequest"/>.
	/// </summary>
	[HttpPost]
	[RequireCyberRole]
	[ProducesResponseType(typeof(RunCreatedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<RunCreatedResponse>> CreateRun(
		RunCreateRequest request,
		CancellationToken cancellationToken)
	{
		if (string.Equals(request.RunType, RemediateRunType, StringComparison.Ordinal))
		{
			if (!CallerHasAtLeast(WaypointRole.Admin))
			{
				throw ApiException.Forbidden(
					"Remediation runs require the Admin role.",
					"Only an Admin may create a run with run_type 'remediate'.");
			}

			if (!string.Equals(request.Confirmation, RemediateConfirmation, StringComparison.Ordinal))
			{
				throw ApiException.Validation(
					"Remediation requires explicit confirmation.",
					$"Set \"confirmation\": \"{RemediateConfirmation}\" in the request body to create a remediation run.");
			}
		}

		ValidateEphemeralCredentialRequest(request);

		string initiatedBy = User.GetRequiredUsername();

		if (string.Equals(request.RunType, ScanRunType, StringComparison.Ordinal))
		{
			return Accepted(await CreateScanRunAsync(request, initiatedBy, cancellationToken).ConfigureAwait(false));
		}

		Guid runId = await _repository.CreateRunAsync(
			request.RunType, request.Scope, request.CredentialId, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

		return Accepted(new RunCreatedResponse(runId.ToString()));
	}

	/// <summary>
	/// Gates the ADR-0011 ad hoc "my credentials" body: Operator+ (the role nuance is
	/// "Cyber starts scans with SERVICE credentials; ad hoc PERSONAL credentials are the
	/// Operator tier" -- docs/domain-model.md), scan runs only (personal credentials
	/// have no scheduling or remediation use -- ADR-0011 "scheduling always uses service
	/// credentials"), mutually exclusive with <c>credential_id</c>, and only the
	/// <c>"personal"</c> kind v1 defines.
	/// </summary>
	private void ValidateEphemeralCredentialRequest(RunCreateRequest request)
	{
		if (request.Credential is null)
		{
			return;
		}

		if (!CallerHasAtLeast(WaypointRole.Operator))
		{
			throw ApiException.Forbidden(
				"Ad hoc credentials require the Operator role.",
				"Only Operator+ may start a run with an inline \"credential\" (ADR-0011 personal tier); use a stored credential_id, or ask an Operator to start this run.");
		}

		if (!string.Equals(request.RunType, ScanRunType, StringComparison.Ordinal))
		{
			throw ApiException.Validation(
				"Ad hoc credentials are only valid for scan runs.",
				$"\"credential\" may only be set when \"run_type\" is \"{ScanRunType}\".");
		}

		if (request.CredentialId is not null)
		{
			throw ApiException.Validation(
				"credential_id and credential are mutually exclusive.",
				"Set either a stored \"credential_id\" or an inline \"credential\", never both.");
		}

		if (!string.Equals(request.Credential.Kind, PersonalCredentialKind, StringComparison.Ordinal))
		{
			throw ApiException.Validation(
				"credential.kind is not supported.",
				$"\"credential.kind\" must be \"{PersonalCredentialKind}\".");
		}

		if (string.IsNullOrWhiteSpace(request.Credential.Username) || string.IsNullOrWhiteSpace(request.Credential.Secret))
		{
			throw ApiException.Validation(
				"credential requires both username and secret.",
				"Set non-empty \"credential.username\" and \"credential.secret\" in the request body.");
		}
	}

	/// <summary>
	/// Validates a scan run's <c>scope</c> (site + optional target selection, all must
	/// resolve to existing rows -- docs/api-contract.md `/runs`: "POST body: site_id,
	/// scope... credential"), then creates the run and fans out one <c>scan</c>
	/// <see cref="JobSpec"/> per target, ordered by <see cref="ScanTargetPriority"/>.
	/// Every target's job is created up front in one <see cref="IJobQueueRepository.FanOutJobsAsync"/>
	/// call -- an individual target's later execution failure cannot affect its
	/// siblings (ADR-0008 Continue policy; each is an independent job row) -- but
	/// validation happens entirely before that call so a bad site/target/credential
	/// reference never leaves a partially created run.
	/// </summary>
	private async Task<RunCreatedResponse> CreateScanRunAsync(
		RunCreateRequest request, string initiatedBy, CancellationToken cancellationToken)
	{
		ScanScope scope;
		try
		{
			scope = ScanScopeParser.Parse(request.Scope);
		}
		catch (FormatException exception)
		{
			throw ApiException.Validation("scope is not valid.", exception.Message);
		}

		if (scope.SiteId is not { } siteId)
		{
			throw ApiException.Validation(
				"scope.site_id is required for a scan run.",
				"Set \"scope\": { \"site_id\": \"<uuid>\" } (optionally with \"target_ids\") in the request body.");
		}

		Site? site = await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
		if (site is null)
		{
			throw ApiException.NotFound("Site not found.", $"Site '{siteId}' does not exist.");
		}

		IReadOnlyList<Target> targets = await ResolveScanTargetsAsync(siteId, scope.TargetIds, cancellationToken).ConfigureAwait(false);
		if (targets.Count == 0)
		{
			throw ApiException.Validation(
				"Site has no targets to scan.",
				$"Site '{siteId}' has no targets; add at least one before starting a scan.");
		}

		bool useEphemeralCredential = request.Credential is not null;

		List<JobSpec> specs = [];
		foreach (Target target in targets)
		{
			// target_kind is the shape-routing signal JobShapes.ForJob reads (issue #309):
			// every scan fans out as job_type = 'scan' regardless of kind, so the payload
			// is the only place the dispatcher can learn "this is an ssh (SRG) target"
			// before a handler ever resolves the target row.
			string payload = JsonSerializer.Serialize(new { target_id = target.Id, site_id = siteId, target_kind = target.Kind });
			if (useEphemeralCredential)
			{
				// No credential_id at all for an ad hoc job -- the secret lives only in
				// IEphemeralCredentialCache, keyed below by the job id FanOutJobsAsync
				// assigns. Falling back to target.CredentialId here would silently mix
				// tiers (a "my credentials" run quietly using a stored service secret).
				specs.Add(new JobSpec(
					ScanJobType,
					ScanTargetPriority.ForTargetKind(target.Kind),
					TargetId: target.Id,
					TargetName: target.Name,
					Payload: payload,
					HasEphemeralCredential: true));
			}
			else
			{
				Guid? credentialId = request.CredentialId ?? target.CredentialId;
				specs.Add(new JobSpec(
					ScanJobType,
					ScanTargetPriority.ForTargetKind(target.Kind),
					TargetId: target.Id,
					TargetName: target.Name,
					CredentialId: credentialId,
					Payload: payload));
			}
		}

		Guid runId = await _repository.CreateRunAsync(
			request.RunType, request.Scope, request.CredentialId, initiatedBy, cancellationToken)
			.ConfigureAwait(false);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		if (useEphemeralCredential)
		{
			// jobIds is returned in the same order as specs (JobQueueRepository.FanOutJobsAsync):
			// zip rather than re-resolve so each target's ad hoc secret lands on exactly
			// its own job's cache entry, never a sibling's.
			EphemeralCredential credential = new(request.Credential!.Username, request.Credential.Secret);
			for (int i = 0; i < jobIds.Count; i++)
			{
				_ephemeralCredentials.Put(jobIds[i], runId, credential, initiatedBy);
			}
		}

		return new RunCreatedResponse(runId.ToString());
	}

	/// <summary>
	/// Resolves the scan's target set: every target under the site when
	/// <paramref name="requestedIds"/> is null/empty (a full-site scan), or exactly the
	/// requested ids -- each of which must belong to <paramref name="siteId"/>, so a
	/// target id from a different site is a clean 404 rather than silently scanning
	/// the wrong site's target.
	/// </summary>
	private async Task<IReadOnlyList<Target>> ResolveScanTargetsAsync(
		Guid siteId, IReadOnlyList<Guid>? requestedIds, CancellationToken cancellationToken)
	{
		if (requestedIds is null || requestedIds.Count == 0)
		{
			// Issue #279: a full-site scan must fan out over every target under the
			// site, not a PageRequest-clamped page of at most 200 -- ListAllForSiteAsync
			// is the dedicated unpaginated repository method for exactly this caller.
			return await _targets.ListAllForSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
		}

		List<Target> resolved = [];
		foreach (Guid targetId in requestedIds)
		{
			Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
			if (target is null || target.SiteId != siteId)
			{
				throw ApiException.NotFound(
					"Target not found.",
					$"Target '{targetId}' does not exist under site '{siteId}'.");
			}

			resolved.Add(target);
		}

		return resolved;
	}

	/// <summary>
	/// List run summaries, newest-first. Viewer+ — any authenticated user can inspect
	/// runs. Paginated per docs/api-contract.md Conventions' <c>?limit/offset</c> +
	/// <c>X-Total-Count</c> idiom (see <see cref="PageRequest"/>).
	/// </summary>
	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<RunResponse>>> ListRuns(
		[FromQuery] PageRequest page,
		CancellationToken cancellationToken)
	{
		RunListResult result = await _repository.ListRunsAsync(page.Limit, page.Offset, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = result.TotalCount.ToString(CultureInfo.InvariantCulture);
		return Ok(result.Items.Select(MapRun).ToArray());
	}

	/// <summary>
	/// Get run detail with job counts. Viewer+ — any authenticated user can inspect
	/// runs.
	/// </summary>
	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunResponse>> GetRun(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		return Ok(MapRun(run));
	}

	/// <summary>
	/// List all jobs belonging to a run. Viewer+.
	/// </summary>
	[HttpGet("{id:guid}/jobs")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(JobResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<JobResponse>>> GetJobs(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(jobs.Select(MapJob).ToArray());
	}

	/// <summary>
	/// Per-target artifact rows for a run (docs/api-contract.md `/runs/{id}/artifacts`,
	/// issue #299). Viewer+, matching every other run read. Only <c>scan</c> jobs produce
	/// artifacts (issue #275's attest/convert stages) -- other job types in the run are
	/// simply absent from the list, not represented as an empty/zeroed row. CAT I/II/III
	/// counts are parsed from the job's HDF report (<see cref="HdfSeverityCounter"/>) when
	/// one exists AND parses; when the HDF is absent or present-but-corrupt the row reports
	/// <c>counts_available: false</c> with null counts ("could not count"), never a
	/// fabricated zero (issue #299 round-1 blocker). <c>artifact_kinds</c>
	/// reflects exactly which files this run has TODAY under <see cref="ScanOptions.ArtifactStorePath"/>
	/// -- what <c>GET /jobs/{id}/artifacts/{kind}</c> can actually serve, not what the
	/// pipeline will eventually produce.
	/// </summary>
	[HttpGet("{id:guid}/artifacts")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunArtifactResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<RunArtifactResponse>>> GetArtifacts(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		IReadOnlyList<JobSummary> jobs = await _repository.GetJobsForRunAsync(id, cancellationToken).ConfigureAwait(false);
		List<RunArtifactResponse> rows = [];
		foreach (JobSummary job in jobs)
		{
			if (!string.Equals(job.JobType, ScanJobType, StringComparison.Ordinal))
			{
				continue;
			}

			rows.Add(await BuildArtifactRowAsync(job, cancellationToken).ConfigureAwait(false));
		}

		return Ok(rows);
	}

	/// <summary>
	/// The full attestations-applied ledger for a run (docs/api-contract.md
	/// `/runs/{id}/attestations-applied`: "Waivers that fired: control, scope,
	/// justification, author/version, expired-skips"). Viewer+.
	///
	/// Issue #306: this reads the persisted, at-scan-time <c>attestation_snapshots</c>
	/// ledger (migration 0021) that <c>ScanJobHandler.ExecuteAttestStageAsync</c> writes
	/// the instant it resolves each target's attestation -- NOT a live re-resolution. A
	/// historical run's answer is therefore immutable: editing a config-doc after the run
	/// no longer changes what this endpoint reports for it, closing the integrity gap PR
	/// #305 could only disclose (the old <c>derivation: "live-resolution"</c> wire marker
	/// this replaced). One row is recorded per scanned target whose attest stage actually
	/// ran, whether or not a doc applied (see <see cref="AttestationSnapshotRepository.RecordAsync"/>);
	/// a target whose scan job never reached the attest stage contributes no row.
	/// <c>control</c> carries the fixed <see cref="ScanOptions.AttestationProfile"/> name,
	/// not a per-control STIG id -- there is no control-enumeration catalog in this
	/// codebase to join the resolved waiver against; per-control granularity is future
	/// work once one exists (issue #306's AC).
	///
	/// <c>applied_at</c> is now a genuine scan-time timestamp (issue #299/#305 removed it
	/// because the old live-resolution shape had no true application time to report; this
	/// endpoint now reads it back from the recorded snapshot). <c>attestation_updated_at</c>
	/// is still the config-doc version's own timestamp, kept distinct from
	/// <c>applied_at</c> for the same reason as before -- a doc-edit time is not a
	/// scan-time application time, even though both are now real recorded facts.
	/// </summary>
	[HttpGet("{id:guid}/attestations-applied")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(AppliedAttestationResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<AppliedAttestationResponse>>> GetAttestationsApplied(Guid id, CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		IReadOnlyList<AttestationSnapshot> snapshots = await _attestationSnapshots.ListForRunAsync(id, cancellationToken).ConfigureAwait(false);

		List<AppliedAttestationResponse> rows = [];
		foreach (AttestationSnapshot snapshot in snapshots)
		{
			Target? target = await _targets.GetAsync(snapshot.TargetId, cancellationToken).ConfigureAwait(false);
			rows.Add(await MapAttestationAsync(target, snapshot, cancellationToken).ConfigureAwait(false));
		}

		return Ok(rows);
	}

	/// <summary>Pause dispatch for a run. Operator+ (own runs), Admin any — see
	/// <see cref="EnforceRunOwnership"/>.
	/// </summary>
	[HttpPost("{id:guid}/pause")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> PauseRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		EnforceRunOwnership(state);

		bool paused = await _repository.PauseRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (!paused)
		{
			throw ApiException.Validation("Run cannot be paused.", $"Run '{id}' is in state '{state.State}'.");
		}

		// Re-fetch state after the action to return the post-action state.
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), newState?.State ?? "paused"));
	}

	/// <summary>
	/// Resume dispatch for a paused run. Operator+ (own runs), Admin any — see
	/// <see cref="EnforceRunOwnership"/>.
	/// </summary>
	[HttpPost("{id:guid}/resume")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> ResumeRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		EnforceRunOwnership(state);

		bool resumed = await _repository.ResumeRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (!resumed)
		{
			throw ApiException.Validation("Run cannot be resumed.", $"Run '{id}' is in state '{state.State}'.");
		}

		// Re-fetch state after the action to return the post-action state.
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), newState?.State ?? "running"));
	}

	/// <summary>
	/// Abort a run. Operator+ (own runs), Admin any — see
	/// <see cref="EnforceRunOwnership"/>.
	/// </summary>
	[HttpPost("{id:guid}/abort")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(RunActionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunActionResponse>> AbortRun(Guid id, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		EnforceRunOwnership(state);

		AbortRunResult result = await _repository.AbortRunAsync(id, cancellationToken).ConfigureAwait(false);
		_ = result; // AbortRunResult carries cancelled/in-flight job IDs for future enrichment

		// Re-fetch state after the action to return the post-action state (same pattern
		// as pause/resume) rather than assuming the abort always succeeded: AbortRunAsync
		// is a no-op against a run that is already terminal, and the response must say so.
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), newState?.State ?? "aborted"));
	}

	/// <summary>
	/// Swap a replacement credential onto a run's halted (credential queue-halted)
	/// jobs and resume them -- docs/api-contract.md "resume-blocked", ADR-0008.
	/// Admin-only (unlike pause/resume/abort's Operator+-own-runs gate): a credential
	/// swap changes which service account authenticates future work against a target,
	/// which is a stronger action than pausing/resuming dispatch. <c>credential_id</c>
	/// in the body is the REPLACEMENT credential -- <see cref="IJobQueueRepository.SwapAndResumeBlockedCredentialAsync"/>
	/// determines the halted credential being replaced from the run's own blocked job
	/// set, so the caller never names it directly.
	/// </summary>
	[HttpPost("{id:guid}/resume-blocked")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ResumeBlockedResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ResumeBlockedResponse>> ResumeBlocked(
		Guid id, [FromBody] ResumeBlockedRequest request, CancellationToken cancellationToken)
	{
		if (!Guid.TryParse(request?.CredentialId, out Guid replacementCredentialId))
		{
			throw ApiException.Validation("A replacement 'credential_id' is required.", "credential_id must be a valid GUID.");
		}

		string actor = User.GetRequiredUsername();
		CredentialSwapResult result = await _repository
			.SwapAndResumeBlockedCredentialAsync(id, replacementCredentialId, actor, reason: null, cancellationToken)
			.ConfigureAwait(false);

		switch (result.Outcome)
		{
			case CredentialSwapOutcome.RunNotFound:
				throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
			case CredentialSwapOutcome.RunNotHalted:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "run_not_halted",
					"Run has no credential halt to resume from.", $"Run '{id}' is not blocked on a credential queue halt.");
			case CredentialSwapOutcome.AmbiguousHaltedCredential:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "ambiguous_halted_credential",
					"Run's blocked jobs reference more than one halted credential.",
					$"Run '{id}' cannot be resumed with a single replacement credential.");
			case CredentialSwapOutcome.ReplacementCredentialNotFound:
				throw ApiException.NotFound("Replacement credential not found.", $"Credential '{replacementCredentialId}' does not exist.");
			case CredentialSwapOutcome.ReplacementCredentialHalted:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "replacement_credential_halted",
					"Replacement credential is itself queue-halted.", $"Credential '{replacementCredentialId}' has an active queue halt.");
			case CredentialSwapOutcome.ReplacementCredentialTypeMismatch:
				throw ApiException.Validation(
					"Replacement credential type does not match the halted credential.",
					$"Credential '{replacementCredentialId}' is not the same credential_type as the halted credential it would replace.");
			case CredentialSwapOutcome.Swapped:
			default:
				return Ok(new ResumeBlockedResponse(
					id.ToString(), result.OldCredentialId!.Value.ToString(), result.NewCredentialId!.Value.ToString(), result.ResumedJobIds.Count));
		}
	}

	// -- mapping helpers ---------------------------------------------------

	private static RunResponse MapRun(RunSummary run)
	{
		return new RunResponse(
			Id: run.Id.ToString(),
			RunType: run.RunType,
			State: run.State,
			Paused: run.Paused,
			Blocked: run.Blocked,
			BlockedReason: run.BlockedReason,
			Scope: run.ScopeJson,
			CredentialId: run.CredentialId?.ToString(),
			InitiatedBy: run.InitiatedBy,
			CreatedAt: run.CreatedAt!,
			StartedAt: run.StartedAt,
			CompletedAt: run.CompletedAt,
			JobCount: run.JobCount,
			JobCountQueued: run.JobCountQueued,
			JobCountRunning: run.JobCountRunning,
			JobCountCompleted: run.JobCountCompleted,
			JobCountFailed: run.JobCountFailed,
			JobCountBlocked: run.JobCountBlocked);
	}

	/// <summary>
	/// Builds one <see cref="RunArtifactResponse"/> row for a <c>scan</c> job: resolves
	/// the target's kind to look up <see cref="ScanOptions.BenchmarkMetadata"/> for a
	/// benchmark label, checks the artifact store for whichever of the HDF/CKL files
	/// currently exist, and parses CAT I/II/III counts from the HDF when present.
	/// </summary>
	private async Task<RunArtifactResponse> BuildArtifactRowAsync(JobSummary job, CancellationToken cancellationToken)
	{
		string artifactStorePath = _scanOptions.Value.ArtifactStorePath;
		string? hdfPath = ScanArtifactPaths.ResolveHdf(artifactStorePath, job.Id);
		string cklPath = ScanArtifactPaths.Ckl(artifactStorePath, job.Id);

		List<string> kinds = [];
		if (hdfPath is not null)
		{
			kinds.Add(ScanArtifactHdfKind);
		}

		if (System.IO.File.Exists(cklPath))
		{
			kinds.Add(ScanArtifactCklKind);
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

		return new RunArtifactResponse(
			JobId: job.Id.ToString(),
			Target: job.TargetName ?? job.TargetId ?? UnknownTargetLabel,
			Benchmark: benchmark,
			CountsAvailable: counts is not null,
			CatIOpen: counts?.CatIOpen,
			CatIIOpen: counts?.CatIIOpen,
			CatIIIOpen: counts?.CatIIIOpen,
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

	/// <summary>
	/// Maps a persisted at-scan-time snapshot (issue #306) to a wire row.
	/// <see cref="AppliedAttestationResponse.Justification"/> is re-read from
	/// <c>config_versions</c> at the EXACT recorded (doc id, version) -- safe because
	/// versions are append-only and never mutated in place (<see cref="ConfigDocRepository.SaveVersionAsync"/>'s
	/// doc comment), so this is still reading the byte-for-byte body that was in effect
	/// at scan time, not a live re-resolution. <paramref name="target"/> is only for
	/// display (<c>coverage</c>) -- a null target (deleted since the run) falls back to
	/// the snapshot's raw id so the row is never dropped from a historical ledger.
	/// </summary>
	private async Task<AppliedAttestationResponse> MapAttestationAsync(Target? target, AttestationSnapshot snapshot, CancellationToken cancellationToken)
	{
		string justification = string.Empty;
		if (snapshot.DocId is { } docId && snapshot.DocVersion is { } version)
		{
			ConfigDocVersion? body = await _configDocs.GetVersionAsync(docId, version, cancellationToken).ConfigureAwait(false);
			justification = body?.BodyYaml ?? string.Empty;
		}

		return new AppliedAttestationResponse(
			Control: snapshot.Profile,
			Scope: snapshot.Scope,
			Coverage: target?.Name ?? snapshot.TargetId.ToString(),
			Justification: justification,
			Author: snapshot.DocAuthor ?? string.Empty,
			Version: snapshot.DocVersion ?? 0,
			AppliedAt: snapshot.AppliedAt.ToString("O", CultureInfo.InvariantCulture),
			AttestationUpdatedAt: snapshot.DocVersionCreatedAt?.ToString("O", CultureInfo.InvariantCulture),
			Expired: snapshot.Expired);
	}

	private static JobResponse MapJob(JobSummary job)
	{
		return new JobResponse(
			Id: job.Id.ToString(),
			RunId: job.RunId?.ToString(),
			JobType: job.JobType,
			TargetId: job.TargetId,
			TargetName: job.TargetName,
			State: job.State,
			Stage: job.Stage,
			Priority: job.Priority,
			AttemptCount: job.AttemptCount,
			CreatedAt: job.CreatedAt!,
			StartedAt: job.StartedAt,
			FinishedAt: job.FinishedAt);
	}
}
