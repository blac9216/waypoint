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
