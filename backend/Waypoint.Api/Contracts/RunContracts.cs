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
using System.Text.Json.Serialization;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Ad hoc "my credentials" body for <c>POST /api/v1/runs</c> (ADR-0011 personal tier,
/// issue #276): an alternative to <see cref="RunCreateRequest.CredentialId"/> that
/// prompts the caller for a username/secret at run initiation instead of referencing a
/// stored row. <c>kind</c> is carried explicitly (rather than inferred from "credential
/// present") so the wire shape has room for a future tier without a breaking change;
/// <c>"personal"</c> is the only value v1 accepts. Never logged, never echoed in any
/// response, never written anywhere except envelope-encrypted, run-scoped storage
/// (<see cref="Waypoint.Core.Secrets.IRunSecretStore"/>, issue #434) -- never a row in
/// the reusable <c>credentials</c>/<c>credential_secrets</c> tables.
/// </summary>
public sealed record EphemeralCredentialRequest(
	[property: JsonPropertyName("kind")]
	string Kind,

	/// <summary>The protocol-level login (e.g. vSphere SSO username) the target job presents.</summary>
	[property: JsonPropertyName("username")]
	string Username,

	/// <summary>The caller's own password/secret. Encrypted at rest for the run's lifetime only -- see docs/security.md "in-play redaction".</summary>
	[property: JsonPropertyName("secret")]
	string Secret);

/// <summary>Request body for <c>POST /api/v1/runs</c>.</summary>
public sealed record RunCreateRequest(
	/// <summary>Run type: scan, remediate, etc.</summary>
	[property: JsonPropertyName("run_type")]
	string RunType,

	/// <summary>Scope: products/components + inventory selection (JSON object).</summary>
	[property: JsonPropertyName("scope")]
	string Scope,

	/// <summary>Optional credential id to use for all targets in the run.</summary>
	[property: JsonPropertyName("credential_id")]
	Guid? CredentialId,

	/// <summary>
	/// Required for <c>run_type</c> "remediate": must be the literal
	/// <c>"REMEDIATE"</c> (docs/api-contract.md `/runs`). Ignored for other run
	/// types. The initiator is always taken from the authenticated identity, never
	/// from the request.
	/// </summary>
	[property: JsonPropertyName("confirmation")]
	string? Confirmation,

	/// <summary>
	/// Ad hoc "my credentials" alternative to <see cref="CredentialId"/> for scan runs
	/// (ADR-0011, issue #276). Mutually exclusive with <c>credential_id</c> and requires
	/// Operator+ (docs/domain-model.md: "Cyber = initiate scans with service
	/// credentials, Operator = Cyber + ad hoc scans with personal credentials").
	/// </summary>
	[property: JsonPropertyName("credential")]
	EphemeralCredentialRequest? Credential = null,

	/// <summary>
	/// Issue #585 (epic #582, ADR-0021 §4): structured per-target/per-purpose
	/// SAVED-credential overrides for a scan run. Each entry substitutes the named
	/// stored credential for exactly one (target, purpose) pair, replacing that
	/// target's own binding (and, for the target's default purpose, any run-level
	/// <see cref="CredentialId"/>). Scan runs only; mutually exclusive with the inline
	/// <see cref="Credential"/> tier (per-target ad hoc secrets are issue #586).
	/// Validation failures come back as one 400 <c>credential_binding_gaps</c>
	/// enumerating every (target, purpose, reason).
	/// </summary>
	[property: JsonPropertyName("credential_overrides")]
	IReadOnlyList<RunCredentialOverrideRequest>? CredentialOverrides = null,

	/// <summary>
	/// Issue #586 (epic #582, ADR-0021 §4): structured per-target/per-purpose AD HOC
	/// ("my credentials", ADR-0011) overrides for a scan run -- the per-target/per-purpose
	/// counterpart of <see cref="Credential"/>'s single flat inline pair. Each entry
	/// supplies an inline username/secret for exactly one (target, purpose) pair,
	/// encrypted at rest under that pair's own <c>run_secrets</c> row (never a stored
	/// <c>credentials</c> row -- ADR-0011's "no personal rows, ever"). Multiple entries
	/// for DIFFERENT (target, purpose) pairs coexist freely on one request; a duplicate
	/// pair, or a pair also named in <see cref="CredentialOverrides"/>, is a validation
	/// gap. Scan runs only; requires Operator+ (same floor as <see cref="Credential"/>).
	/// </summary>
	[property: JsonPropertyName("ad_hoc_credentials")]
	IReadOnlyList<RunAdHocCredentialRequest>? AdHocCredentials = null);

/// <summary>One saved-credential override for a specific (target, purpose) pair -- see <see cref="RunCreateRequest.CredentialOverrides"/>.</summary>
public sealed record RunCredentialOverrideRequest(
	[property: JsonPropertyName("target_id")]
	Guid TargetId,

	[property: JsonPropertyName("purpose")]
	string Purpose,

	[property: JsonPropertyName("credential_id")]
	Guid CredentialId);

/// <summary>
/// One inline ad hoc ("my credentials") credential for a specific (target, purpose)
/// pair -- see <see cref="RunCreateRequest.AdHocCredentials"/>. Never logged, never
/// echoed in any response, never written anywhere except envelope-encrypted, run-scoped
/// storage keyed by (run, target, purpose) (<see cref="Waypoint.Core.Secrets.IRunSecretStore"/>,
/// issue #586).
/// </summary>
public sealed record RunAdHocCredentialRequest(
	[property: JsonPropertyName("target_id")]
	Guid TargetId,

	[property: JsonPropertyName("purpose")]
	string Purpose,

	[property: JsonPropertyName("username")]
	string Username,

	[property: JsonPropertyName("secret")]
	string Secret);

/// <summary>Response body for <c>GET /api/v1/runs/{id}</c>.</summary>
public sealed record RunResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("run_type")]
	string RunType,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("paused")]
	bool Paused,

	[property: JsonPropertyName("blocked")]
	bool Blocked,

	[property: JsonPropertyName("blocked_reason")]
	string? BlockedReason,

	[property: JsonPropertyName("scope")]
	string Scope,

	[property: JsonPropertyName("credential_id")]
	string? CredentialId,

	[property: JsonPropertyName("initiated_by")]
	string? InitiatedBy,

	/// <summary>
	/// The schedule that produced this run (issue #515), or null for an
	/// operator-initiated run. Only <c>ScheduleDispatchService</c> stamps this --
	/// distinct from <c>schedules.last_run_id</c>, which only ever points at a
	/// schedule's most recent run.
	/// </summary>
	[property: JsonPropertyName("schedule_id")]
	string? ScheduleId,

	[property: JsonPropertyName("created_at")]
	string CreatedAt,

	[property: JsonPropertyName("started_at")]
	string? StartedAt,

	[property: JsonPropertyName("completed_at")]
	string? CompletedAt,

	/// <summary>Total jobs in the run.</summary>
	[property: JsonPropertyName("job_count")]
	int JobCount,

	/// <summary>Jobs still queued.</summary>
	[property: JsonPropertyName("job_count_queued")]
	int JobCountQueued,

	/// <summary>Jobs currently running.</summary>
	[property: JsonPropertyName("job_count_running")]
	int JobCountRunning,

	/// <summary>Jobs completed successfully.</summary>
	[property: JsonPropertyName("job_count_completed")]
	int JobCountCompleted,

	/// <summary>Jobs that failed (including auth-failed).</summary>
	[property: JsonPropertyName("job_count_failed")]
	int JobCountFailed,

	/// <summary>Jobs blocked by credential halt.</summary>
	[property: JsonPropertyName("job_count_blocked")]
	int JobCountBlocked,

	/// <summary>
	/// Issue #593: non-secret attribution snapshotted when this run's credential was
	/// a terminal-only reference at deletion time -- null while <see cref="CredentialId"/>
	/// still names a live credential, and null forever for a run that never had one.
	/// </summary>
	[property: JsonPropertyName("credential_name")]
	string? CredentialName = null,

	[property: JsonPropertyName("credential_type")]
	string? CredentialType = null,

	[property: JsonPropertyName("credential_username")]
	string? CredentialUsername = null);

/// <summary>Single job row for <c>GET /api/v1/runs/{id}/jobs</c>.</summary>
public sealed record JobResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("run_id")]
	string? RunId,

	[property: JsonPropertyName("job_type")]
	string JobType,

	[property: JsonPropertyName("target_id")]
	string? TargetId,

	[property: JsonPropertyName("target_name")]
	string? TargetName,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("stage")]
	string? Stage,

	[property: JsonPropertyName("priority")]
	short Priority,

	[property: JsonPropertyName("attempt_count")]
	int AttemptCount,

	[property: JsonPropertyName("created_at")]
	string CreatedAt,

	[property: JsonPropertyName("started_at")]
	string? StartedAt,

	[property: JsonPropertyName("finished_at")]
	string? FinishedAt,

	/// <summary>Issue #593: see <see cref="RunResponse.CredentialName"/> -- same snapshot, job-scoped.</summary>
	[property: JsonPropertyName("credential_name")]
	string? CredentialName = null,

	[property: JsonPropertyName("credential_type")]
	string? CredentialType = null,

	[property: JsonPropertyName("credential_username")]
	string? CredentialUsername = null);

/// <summary>Response body for <c>POST /api/v1/runs</c> (202 Accepted).</summary>
public sealed record RunCreatedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId);

/// <summary>
/// Request body for <c>POST /api/v1/runs/plan-preview</c> (issues #733/#734 remainder,
/// docs/api-contract.md's planned <c>/runs/plan-preview</c>): the same <c>scope</c>
/// shape <c>POST /runs</c> accepts for a scan, restricted at this endpoint to the
/// <c>target_scope</c> form -- preview never selects a profile (ADR-0022 §7), so
/// <c>scope.profile_id</c> is rejected here rather than silently ignored.
/// <see cref="CredentialOverrides"/>/<see cref="AdHocCredentials"/> are optional and,
/// when supplied, are evaluated read-only against the resolved plan's per-component
/// required purposes -- exactly the coverage <c>POST /runs</c> would resolve for the
/// same inputs -- so the wizard can show a gap before the caller commits to creating the
/// run.
/// </summary>
public sealed record RunPlanPreviewRequest(
	[property: JsonPropertyName("scope")]
	string Scope,

	[property: JsonPropertyName("credential_overrides")]
	IReadOnlyList<RunCredentialOverrideRequest>? CredentialOverrides = null,

	[property: JsonPropertyName("ad_hoc_credentials")]
	IReadOnlyList<RunAdHocCredentialRequest>? AdHocCredentials = null);

/// <summary>
/// Response body for <c>POST /api/v1/runs/plan-preview</c>: the resolved component scope
/// (requested-vs-resolved, every <see cref="Waypoint.Core.Components.ScopeOmission"/>),
/// the would-be plan (accepted items and skips, post credential-gap demotion), and every
/// credential gap found. <see cref="PlanDigest"/> is byte-for-byte identical to a
/// subsequent <c>POST /runs</c> create's digest for the same inputs (issue #734 AC-4) --
/// this is the field a caller can use to detect "nothing changed since I previewed" before
/// committing. Mirrors <c>GET /runs/{id}/plan</c>'s planned response shape (not yet
/// implemented -- that endpoint is a distinct remainder), minus <c>run_id</c>, which does
/// not exist yet for a preview.
/// </summary>
public sealed record RunPlanPreviewResponse(
	[property: JsonPropertyName("requested_mode")]
	string RequestedMode,

	[property: JsonPropertyName("resolved_component_ids")]
	IReadOnlyList<Guid> ResolvedComponentIds,

	[property: JsonPropertyName("scope_omissions")]
	IReadOnlyList<Waypoint.Core.Components.ScopeOmission> ScopeOmissions,

	[property: JsonPropertyName("plan_schema_version")]
	int PlanSchemaVersion,

	[property: JsonPropertyName("items")]
	IReadOnlyList<Waypoint.Core.Scans.ScanPlanItem> Items,

	[property: JsonPropertyName("skips")]
	IReadOnlyList<Waypoint.Core.Scans.ScanPlanSkip> Skips,

	[property: JsonPropertyName("plan_digest")]
	string PlanDigest,

	[property: JsonPropertyName("explanation")]
	string Explanation,

	[property: JsonPropertyName("is_runnable")]
	bool IsRunnable,

	[property: JsonPropertyName("credential_gaps")]
	IReadOnlyList<Waypoint.Core.Errors.CredentialBindingGap> CredentialGaps);

/// <summary>Response body for <c>POST /api/v1/runs/{id}/pause|resume|abort</c>.</summary>
public sealed record RunActionResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("state")]
	string State);

/// <summary>
/// Response body for <c>DELETE /api/v1/jobs/{id}</c> (issue #291, wrapping #277's
/// <see cref="Waypoint.Core.Jobs.JobCancelOutcome"/>). <see cref="State"/> distinguishes
/// an immediate cancel (<c>"cancelled"</c>) from a cooperative in-flight request
/// (<c>"cancel_requested"</c>) -- the caller needs to know which happened, since the
/// job is not necessarily stopped yet in the second case.
/// </summary>
public sealed record JobCancelResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("state")]
	string State);

/// <summary>
/// Response body for <c>POST /api/v1/runs/{runId}/jobs/{jobId}/retry</c> (issue #297,
/// ADR-0012 §5). <see cref="Stage"/> echoes back the preserved <c>jobs.stage</c> marker
/// (null when the job had not yet completed any stage) so the caller can confirm the
/// resume point without a follow-up GET.
/// </summary>
public sealed record JobRetryResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("stage")]
	string? Stage);

/// <summary>
/// Request body for <c>POST /api/v1/runs/{id}/jobs/bulk-cancel</c> and
/// <c>bulk-retry</c> (issue #757). Exactly one of <see cref="JobIds"/> or
/// <see cref="Filter"/> must be supplied (400 otherwise) -- a filter is resolved to
/// explicit job ids SERVER-SIDE, bounded, before any mutation is attempted; there is
/// no "apply to everything matching" mode that skips that bound. <see cref="Filter"/>
/// shares the exact `state`/`priority`/`component_kind`/`search` vocabulary
/// `GET /runs/{id}/component-jobs` accepts, as arrays rather than comma-joined query
/// strings.
/// </summary>
public sealed record BulkJobActionRequest(
	[property: JsonPropertyName("job_ids")]
	IReadOnlyList<string>? JobIds,

	[property: JsonPropertyName("filter")]
	BulkJobActionFilterRequest? Filter);

/// <summary>The array-valued sibling of the query-string filter <see cref="RunsController"/>'s <c>MapComponentJobFilter</c> parses.</summary>
public sealed record BulkJobActionFilterRequest(
	[property: JsonPropertyName("state")]
	IReadOnlyList<string>? State,

	[property: JsonPropertyName("priority")]
	IReadOnlyList<short>? Priority,

	[property: JsonPropertyName("component_kind")]
	IReadOnlyList<string>? ComponentKind,

	[property: JsonPropertyName("search")]
	string? Search);

/// <summary>
/// One resolved job's outcome within <see cref="BulkJobActionResponse"/> -- honest
/// per-item reporting, never collapsed into a single success/failure boolean (issue
/// #757 AC "report partial conflicts honestly").
/// </summary>
public sealed record BulkJobActionItemResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("outcome")]
	string Outcome);

/// <summary>
/// Response body for <c>POST /api/v1/runs/{id}/jobs/bulk-cancel</c> and
/// <c>bulk-retry</c>. <see cref="ResolvedCount"/> is how many job ids the server
/// resolved (from <see cref="BulkJobActionRequest.JobIds"/> directly, or from
/// <see cref="BulkJobActionRequest.Filter"/>) before attempting anything --
/// <see cref="Items"/> always has exactly that many entries, one per resolved job, in
/// resolution order.
/// </summary>
public sealed record BulkJobActionResponse(
	[property: JsonPropertyName("resolved_count")]
	int ResolvedCount,

	[property: JsonPropertyName("items")]
	IReadOnlyList<BulkJobActionItemResponse> Items);

/// <summary>
/// Request body for <c>POST /api/v1/runs/{id}/resume-blocked</c> (ADR-0008, issue #291).
/// <see cref="CredentialId"/> is the REPLACEMENT credential to swap onto the run's
/// halted jobs -- not the id of the halted credential itself, which the server
/// determines from the run's own blocked job set.
/// </summary>
public sealed record ResumeBlockedRequest(
	[property: JsonPropertyName("credential_id")]
	string? CredentialId);

/// <summary>
/// Response body for <c>POST /api/v1/runs/{id}/resume-blocked</c>. Carries both
/// credential identities so the caller can confirm exactly what was swapped, plus how
/// many jobs were requeued.
/// </summary>
public sealed record ResumeBlockedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("old_credential_id")]
	string OldCredentialId,

	[property: JsonPropertyName("new_credential_id")]
	string NewCredentialId,

	[property: JsonPropertyName("resumed_job_count")]
	int ResumedJobCount);

/// <summary>
/// One target row of <c>GET /api/v1/runs/{id}/artifacts</c> (issue #299, docs/api-contract.md
/// "CKL/HDF download"). Matches <c>frontend/src/screens/results/results.ts</c>'s
/// <c>RunArtifactRow</c> field-for-field -- that module was built against this documented
/// shape ahead of the backend landing it (issue #27/#300). Only <c>scan</c>-type jobs
/// produce artifacts; other job types in the run are simply absent from this list.
/// </summary>
public sealed record RunArtifactResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("target")]
	string Target,

	[property: JsonPropertyName("benchmark")]
	string? Benchmark,

	/// <summary>
	/// <c>false</c> when the HDF is absent or present-but-unparseable -- the CAT counts
	/// below are then <c>null</c> and mean "could not count", NOT zero (issue #299
	/// round-1 blocker: a corrupt HDF must never render as a clean, compliant row). A
	/// consumer must gate on this before trusting the counts.
	/// </summary>
	[property: JsonPropertyName("counts_available")]
	bool CountsAvailable,

	[property: JsonPropertyName("cat_i_open")]
	int? CatIOpen,

	[property: JsonPropertyName("cat_ii_open")]
	int? CatIIOpen,

	[property: JsonPropertyName("cat_iii_open")]
	int? CatIIIOpen,

	/// <summary>Which of <c>hdf</c>/<c>ckl</c> currently have a file on disk for this job -- what <c>GET /jobs/{id}/artifacts/{kind}</c> can actually serve.</summary>
	[property: JsonPropertyName("artifact_kinds")]
	IReadOnlyList<string> ArtifactKinds,

	/// <summary>
	/// STIG Manager upload status (issue #311): <c>jobs.upload_status</c> when the
	/// convert stage has actually attempted an upload (<c>uploaded</c>/<c>failed</c>/
	/// <c>conflict</c>, a real HTTP outcome, not derived), else the same job-state
	/// fallback issue #299 originally shipped (<c>"pending"</c> before the job reaches
	/// its <c>uploaded</c>/<c>done</c> terminal, <c>"not-uploaded"</c> after a
	/// <c>failed</c>/<c>auth-failed</c> job that never reached convert) -- e.g. an
	/// SRG-only job (#24/#309), which never attempts an upload at all.
	/// </summary>
	[property: JsonPropertyName("upload_status")]
	string UploadStatus,

	/// <summary>Short, redacted reason for a <c>failed</c>/<c>conflict</c> upload_status (<c>jobs.upload_detail</c>) -- null when there is nothing to explain.</summary>
	[property: JsonPropertyName("upload_detail")]
	string? UploadDetail);

/// <summary>Response for <c>POST /api/v1/jobs/{id}/stigman-upload-retry</c> (issue #311, the retry shape #297 documents).</summary>
public sealed record JobUploadRetryResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("upload_status")]
	string UploadStatus,

	[property: JsonPropertyName("upload_detail")]
	string? UploadDetail);

/// <summary>
/// One immutable row of <c>GET /api/v1/jobs/{id}/upload-attempts</c> (issue #744
/// remainder: the read side of migration 0062's append-only <c>upload_attempts</c>
/// table, un-stubbing the Results screen's attempt-history drill-down). Ordered
/// oldest-first, mirroring <see cref="Waypoint.Core.Jobs.IJobRunnerRepository.GetUploadAttemptsAsync"/>.
/// <see cref="Endpoint"/>/<see cref="Collection"/> are null only when no STIG Manager
/// connection was resolved at all for that attempt.
/// </summary>
public sealed record UploadAttemptResponse(
	[property: JsonPropertyName("attempt_number")]
	int AttemptNumber,

	[property: JsonPropertyName("endpoint")]
	string? Endpoint,

	[property: JsonPropertyName("collection")]
	string? Collection,

	[property: JsonPropertyName("status")]
	string Status,

	[property: JsonPropertyName("error_detail")]
	string? ErrorDetail,

	[property: JsonPropertyName("attempted_at")]
	DateTimeOffset AttemptedAt);

/// <summary>
/// One waiver row of <c>GET /api/v1/runs/{id}/attestations-applied</c> (issue #299,
/// docs/domain-model.md: "Results lists expired attestations explicitly"). There is no
/// per-control waiver ledger persisted anywhere -- config-docs resolve as whole YAML
/// bodies per (kind, profile, layer), never parsed per-control (issue #266's
/// <c>ConfigDocsController.Resolve</c> doc comment), and the attest stage (#275) records
/// only a free-text <c>jobs.note</c> summary plus a <c>job.log</c> WARN line, not a
/// structured ledger -- issue #306 closed that gap: the attest stage
/// (<c>ScanJobHandler.ExecuteAttestStageAsync</c>) now persists one <c>attestation_snapshots</c>
/// row per scanned target the instant it resolves that target's attestation, and this
/// response is read back from that recorded ledger, never re-resolved. <c>control</c> is
/// always the fixed <see cref="Waypoint.Core.Scans.ScanOptions.AttestationProfile"/>
/// profile name, not a per-control id -- there is no control-enumeration catalog in this
/// codebase to join against; per-control granularity is future work once one exists.
///
/// <para>
/// This response is RECORDED HISTORY, not current resolution: <see cref="AppliedAt"/> is
/// the genuine scan-time timestamp the snapshot was written at, so it is immutable for a
/// given run regardless of any config-doc edit made afterward (the integrity gap issue
/// #299/#305/#306 tracked). <see cref="AttestationUpdatedAt"/> remains the config-doc
/// version's own timestamp -- kept distinct from <see cref="AppliedAt"/> because a
/// doc-edit time and a scan-time application time answer different questions, even
/// though both are now real recorded facts rather than one being faked. There is no
/// <c>derivation</c> field anymore -- the prior <c>"live-resolution"</c> wire marker
/// (issue #299 round-1 blocker) is obsolete now that this is genuinely recorded history.
/// </para>
/// </summary>
public sealed record AppliedAttestationResponse(
	[property: JsonPropertyName("control")]
	string Control,

	[property: JsonPropertyName("scope")]
	string Scope,

	[property: JsonPropertyName("coverage")]
	string Coverage,

	[property: JsonPropertyName("justification")]
	string Justification,

	[property: JsonPropertyName("author")]
	string Author,

	[property: JsonPropertyName("version")]
	int Version,

	/// <summary>The genuine scan-time timestamp this snapshot was recorded at (ISO-8601) -- see the type doc comment.</summary>
	[property: JsonPropertyName("applied_at")]
	string AppliedAt,

	/// <summary>The attestation config-doc version's own timestamp, NOT when it was applied to any scan.</summary>
	[property: JsonPropertyName("attestation_updated_at")]
	string? AttestationUpdatedAt,

	[property: JsonPropertyName("expired")]
	bool Expired);

/// <summary>
/// Request body for <c>POST /api/v1/runs/{id}/purge</c> (issue #594, epic #577).
/// <see cref="Confirmation"/> mirrors <c>RunsController.RemediateConfirmation</c>'s
/// explicit step-up pattern -- purge is destructive and irreversible, so it is never
/// implicit.
/// </summary>
public sealed record RunPurgeRequest(
	[property: JsonPropertyName("confirmation")]
	string? Confirmation);

/// <summary>
/// Response body for both <c>POST /api/v1/runs/{id}/purge</c> and
/// <c>GET /api/v1/runs/{id}/purge</c> -- the same durable status shape either way, so
/// the frontend can poll the GET endpoint with the exact response the POST returned.
/// <see cref="ArtifactsPhase"/>/<see cref="LastError"/> are what a retry affordance
/// keys off: <c>"failed"</c> means retryable by calling <c>POST</c> again.
/// </summary>
public sealed record RunPurgeStatusResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("outcome")]
	string Outcome,

	[property: JsonPropertyName("requested_by")]
	string RequestedBy,

	[property: JsonPropertyName("requested_at")]
	string RequestedAt,

	[property: JsonPropertyName("prior_state")]
	string PriorState,

	[property: JsonPropertyName("db_phase_done")]
	bool DbPhaseDone,

	[property: JsonPropertyName("artifacts_phase")]
	string ArtifactsPhase,

	[property: JsonPropertyName("artifacts_total")]
	int ArtifactsTotal,

	[property: JsonPropertyName("artifacts_deleted")]
	int ArtifactsDeleted,

	[property: JsonPropertyName("last_error")]
	string? LastError,

	[property: JsonPropertyName("completed_at")]
	string? CompletedAt);

/// <summary>
/// Request body for <c>DELETE /api/v1/runs/{id}/history</c> (issue #592, epic #588).
/// <see cref="Confirmation"/> mirrors <see cref="RunPurgeRequest.Confirmation"/>'s
/// explicit step-up pattern -- history deletion is irreversible for that run's
/// operational record, so it is never implicit.
/// </summary>
public sealed record RunHistoryDeletionRequest(
	[property: JsonPropertyName("confirmation")]
	string? Confirmation);

/// <summary>
/// Response body for both <c>DELETE /api/v1/runs/{id}/history</c> and
/// <c>GET /api/v1/runs/{id}/history</c> -- the tombstone, once deletion has
/// completed. Deliberately a sibling of <see cref="RunPurgeStatusResponse"/>, not a
/// shared shape (no artifact-phase progress here: this operation completes
/// synchronously in one database transaction, unlike purge's runner-executed
/// artifact-deletion phase).
/// </summary>
public sealed record RunHistoryDeletionStatusResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("outcome")]
	string Outcome,

	[property: JsonPropertyName("actor")]
	string Actor,

	[property: JsonPropertyName("prior_state")]
	string PriorState,

	[property: JsonPropertyName("occurred_at")]
	string OccurredAt);

/// <summary>
/// Query parameters for <c>GET /api/v1/runs/{id}/events/history</c> (issue #581,
/// ADR-0019): the bounded historical counterpart to the SSE stream. All bind from the
/// query string via <c>[FromQuery]</c>; <see cref="RunsController.MapHistoryQuery"/>
/// validates and converts to <see cref="Waypoint.Core.Jobs.JobEventHistoryQuery"/>.
/// </summary>
public sealed record RunEventHistoryRequest(
	/// <summary>Narrow to one job's events within the run. Omit for the whole run.</summary>
	[property: JsonPropertyName("job_id")]
	string? JobId,

	/// <summary>
	/// Comma-separated allow-list of <c>job_events.event_type</c> values (e.g.
	/// <c>job.log,job.state</c>). Omit for every type.
	/// </summary>
	[property: JsonPropertyName("kind")]
	string? Kind,

	/// <summary>
	/// Comma-separated allow-list of <c>job.log</c> payload <c>severity</c> values
	/// (<c>information</c>/<c>warning</c>/<c>error</c>/<c>verbose</c>/<c>debug</c>).
	/// Omit for every severity. Meaningless (but harmless) on event types that carry no
	/// <c>severity</c> field.
	/// </summary>
	[property: JsonPropertyName("level")]
	string? Level,

	/// <summary>
	/// Opaque page cursor from a previous response's <c>next_cursor</c>. Omit to start
	/// from the beginning of the run's history.
	/// </summary>
	[property: JsonPropertyName("cursor")]
	string? Cursor,

	/// <summary>Page size, 1-200 (default 100) -- see <c>RunsController</c> for the clamp.</summary>
	[property: JsonPropertyName("limit")]
	int? Limit);

/// <summary>
/// One <c>job_events</c> row on the wire for <c>GET /api/v1/runs/{id}/events/history</c>
/// -- deliberately the same shape as the SSE envelope (<c>EventStreamController.WriteEventAsync</c>)
/// so a client can treat live and historical rows identically once received.
/// <see cref="Data"/> is the already-redacted <c>payload</c> column, embedded as raw
/// JSON exactly as SSE does -- this endpoint performs no additional transform and
/// therefore introduces no new leak surface.
/// </summary>
public sealed record JobEventHistoryItemResponse(
	[property: JsonPropertyName("seq")]
	long Seq,

	[property: JsonPropertyName("ts")]
	string Ts,

	[property: JsonPropertyName("type")]
	string Type,

	[property: JsonPropertyName("run_id")]
	string? RunId,

	[property: JsonPropertyName("job_id")]
	string? JobId,

	[property: JsonPropertyName("data")]
	JsonElement Data);

/// <summary>
/// Response body for <c>GET /api/v1/runs/{id}/events/history</c>. <see cref="NextCursor"/>
/// is null exactly when this page reached the end of the run's currently-persisted
/// history -- never a silent truncation (issue #581 AC2); a non-null value must be
/// passed back as the next request's <c>cursor</c> query parameter to continue.
/// </summary>
public sealed record RunEventHistoryResponse(
	[property: JsonPropertyName("items")]
	IReadOnlyList<JobEventHistoryItemResponse> Items,

	[property: JsonPropertyName("next_cursor")]
	string? NextCursor);

/// <summary>
/// Query parameters for <c>GET /api/v1/runs/history</c> (issue #708/#689): the global
/// Jobs History mode's filtered, keyset-cursor-paged terminal-run browsing surface.
/// <c>RunsController.ListRunHistory</c> binds each query-string value individually via
/// its own <c>[FromQuery(Name = "...")]</c> parameter (the same idiom
/// <see cref="RunsController.GetEventHistory"/> uses for <see cref="RunEventHistoryRequest"/>'s
/// shape, rather than binding this whole record directly as one complex
/// <c>[FromQuery]</c> parameter) and constructs this record itself, which
/// <c>RunsController.MapHistoryListQuery</c> then validates and converts to
/// <see cref="Waypoint.Core.Jobs.RunHistoryQuery"/>.
/// </summary>
public sealed record RunHistoryListRequest(
	/// <summary>Comma-separated allow-list of <c>runs.state</c> values. Omit for every state.</summary>
	[property: JsonPropertyName("state")]
	string? State,

	/// <summary>Comma-separated allow-list of <c>runs.run_type</c> values. Omit for every type.</summary>
	[property: JsonPropertyName("run_type")]
	string? RunType,

	/// <summary>Inclusive lower bound on <c>created_at</c> (ISO-8601). Omit for no lower bound.</summary>
	[property: JsonPropertyName("since")]
	string? Since,

	/// <summary>Inclusive upper bound on <c>created_at</c> (ISO-8601). Omit for no upper bound.</summary>
	[property: JsonPropertyName("until")]
	string? Until,

	/// <summary>Opaque page cursor from a previous response's <c>next_cursor</c>. Omit to start from the newest run.</summary>
	[property: JsonPropertyName("cursor")]
	string? Cursor,

	/// <summary>Page size, 1-200 (default 50) -- see <c>RunsController</c> for the clamp.</summary>
	[property: JsonPropertyName("limit")]
	int? Limit);

/// <summary>
/// Response body for <c>GET /api/v1/runs/history</c>. <see cref="NextCursor"/> is null
/// exactly when this page reached the end of matching history -- never a silent
/// truncation, same idiom as <see cref="RunEventHistoryResponse.NextCursor"/>.
/// </summary>
public sealed record RunHistoryListResponse(
	[property: JsonPropertyName("items")]
	IReadOnlyList<RunResponse> Items,

	[property: JsonPropertyName("next_cursor")]
	string? NextCursor);

/// <summary>
/// Issue #757 (epic #726 §7, ADR-0024): one server-side grouped-count row for
/// <c>GET /api/v1/runs/{id}/component-jobs/counts</c> -- the exact number of a run's
/// component jobs sharing this (priority, component_kind, state) triple. The state
/// board sums/slices these to render per-priority totals and per-state breakdowns
/// without ever requesting individual job rows for the counter view.
/// </summary>
public sealed record ComponentJobCountResponse(
	[property: JsonPropertyName("priority")]
	short Priority,

	/// <summary>
	/// The frozen catalog <c>selector_kind</c> (vcenter/esxi/vm/service/target) a
	/// component job's plan item carries, or <c>"unknown"</c> for a job with no
	/// <c>scan_plan_item_id</c> (legacy per-target fan-out, or a non-scan job type).
	/// </summary>
	[property: JsonPropertyName("component_kind")]
	string ComponentKind,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("count")]
	long Count);

/// <summary>
/// One row on the wire for <c>GET /api/v1/runs/{id}/component-jobs</c> -- the
/// cursor-paged, filtered, searchable component-job list the Live Run state board's
/// virtualized item list renders. A subset of <see cref="JobResponse"/>'s fields
/// (no credential attribution -- the list view has no use for it) plus
/// <see cref="ComponentKind"/>, which <see cref="JobResponse"/> does not carry.
/// </summary>
public sealed record ComponentJobResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("job_type")]
	string JobType,

	[property: JsonPropertyName("target_id")]
	string? TargetId,

	[property: JsonPropertyName("target_name")]
	string? TargetName,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("stage")]
	string? Stage,

	[property: JsonPropertyName("priority")]
	short Priority,

	[property: JsonPropertyName("component_kind")]
	string ComponentKind,

	[property: JsonPropertyName("attempt_count")]
	int AttemptCount,

	[property: JsonPropertyName("created_at")]
	string? CreatedAt,

	[property: JsonPropertyName("started_at")]
	string? StartedAt,

	[property: JsonPropertyName("finished_at")]
	string? FinishedAt);

/// <summary>
/// Response body for <c>GET /api/v1/runs/{id}/component-jobs</c>. <see cref="NextCursor"/>
/// is null exactly when this page reached the end of the filtered set -- never a
/// silent truncation, matching every other paged reader's contract in this codebase.
/// </summary>
public sealed record ComponentJobListResponse(
	[property: JsonPropertyName("items")]
	IReadOnlyList<ComponentJobResponse> Items,

	[property: JsonPropertyName("next_cursor")]
	string? NextCursor);

/// <summary>
/// One status bucket of <c>GET /api/v1/runs/{id}/component-results/summary</c> (issue
/// #745): the LATEST attempt per scan_plan_item, grouped by component-result status.
/// <see cref="ComponentCount"/> is a count of COMPONENTS (scan_plan_items), not
/// findings -- CAT/passed/etc. counts are the SUM across those components' latest
/// attempts.
/// </summary>
public sealed record RunResultRollupStatusResponse(
	[property: JsonPropertyName("status")]
	string Status,

	[property: JsonPropertyName("component_count")]
	int ComponentCount,

	[property: JsonPropertyName("cat_i_open")]
	int CatIOpen,

	[property: JsonPropertyName("cat_ii_open")]
	int CatIIOpen,

	[property: JsonPropertyName("cat_iii_open")]
	int CatIIIOpen,

	[property: JsonPropertyName("passed_count")]
	int PassedCount,

	[property: JsonPropertyName("not_applicable_count")]
	int NotApplicableCount,

	[property: JsonPropertyName("not_reviewed_count")]
	int NotReviewedCount,

	[property: JsonPropertyName("skipped_count")]
	int SkippedCount);

/// <summary>
/// Response body for <c>GET /api/v1/runs/{id}/component-results/summary</c>.
/// <see cref="PlannedComponentCount"/> is the run's total accepted plan-item count
/// (scan_plan_items row count) -- a caller computes coverage as
/// <c>sum(by_status[].component_count) vs. planned_component_count</c>; the gap
/// (planned but no result row at all yet) is components still queued/running/legacy,
/// never fabricated into any status bucket.
/// </summary>
public sealed record RunResultRollupResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("planned_component_count")]
	int PlannedComponentCount,

	[property: JsonPropertyName("by_status")]
	IReadOnlyList<RunResultRollupStatusResponse> ByStatus);

/// <summary>
/// One <c>component_result_findings</c> row for <c>GET /api/v1/jobs/{id}/component-results/findings</c>
/// (issue #745). Status/severity pass through exactly as recorded (epic #726 §6) --
/// never re-bucketed, never collapsed. <see cref="RuleId"/>/<see cref="Title"/>/
/// <see cref="Evidence"/> are omitted (not a blank string) when the parser had no
/// XCCDF rule identity, title, or evidence text for this control.
/// </summary>
public sealed record ComponentResultFindingResponse(
	[property: JsonPropertyName("control_id")]
	string ControlId,

	[property: JsonPropertyName("rule_id")]
	string? RuleId,

	[property: JsonPropertyName("title")]
	string? Title,

	[property: JsonPropertyName("severity")]
	string Severity,

	[property: JsonPropertyName("status")]
	string Status,

	[property: JsonPropertyName("evidence")]
	string? Evidence);

/// <summary>
/// Response body for <c>GET /api/v1/jobs/{id}/component-results/findings</c>. Limit/
/// offset paged -- a single attempt's finding count is bounded by one benchmark's
/// control count (never an unboundedly growing history), so this endpoint uses the
/// same <c>?limit&amp;offset</c> + <c>X-Total-Count</c> HEADER idiom as
/// <c>GET /runs</c> (docs/api-contract.md Conventions; see
/// <see cref="Waypoint.Core.Pagination.PageRequest"/>'s doc comment) rather than the
/// cursor idiom `/runs/{id}/events/history` uses for genuinely unbounded history. The
/// total matching-finding count travels ONLY in the <c>X-Total-Count</c> response
/// header, never in this body -- no list endpoint in this API carries an in-body
/// count. <see cref="AttemptNumber"/>/<see cref="ComponentResultStatus"/> describe
/// WHICH attempt these findings belong to (always the job's latest) so a caller never
/// has to guess. <c>null</c> attempt fields mean the job has no recorded
/// component-result attempt at all yet -- honest-empty, distinct from "attempt
/// exists, zero findings".
/// </summary>
public sealed record ComponentResultFindingsResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("attempt_number")]
	int? AttemptNumber,

	[property: JsonPropertyName("component_result_status")]
	string? ComponentResultStatus,

	[property: JsonPropertyName("items")]
	IReadOnlyList<ComponentResultFindingResponse> Items,

	[property: JsonPropertyName("limit")]
	int Limit,

	[property: JsonPropertyName("offset")]
	int Offset);

/// <summary>
/// One <c>component_result_artifacts</c> row for <c>GET /api/v1/jobs/{id}/component-results/artifacts</c>
/// (issue #745). Metadata only -- digest/size as recorded at write time; this endpoint
/// never streams the artifact's bytes (that stays on the existing
/// <c>GET /jobs/{id}/artifacts/{kind}</c> route, which serves only the two byte-
/// downloadable kinds `hdf`/`ckl` documented in docs/api-contract.md today).
/// </summary>
public sealed record ComponentResultArtifactResponse(
	[property: JsonPropertyName("kind")]
	string Kind,

	[property: JsonPropertyName("path")]
	string Path,

	[property: JsonPropertyName("digest")]
	string Digest,

	[property: JsonPropertyName("size_bytes")]
	long SizeBytes);

/// <summary>
/// Response body for <c>GET /api/v1/jobs/{id}/component-results/artifacts</c>. Unpaged
/// -- bounded by the closed 5-value artifact-kind vocabulary per attempt. Same
/// "describes which attempt" honesty as <see cref="ComponentResultFindingsResponse"/>.
/// </summary>
public sealed record ComponentResultArtifactsResponse(
	[property: JsonPropertyName("job_id")]
	string JobId,

	[property: JsonPropertyName("attempt_number")]
	int? AttemptNumber,

	[property: JsonPropertyName("component_result_status")]
	string? ComponentResultStatus,

	[property: JsonPropertyName("items")]
	IReadOnlyList<ComponentResultArtifactResponse> Items);
