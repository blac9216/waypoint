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

/// <summary>Wire shape for one <c>audit_log</c> row (docs/api-contract.md `/audit`).</summary>
public sealed record AuditEntryResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("event_type")]
	string EventType,

	[property: JsonPropertyName("actor")]
	string Actor,

	[property: JsonPropertyName("credential_id")]
	string? CredentialId,

	[property: JsonPropertyName("job_id")]
	string? JobId,

	[property: JsonPropertyName("run_id")]
	string? RunId,

	[property: JsonPropertyName("detail")]
	string Detail,

	[property: JsonPropertyName("occurred_at")]
	string OccurredAt);
