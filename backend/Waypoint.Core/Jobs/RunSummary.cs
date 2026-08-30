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

namespace Waypoint.Core.Jobs;

/// <summary>
/// Run row projected for the REST surface (GET /runs/{id}).
/// Matches docs/api-contract.md "Runs &amp; jobs" — header, progress, pass/fail/na,
/// per-queue status including <c>blocked</c>.
/// </summary>
public sealed record RunSummary(
	Guid Id,
	string RunType,
	string State,
	bool Paused,
	bool Blocked,
	string? BlockedReason,
	string ScopeJson,
	Guid? CredentialId,
	string? InitiatedBy,
	Guid? ScheduleId,
	string? CreatedAt,
	string? StartedAt,
	string? CompletedAt,
	int JobCount,
	int JobCountQueued,
	int JobCountRunning,
	int JobCountCompleted,
	int JobCountFailed,
	int JobCountBlocked,
	/// <summary>
	/// Issue #593 (migration 0041): non-secret attribution snapshotted onto this row
	/// when a terminal run's credential was detached and deleted -- null until then,
	/// and null forever for a run whose credential still exists (<see cref="CredentialId"/>
	/// is the live reference in that case). Display fields only, deliberately NOT the
	/// credential's purpose/binding (epic #577's trajectory note).
	/// </summary>
	string? CredentialName = null,
	string? CredentialType = null,
	string? CredentialUsername = null,
	/// <summary>
	/// Issue #1140: true when this run's coverage is not provably complete --
	/// mirrors <c>RunsController.GetComponentResultsSummary</c>'s own three-way
	/// predicate exactly (no recorded scan plan, a plan-time coverage omission, or
	/// at least one component that evaluated zero controls), computed in bulk by
	/// <c>JobQueueRepository.RunSummaryProjectionSql</c> so <see cref="GetRunAsync"/>-
	/// style callers and the run list/history surfaces carry the same signal the
	/// component-results summary endpoint already exposed (PR #1139) without a
	/// second query per run. False for a non-scan run type (no scan plan is ever
	/// recorded for those, so this is intentionally uninformative there -- callers
	/// gate display on <see cref="RunType"/> the same way they already do for
	/// other compliance-only fields).
	/// </summary>
	bool CoverageIncomplete = false);
