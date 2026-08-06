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

	/// <summary>Optional: who initiated the run (usually populated from auth context).</summary>
	[property: JsonPropertyName("initiated_by")]
	string? InitiatedBy);

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
