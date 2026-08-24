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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Runs;
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
	private const string PersonalCredentialKind = "personal";
	private const string PurgeConfirmation = "PURGE";

	private readonly IJobControlRepository _repository;
	private readonly ConfigDocRepository _configDocs;
	private readonly AttestationSnapshotRepository _attestationSnapshots;
	private readonly TargetRepository _targets;
	private readonly RunCreationService _runCreation;
	private readonly RunArtifactProjectionService _artifactProjection;
	private readonly RunControlService _runControl;
	private readonly RunPurgeService _runPurge;
	private readonly IJobEventHistoryReader _eventHistory;

	public RunsController(
		IJobControlRepository repository,
		ConfigDocRepository configDocs,
		AttestationSnapshotRepository attestationSnapshots,
		TargetRepository targets,
		RunCreationService runCreation,
		RunArtifactProjectionService artifactProjection,
		RunControlService runControl,
		RunPurgeService runPurge,
		IJobEventHistoryReader eventHistory)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(attestationSnapshots);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(runCreation);
		ArgumentNullException.ThrowIfNull(artifactProjection);
		ArgumentNullException.ThrowIfNull(runControl);
		ArgumentNullException.ThrowIfNull(runPurge);
		ArgumentNullException.ThrowIfNull(eventHistory);
		_repository = repository;
		_configDocs = configDocs;
		_attestationSnapshots = attestationSnapshots;
		_targets = targets;
		_runCreation = runCreation;
		_artifactProjection = artifactProjection;
		_runControl = runControl;
		_runPurge = runPurge;
		_eventHistory = eventHistory;
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
		ValidateCredentialOverridesRequest(request);
		ValidateAdHocCredentialsRequest(request);

		string initiatedBy = User.GetRequiredUsername();

		if (string.Equals(request.RunType, ScanRunType, StringComparison.Ordinal))
		{
			RunSecretCredentialRequest? credential = request.Credential is null
				? null
				: new RunSecretCredentialRequest(request.Credential.Username, request.Credential.Secret);

			IReadOnlyList<RunCredentialOverride>? overrides = request.CredentialOverrides is not { Count: > 0 }
				? null
				: [.. request.CredentialOverrides.Select(o => new RunCredentialOverride(o.TargetId, o.Purpose, o.CredentialId))];

			IReadOnlyList<RunAdHocCredential>? adHocCredentials = request.AdHocCredentials is not { Count: > 0 }
				? null
				: [.. request.AdHocCredentials.Select(a => new RunAdHocCredential(a.TargetId, a.Purpose, a.Username, a.Secret))];

			Guid scanRunId = await _runCreation.CreateScanRunAsync(
				request.Scope, request.CredentialId, credential, initiatedBy, cancellationToken,
				credentialOverrides: overrides, adHocCredentials: adHocCredentials).ConfigureAwait(false);

			return Accepted(new RunCreatedResponse(scanRunId.ToString()));
		}

		Guid runId = await _runCreation.CreateRunAsync(
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
	/// Issue #585 shape gates for <c>credential_overrides</c>: scan runs only, no inline
	/// ad hoc credential alongside (per-target ad hoc secrets are #586), and every
	/// purpose string must be a member of the closed <see cref="CredentialPurposes"/> set
	/// (a garbage purpose is a malformed request, not a per-target resolution gap --
	/// same split the binding CRUD endpoints' <c>invalid_purpose</c> check uses).
	/// Semantic validation (target in scope, purpose applicable to the target's kind,
	/// credential exists/compatible) happens in
	/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>'s resolution step,
	/// which enumerates every gap in one <c>credential_binding_gaps</c> 400. No role
	/// gate beyond the scan-creation floor: a caller who may start a scan with a stored
	/// <c>credential_id</c> (Cyber+) may name stored credentials per target/purpose the
	/// same way -- overrides reference the service tier only, never personal secrets.
	/// </summary>
	private static void ValidateCredentialOverridesRequest(RunCreateRequest request)
	{
		if (request.CredentialOverrides is not { Count: > 0 })
		{
			return;
		}

		if (!string.Equals(request.RunType, ScanRunType, StringComparison.Ordinal))
		{
			throw ApiException.Validation(
				"credential_overrides are only valid for scan runs.",
				$"\"credential_overrides\" may only be set when \"run_type\" is \"{ScanRunType}\".");
		}

		if (request.Credential is not null)
		{
			throw ApiException.Validation(
				"credential_overrides and credential are mutually exclusive.",
				"Per-target saved-credential overrides apply to stored-credential scans only; use ad_hoc_credentials for per-target ad hoc secrets (issue #586).");
		}

		foreach (RunCredentialOverrideRequest @override in request.CredentialOverrides)
		{
			if (!CredentialPurposes.IsValid(@override.Purpose))
			{
				throw ApiException.Validation(
					"credential_overrides contains an unknown purpose.",
					$"'{@override.Purpose}' is not a credential purpose; valid values: {string.Join(", ", CredentialPurposes.All)}.");
			}
		}
	}

	/// <summary>
	/// Issue #586 shape gates for <c>ad_hoc_credentials</c>: Operator+ (same floor as
	/// <see cref="Credential"/> -- personal secrets are always the Operator tier,
	/// docs/domain-model.md), scan runs only, no duplicate (target, purpose) pair within
	/// the array itself, no pair also named in <see cref="RunCreateRequest.CredentialOverrides"/>
	/// (a single (target, purpose) slot has exactly one source of truth -- mixing an ad
	/// hoc secret and a saved-credential override for the SAME pair has no defined
	/// precedence), and every purpose string must be a member of the closed
	/// <see cref="CredentialPurposes"/> set. Unlike <see cref="ValidateCredentialOverridesRequest"/>,
	/// this tier is NOT globally mutually exclusive with <see cref="RunCreateRequest.Credential"/>
	/// or <see cref="RunCreateRequest.CredentialOverrides"/> -- a caller may combine the
	/// legacy flat ad hoc credential, saved overrides, and per-target ad hoc overrides in
	/// one request as long as no two sources ever name the same (target, purpose) pair
	/// (semantic scope/target-membership checks happen in
	/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>'s resolution step,
	/// which enumerates every gap in one <c>credential_binding_gaps</c> 400).
	/// </summary>
	private void ValidateAdHocCredentialsRequest(RunCreateRequest request)
	{
		if (request.AdHocCredentials is not { Count: > 0 })
		{
			return;
		}

		if (!CallerHasAtLeast(WaypointRole.Operator))
		{
			throw ApiException.Forbidden(
				"Ad hoc credentials require the Operator role.",
				"Only Operator+ may start a run with per-target \"ad_hoc_credentials\" (ADR-0011 personal tier); use credential_overrides with a stored credential, or ask an Operator to start this run.");
		}

		if (!string.Equals(request.RunType, ScanRunType, StringComparison.Ordinal))
		{
			throw ApiException.Validation(
				"ad_hoc_credentials are only valid for scan runs.",
				$"\"ad_hoc_credentials\" may only be set when \"run_type\" is \"{ScanRunType}\".");
		}

		HashSet<(Guid TargetId, string Purpose)> seen = [];
		HashSet<(Guid TargetId, string Purpose)> savedOverridePairs = request.CredentialOverrides is { Count: > 0 }
			? [.. request.CredentialOverrides.Select(o => (o.TargetId, o.Purpose))]
			: [];

		foreach (RunAdHocCredentialRequest adHoc in request.AdHocCredentials)
		{
			if (!CredentialPurposes.IsValid(adHoc.Purpose))
			{
				throw ApiException.Validation(
					"ad_hoc_credentials contains an unknown purpose.",
					$"'{adHoc.Purpose}' is not a credential purpose; valid values: {string.Join(", ", CredentialPurposes.All)}.");
			}

			if (string.IsNullOrWhiteSpace(adHoc.Username) || string.IsNullOrWhiteSpace(adHoc.Secret))
			{
				throw ApiException.Validation(
					"ad_hoc_credentials entries require both username and secret.",
					$"Set non-empty \"username\" and \"secret\" for target '{adHoc.TargetId}', purpose '{adHoc.Purpose}'.");
			}

			if (!seen.Add((adHoc.TargetId, adHoc.Purpose)))
			{
				throw ApiException.Validation(
					"ad_hoc_credentials contains a duplicate (target_id, purpose) pair.",
					$"Target '{adHoc.TargetId}' purpose '{adHoc.Purpose}' appears more than once in \"ad_hoc_credentials\".");
			}

			if (savedOverridePairs.Contains((adHoc.TargetId, adHoc.Purpose)))
			{
				throw ApiException.Validation(
					"ad_hoc_credentials and credential_overrides name the same (target_id, purpose) pair.",
					$"Target '{adHoc.TargetId}' purpose '{adHoc.Purpose}' is named in both \"ad_hoc_credentials\" and \"credential_overrides\"; each (target, purpose) pair may have only one credential source.");
			}
		}
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
	/// Retries a single <c>failed</c> job within a run, resuming from its last-reached
	/// stage (issue #297; the HTTP surface ADR-0012 §5 deferred). Run-scoped so
	/// ownership resolves the same way <c>DELETE /jobs/{id}</c>'s does (issue #294):
	/// Operator+ (own runs), Admin any -- <see cref="EnforceRunOwnership(ClaimsPrincipal, RunQueueState)"/>
	/// against the run named in the route, not a run resolved indirectly off the job.
	/// The job must belong to <paramref name="runId"/> (404 otherwise -- a job id from a
	/// different run is not silently retried under the wrong run's authority). Scoped to
	/// <c>failed</c> only: NOT <c>auth-failed</c> (issue #146/#295's credential-swap-resume
	/// path is the correct route there -- retrying without swapping the bad credential
	/// would just re-fail) and NOT <c>cancelled</c> (a deliberate operator action; the
	/// operator starts a new run rather than silently re-queueing it). A retry on any
	/// other state, including those two, is 409. This is a manual override of the
	/// engine's own retry accounting: it does not increment <c>attempt_count</c> and is
	/// never blocked by the automatic-retry <c>max_attempts</c> cap -- see
	/// <see cref="IJobControlRepository.RetryJobAsync"/>. <c>jobs.stage</c> is preserved
	/// untouched, so the next claim resumes the pipeline at the marker rather than
	/// restarting it (ADR-0012 §5), and the action is recorded to <c>audit_log</c>
	/// (<c>event_type = 'job.retried'</c>).
	/// </summary>
	[HttpPost("{runId:guid}/jobs/{jobId:guid}/retry")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(JobRetryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<JobRetryResponse>> RetryJob(Guid runId, Guid jobId, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _repository.GetRunQueueStateAsync(runId, cancellationToken).ConfigureAwait(false);
		if (state is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{runId}' does not exist.");
		}

		EnforceRunOwnership(state);

		JobSummary? job = await _repository.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
		if (job is null || job.RunId != runId)
		{
			throw ApiException.NotFound("Job not found.", $"Job '{jobId}' does not exist under run '{runId}'.");
		}

		string actor = User.GetRequiredUsername();
		JobRetryOutcome outcome = await _repository.RetryJobAsync(jobId, actor, cancellationToken).ConfigureAwait(false);

		switch (outcome)
		{
			case JobRetryOutcome.NotFound:
				throw ApiException.NotFound("Job not found.", $"Job '{jobId}' does not exist under run '{runId}'.");
			case JobRetryOutcome.NotFailed:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "not_retryable",
					"Job cannot be retried.", $"Job '{jobId}' is in state '{job.State}'; only a 'failed' job may be retried.");
			case JobRetryOutcome.Retried:
			default:
				return Ok(new JobRetryResponse(jobId.ToString(), JobStates.Queued, job.Stage));
		}
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

		IReadOnlyList<RunArtifactRow> rows = await _artifactProjection.GetArtifactsAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(rows.Select(MapArtifactRow).ToArray());
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

		string resultState = await _runControl.PauseAsync(id, state, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), resultState));
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

		string resultState = await _runControl.ResumeAsync(id, state, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), resultState));
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

		// AbortRunAsync is a no-op against a run that is already terminal;
		// RunControlService.AbortAsync re-fetches state after the action so the
		// response reflects that rather than assuming the abort always succeeded.
		string resultState = await _runControl.AbortAsync(id, state, cancellationToken).ConfigureAwait(false);
		return Ok(new RunActionResponse(id.ToString(), resultState));
	}

	/// <summary>
	/// Swap a replacement credential onto a run's halted (credential queue-halted)
	/// jobs and resume them -- docs/api-contract.md "resume-blocked", ADR-0008.
	/// Admin-only (unlike pause/resume/abort's Operator+-own-runs gate): a credential
	/// swap changes which service account authenticates future work against a target,
	/// which is a stronger action than pausing/resuming dispatch. <c>credential_id</c>
	/// in the body is the REPLACEMENT credential -- <see cref="IJobControlRepository.SwapAndResumeBlockedCredentialAsync"/>
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

	/// <summary>
	/// Purges a terminal compliance run's owned database projections and artifact
	/// files (issue #594, epic #577). Admin-only -- a stronger gate than
	/// pause/resume/abort's Operator+-own-runs, matching <see cref="ResumeBlocked"/>'s
	/// precedent for an action with wider blast radius than ordinary dispatch control.
	/// Requires the explicit <c>confirmation: "PURGE"</c> body field, mirroring
	/// <see cref="RemediateConfirmation"/>'s step-up pattern -- purge is destructive and
	/// irreversible. Non-terminal runs are rejected with a machine-readable 409
	/// (<c>run_not_terminal</c>); the run is left untouched. Safe to call again at any
	/// point (see <see cref="RunPurgeService"/>'s doc comment) -- an already-purged run
	/// returns its tombstone rather than erroring, and a partially-purged run resumes.
	/// </summary>
	[HttpPost("{id:guid}/purge")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RunPurgeStatusResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(RunPurgeStatusResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<RunPurgeStatusResponse>> PurgeRun(Guid id, [FromBody] RunPurgeRequest? request, CancellationToken cancellationToken)
	{
		if (!string.Equals(request?.Confirmation, PurgeConfirmation, StringComparison.Ordinal))
		{
			throw ApiException.Validation(
				"Purge requires explicit confirmation.",
				$"Set \"confirmation\": \"{PurgeConfirmation}\" in the request body to purge this run.");
		}

		string actor = User.GetRequiredUsername();
		RunPurgeResult result = await _runPurge.PurgeRunAsync(id, actor, cancellationToken).ConfigureAwait(false);

		switch (result.Outcome)
		{
			case RunPurgeOutcome.RunNotFound:
				throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
			case RunPurgeOutcome.RunNotTerminal:
				throw new ApiException(
					System.Net.HttpStatusCode.Conflict, "run_not_terminal",
					"Run cannot be purged.", $"Run '{id}' is not in a terminal state (completed, completed_with_failures, or aborted).");
			case RunPurgeOutcome.Completed:
			case RunPurgeOutcome.AlreadyPurged:
				return Ok(MapPurgeStatus(id, result));
			case RunPurgeOutcome.InProgress:
			case RunPurgeOutcome.Failed:
			default:
				return Accepted(MapPurgeStatus(id, result));
		}
	}

	/// <summary>
	/// Polls purge progress/terminal state for a run (issue #594) -- the frontend's
	/// progress/retry UI reads this rather than re-issuing <see cref="PurgeRun"/> just
	/// to check status. 404 when purge was never requested for this run (distinct from
	/// the run itself not existing, which is also 404 -- both cases return the same
	/// generic not-found shape since neither leaks which one it was).
	/// </summary>
	[HttpGet("{id:guid}/purge")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunPurgeStatusResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunPurgeStatusResponse>> GetPurgeStatus(Guid id, CancellationToken cancellationToken)
	{
		RunPurgeResult? result = await _runPurge.GetStatusAsync(id, cancellationToken).ConfigureAwait(false);
		if (result is null)
		{
			throw ApiException.NotFound("No purge has been requested for this run.", $"Run '{id}' has never had a purge requested.");
		}

		return Ok(MapPurgeStatus(id, result));
	}

	/// <summary>
	/// Bounded, cursor-paged historical read over a run's persisted <c>job_events</c>
	/// (issue #581, ADR-0019) -- the complement to the SSE stream
	/// (<see cref="EventStreamController.RunStream"/>): SSE is the live/replay
	/// transport for an open connection, this is a single bounded page for a client
	/// that wants completed-run (or completed-so-far) history without holding a stream
	/// open. Viewer+, matching every other run read (<see cref="ListRuns"/>,
	/// <see cref="GetRun"/>, <see cref="GetJobs"/>) -- no ownership scoping, since
	/// reading operational history is visibility, not a domain action (ADR-0019
	/// decision 6: "a role may observe permitted job metadata/logs without receiving
	/// the ability to perform the job type's domain actions").
	///
	/// 404 for a run that does not exist. An existing run with no matching events
	/// (including one that simply has not produced any yet) returns 200 with an empty
	/// <c>items</c> array and a null <c>next_cursor</c> -- distinct from 404, so an
	/// empty history is never confused with "no such run". <paramref name="job_id"/>
	/// naming a job that is not actually part of this run is not validated separately;
	/// it simply matches zero rows (same "narrow the range, don't guess" posture as
	/// <see cref="EventStreamController"/>'s scope filter).
	///
	/// Redaction: every returned payload is the same already-redacted
	/// <c>job_events.payload</c> column SSE streams (<see cref="JobEventPublisher"/>/
	/// <c>BufferedJobEventWriter</c> redact at write time) -- this endpoint performs no
	/// additional transform and adds no new leak surface.
	/// </summary>
	[HttpGet("{id:guid}/events/history")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RunEventHistoryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RunEventHistoryResponse>> GetEventHistory(
		Guid id,
		[FromQuery(Name = "job_id")] string? jobId,
		[FromQuery(Name = "kind")] string? kind,
		[FromQuery(Name = "level")] string? level,
		[FromQuery(Name = "cursor")] string? cursor,
		[FromQuery(Name = "limit")] int? limit,
		CancellationToken cancellationToken)
	{
		RunSummary? run = await _repository.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
		if (run is null)
		{
			throw ApiException.NotFound("Run not found.", $"Run '{id}' does not exist.");
		}

		RunEventHistoryRequest query = new(jobId, kind, level, cursor, limit);
		JobEventHistoryQuery historyQuery = MapHistoryQuery(id, query);
		JobEventHistoryPage page = await _eventHistory.ReadHistoryAsync(historyQuery, cancellationToken).ConfigureAwait(false);

		return Ok(new RunEventHistoryResponse(
			Items: page.Items.Select(MapHistoryItem).ToArray(),
			NextCursor: page.NextCursor is { } nextSeq ? JobEventCursor.Encode(nextSeq) : null));
	}

	/// <summary>
	/// Validates and converts <see cref="RunEventHistoryRequest"/>'s query-string shape
	/// into <see cref="JobEventHistoryQuery"/>. Every rejection is
	/// <see cref="ApiException.Validation"/> (400, machine-readable <c>validation_error</c>
	/// code) rather than a 500 -- a garbage cursor or an unknown filter value is a
	/// malformed request, not a server fault (issue #581 AC "cursor abuse").
	/// </summary>
	private static JobEventHistoryQuery MapHistoryQuery(Guid runId, RunEventHistoryRequest query)
	{
		Guid? jobId = null;
		if (!string.IsNullOrWhiteSpace(query.JobId))
		{
			if (!Guid.TryParse(query.JobId, out Guid parsedJobId))
			{
				throw ApiException.Validation("job_id is not a valid identifier.", $"'{query.JobId}' is not a valid GUID.");
			}

			jobId = parsedJobId;
		}

		IReadOnlyList<string>? eventTypes = ParseAllowList(query.Kind, JobEventTypes.IsValid,
			"kind", $"valid values: {string.Join(", ", JobEventTypes.All)}.");
		IReadOnlyList<string>? severities = ParseAllowList(query.Level, JobLogSeverities.IsValid,
			"level", $"valid values: {string.Join(", ", JobLogSeverities.All)}.");

		long? afterSeq = null;
		if (!string.IsNullOrWhiteSpace(query.Cursor))
		{
			if (!JobEventCursor.TryDecode(query.Cursor, out long decoded))
			{
				throw ApiException.Validation("cursor is not valid.", "The 'cursor' query parameter must be an opaque value returned by a previous response's next_cursor -- it cannot be constructed by hand.");
			}

			afterSeq = decoded;
		}

		const int DefaultLimit = 100;
		const int MaxLimit = 500;
		int limit = query.Limit is { } requested ? Math.Clamp(requested, 1, MaxLimit) : DefaultLimit;

		return new JobEventHistoryQuery(runId, jobId, eventTypes, severities, afterSeq, limit);
	}

	/// <summary>
	/// Splits a comma-separated filter value into a de-duplicated allow-list, or null
	/// when the query parameter was omitted. Every entry must satisfy
	/// <paramref name="isValid"/> or the whole request 400s (issue #581 AC: filters must
	/// page correctly, not silently drop an unrecognized value and under-report).
	/// </summary>
	private static string[]? ParseAllowList(string? raw, Func<string, bool> isValid, string paramName, string validValuesDetail)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		string[] values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (values.Length == 0)
		{
			return null;
		}

		foreach (string value in values)
		{
			if (!isValid(value))
			{
				throw ApiException.Validation($"{paramName} contains an unknown value.", $"'{value}' is not recognized; {validValuesDetail}");
			}
		}

		return values.Distinct(StringComparer.Ordinal).ToArray();
	}

	private static JobEventHistoryItemResponse MapHistoryItem(StreamedJobEvent row)
	{
		return new JobEventHistoryItemResponse(
			Seq: row.Seq,
			Ts: row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
			Type: row.EventType,
			RunId: row.RunId?.ToString(),
			JobId: row.JobId?.ToString(),
			Data: JsonDocument.Parse(row.PayloadJson).RootElement.Clone());
	}

	// -- mapping helpers ---------------------------------------------------

	private static RunPurgeStatusResponse MapPurgeStatus(Guid runId, RunPurgeResult result)
	{
		RunPurgeStatus? status = result.Status;
		return new RunPurgeStatusResponse(
			RunId: runId.ToString(),
			Outcome: result.Outcome.ToString(),
			RequestedBy: status?.RequestedBy ?? string.Empty,
			RequestedAt: status?.RequestedAt.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
			PriorState: status?.PriorState ?? string.Empty,
			DbPhaseDone: status?.DbPhaseDone ?? false,
			ArtifactsPhase: status?.ArtifactsPhase ?? "pending",
			ArtifactsTotal: status?.ArtifactsTotal ?? 0,
			ArtifactsDeleted: status?.ArtifactsDeleted ?? 0,
			LastError: status?.LastError,
			CompletedAt: status?.CompletedAt?.ToString("O", CultureInfo.InvariantCulture));
	}

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
			ScheduleId: run.ScheduleId?.ToString(),
			CreatedAt: run.CreatedAt!,
			StartedAt: run.StartedAt,
			CompletedAt: run.CompletedAt,
			JobCount: run.JobCount,
			JobCountQueued: run.JobCountQueued,
			JobCountRunning: run.JobCountRunning,
			JobCountCompleted: run.JobCountCompleted,
			JobCountFailed: run.JobCountFailed,
			JobCountBlocked: run.JobCountBlocked,
			CredentialName: run.CredentialName,
			CredentialType: run.CredentialType,
			CredentialUsername: run.CredentialUsername);
	}

	/// <summary>
	/// Maps a <see cref="RunArtifactRow"/> (control-plane shape,
	/// <see cref="RunArtifactProjectionService"/>) to the wire response.
	/// </summary>
	private static RunArtifactResponse MapArtifactRow(RunArtifactRow row)
	{
		return new RunArtifactResponse(
			JobId: row.JobId.ToString(),
			Target: row.Target,
			Benchmark: row.Benchmark,
			CountsAvailable: row.CountsAvailable,
			CatIOpen: row.CatIOpen,
			CatIIOpen: row.CatIIOpen,
			CatIIIOpen: row.CatIIIOpen,
			ArtifactKinds: row.ArtifactKinds,
			UploadStatus: row.UploadStatus,
			UploadDetail: row.UploadDetail);
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
			FinishedAt: job.FinishedAt,
			CredentialName: job.CredentialName,
			CredentialType: job.CredentialType,
			CredentialUsername: job.CredentialUsername);
	}
}
