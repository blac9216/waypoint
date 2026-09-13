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
/// Issue #1531 (epic #1180, split from #1042): the Admin-configurable disk-admission
/// reserve wire contract, backing <c>GET</c>/<c>PUT /api/v1/disk-admission-policy</c> --
/// mirrors <see cref="RetentionPolicyUpdateRequest"/>'s shape exactly, per this
/// issue's own AC.
/// </summary>
public sealed record DiskAdmissionPolicyUpdateRequest(
	[property: JsonPropertyName("reserve_bytes")]
	long? ReserveBytes);

/// <summary>
/// Response body for <c>GET</c>/<c>PUT /api/v1/disk-admission-policy</c>.
/// <see cref="UpdatedBy"/> is <c>null</c> when the policy still holds the seeded
/// default and has never been changed by an Admin.
/// </summary>
public sealed record DiskAdmissionPolicyResponse(
	[property: JsonPropertyName("reserve_bytes")]
	long ReserveBytes,

	[property: JsonPropertyName("updated_by")]
	string? UpdatedBy,

	[property: JsonPropertyName("updated_at")]
	string UpdatedAt);
