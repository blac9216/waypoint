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
/// response, never written past <see cref="Waypoint.Core.Secrets.IEphemeralCredentialCache"/>.
/// </summary>
public sealed record EphemeralCredentialRequest(
	[property: JsonPropertyName("kind")]
	string Kind,

	/// <summary>The protocol-level login (e.g. vSphere SSO username) the target job presents.</summary>
	[property: JsonPropertyName("username")]
	string Username,

	/// <summary>The caller's own password/secret. Held in memory for the run's lifetime only -- see docs/security.md "in-play redaction".</summary>
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
	int JobCountBlocked);

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
	string? FinishedAt);

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
	/// STIG Manager upload status derived from job state (docs/api-contract.md): the real
	/// upload integration is issue #25, not yet built, so this is <c>"pending"</c> until
	/// the job reaches its <c>uploaded</c>/<c>done</c> terminal ("artifacts ready", per
	/// <c>ScanJobHandler</c>'s doc comment) and <c>"not-uploaded"</c> on a failed job --
	/// never <c>"conflict"</c>, which only a real upload attempt can report.
	/// </summary>
	[property: JsonPropertyName("upload_status")]
	string UploadStatus);

/// <summary>
/// One waiver row of <c>GET /api/v1/runs/{id}/attestations-applied</c> (issue #299,
/// docs/domain-model.md: "Results lists expired attestations explicitly"). There is no
/// per-control waiver ledger persisted anywhere -- config-docs resolve as whole YAML
/// bodies per (kind, profile, layer), never parsed per-control (issue #266's
/// <c>ConfigDocsController.Resolve</c> doc comment), and the attest stage (#275) records
/// only a free-text <c>jobs.note</c> summary plus a <c>job.log</c> WARN line, not a
/// structured ledger. This response is therefore derived live, per scanned target in the
/// run, by re-running the same <c>ConfigDocResolver.Resolve</c> the attest stage itself
/// used -- one row per (target, resolved-or-expired-attestation) pair, mirroring exactly
/// what <c>frontend/src/screens/results/results.ts</c>'s <c>fetchAttestationResolution</c>
/// stand-in already does client-side against <c>/config-docs/resolve</c> (see that
/// module's doc comment). <c>control</c> is always the fixed <see cref="Waypoint.Core.Scans.ScanOptions.AttestationProfile"/>
/// profile name, not a per-control id -- there is no control-enumeration catalog in this
/// codebase to join against (see this PR's body for the acknowledged gap).
///
/// <para>
/// This response is CURRENT RESOLUTION, not recorded at-scan-time history: <see cref="Derivation"/>
/// is the constant <c>"live-resolution"</c> and <see cref="ResolvedAt"/> is when THIS request
/// resolved it, so an API consumer can tell that an attestation edited after the run silently
/// rewrites what this endpoint reports (issue #299 round-1 blocker). There is deliberately no
/// <c>applied_at</c> field -- the config-doc's last-edit time (<see cref="AttestationUpdatedAt"/>)
/// is exactly that, a doc-edit time, and must not be presented as a scan-time application time.
/// The persisted at-scan-time ledger that WOULD carry a true <c>applied_at</c> is tracked as
/// issue #306.
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

	/// <summary>Always <c>"live-resolution"</c>: derived by re-resolving the config-doc at request time, not read from a recorded scan-time ledger (#306).</summary>
	[property: JsonPropertyName("derivation")]
	string Derivation,

	/// <summary>When THIS request resolved the attestation (ISO-8601). NOT a scan-time application time -- see <see cref="Derivation"/>.</summary>
	[property: JsonPropertyName("resolved_at")]
	string ResolvedAt,

	/// <summary>The attestation config-doc's own last-modified time (its version timestamp), NOT when it was applied to any scan.</summary>
	[property: JsonPropertyName("attestation_updated_at")]
	string? AttestationUpdatedAt,

	[property: JsonPropertyName("expired")]
	bool Expired);
