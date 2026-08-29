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

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): the Admin-configurable evidence retention
/// period wire contract, backing <c>GET</c>/<c>PUT /api/v1/retention-policy</c>.
/// </summary>
public sealed record RetentionPolicyUpdateRequest(
	[property: JsonPropertyName("evidence_retention_days")]
	int? EvidenceRetentionDays);

/// <summary>
/// Response body for <c>GET</c>/<c>PUT /api/v1/retention-policy</c>. <see cref="UpdatedBy"/>
/// is <c>null</c> when the policy still holds the seeded 180-day default and has
/// never been changed by an Admin.
/// </summary>
public sealed record RetentionPolicyResponse(
	[property: JsonPropertyName("evidence_retention_days")]
	int EvidenceRetentionDays,

	[property: JsonPropertyName("updated_by")]
	string? UpdatedBy,

	[property: JsonPropertyName("updated_at")]
	string UpdatedAt);
