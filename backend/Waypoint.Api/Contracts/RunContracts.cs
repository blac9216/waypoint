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
	EphemeralCredentialRequest? Credential = null);

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
