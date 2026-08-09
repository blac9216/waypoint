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

namespace Waypoint.Core.ConfigDocs;

/// <summary>
/// One persisted, at-scan-time record of what the attest stage
/// (<c>ScanJobHandler.ExecuteAttestStageAsync</c>, #275/#302) resolved for a single
/// (job, target) -- issue #306's real fix for the gap PR #305 could only disclose:
/// <c>GET /runs/{id}/attestations-applied</c> used to re-resolve the config-doc LIVE at
/// request time, so editing a waiver after a run silently rewrote what that endpoint
/// reported about a historical run. This record is written once, at attest-stage
/// execution time, and never edited afterward (<c>attestation_snapshots</c> is
/// append-only -- see the 0021 migration), so a historical run's answer is immutable
/// regardless of any later edit to the config-doc.
///
/// Granularity is PER-TARGET, matching the live-resolution endpoint this replaces --
/// per-control is future work once a control-enumeration catalog exists (issue #306's
/// AC notes this explicitly; there is no such catalog in this codebase today).
///
/// <see cref="DocId"/>/<see cref="DocVersion"/>/<see cref="DocAuthor"/>/
/// <see cref="DocVersionCreatedAt"/> are all null together when no config-doc applied at
/// any layer (<see cref="Applied"/> is then also false) -- there was nothing to record
/// beyond the fact that no waiver was found.
/// </summary>
public sealed record AttestationSnapshot(
	Guid Id,
	Guid RunId,
	Guid JobId,
	Guid TargetId,
	string Profile,
	string Scope,
	Guid? DocId,
	int? DocVersion,
	string? DocAuthor,
	DateTimeOffset? DocVersionCreatedAt,
	bool Applied,
	bool Expired,
	DateTimeOffset AppliedAt);
