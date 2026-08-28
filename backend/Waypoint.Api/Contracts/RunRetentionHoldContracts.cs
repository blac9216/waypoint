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
/// Issue #784 (epic #726): retention-hold wire contracts. Deliberately a SEPARATE
/// file from <c>RunContracts.cs</c> (not an addition to <see cref="RunResponse"/>)
/// so hold status is a dedicated, linked read endpoint -- mirroring how
/// <see cref="RunPurgeStatusResponse"/>/<see cref="RunHistoryDeletionStatusResponse"/>
/// already sit beside <see cref="RunResponse"/> rather than inside it, and ADR-0019
/// decision 4's "operational details link to those resources rather than reproducing
/// their management actions."
/// </summary>
public sealed record RunRetentionHoldRequest(
	[property: JsonPropertyName("reason")]
	string? Reason);

/// <summary>
/// Response body for <c>POST</c>/<c>DELETE</c>/<c>GET /api/v1/runs/{id}/retention-hold</c>.
/// Always 200 with <see cref="Active"/> reflecting the current state -- never 404 --
/// so the run-details surface can always ask "is this run held" without first
/// knowing whether a hold was ever placed (honest-empty read, ADR-0019 decision 3's
/// idiom applied to this narrower surface).
/// </summary>
public sealed record RunRetentionHoldResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("active")]
	bool Active,

	[property: JsonPropertyName("reason")]
	string? Reason,

	[property: JsonPropertyName("placed_by")]
	string? PlacedBy,

	[property: JsonPropertyName("placed_at")]
	string? PlacedAt);
