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

namespace Waypoint.Api.Contracts;

/// <summary>Request body for <c>POST /api/v1/schedules</c>.</summary>
public sealed record ScheduleCreateRequest(
	[property: JsonPropertyName("name")]
	string Name,

	/// <summary>Read-only job type only -- scan, discover, credential-test, catalog-index (docs/api-contract.md `/schedules`).</summary>
	[property: JsonPropertyName("job_type")]
	string JobType,

	[property: JsonPropertyName("cron_expression")]
	string CronExpression,

	/// <summary>Same site/target-selection JSON shape <c>POST /runs</c> accepts for a scan run's <c>scope</c>.</summary>
	[property: JsonPropertyName("scope")]
	string? Scope,

	[property: JsonPropertyName("credential_id")]
	Guid? CredentialId);

/// <summary>Request body for <c>PUT /api/v1/schedules/{id}</c>. A null field leaves the corresponding column unchanged; an empty-string <c>credential_id</c> is not meaningful (use <c>clear_credential</c>).</summary>
public sealed record ScheduleUpdateRequest(
	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("cron_expression")]
	string? CronExpression,

	[property: JsonPropertyName("scope")]
	string? Scope,

	[property: JsonPropertyName("credential_id")]
	Guid? CredentialId,

	[property: JsonPropertyName("clear_credential")]
	bool ClearCredential,

	[property: JsonPropertyName("enabled")]
	bool? Enabled);

/// <summary>
/// Wire shape for a schedule, including the initiator/attribution fields
/// docs/domain-model.md's Scheduling section requires ("scheduled" as initiator
/// alongside the schedule's creator) -- <see cref="CreatedBy"/> is the creator;
/// "scheduled" itself is the fixed initiator recorded on every dispatched RUN, not a
/// per-schedule field (see <c>ScheduleDispatchService.ScheduledInitiator</c>).
/// </summary>
public sealed record ScheduleResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("job_type")]
	string JobType,

	[property: JsonPropertyName("cron_expression")]
	string CronExpression,

	[property: JsonPropertyName("scope")]
	string Scope,

	[property: JsonPropertyName("credential_id")]
	string? CredentialId,

	[property: JsonPropertyName("enabled")]
	bool Enabled,

	/// <summary>Non-null when auto-paused (docs/api-contract.md: "auto-paused states in air-gapped mode for depot kinds"); independent of <see cref="Enabled"/>.</summary>
	[property: JsonPropertyName("paused_reason")]
	string? PausedReason,

	[property: JsonPropertyName("next_run_at")]
	string? NextRunAt,

	[property: JsonPropertyName("last_run_at")]
	string? LastRunAt,

	[property: JsonPropertyName("last_run_id")]
	string? LastRunId,

	[property: JsonPropertyName("last_result")]
	string? LastResult,

	/// <summary>The schedule's creator -- docs/domain-model.md Scheduling: "record 'scheduled' as the initiator alongside the schedule's creator".</summary>
	[property: JsonPropertyName("created_by")]
	string CreatedBy,

	[property: JsonPropertyName("created_at")]
	string CreatedAt,

	[property: JsonPropertyName("updated_at")]
	string UpdatedAt);
