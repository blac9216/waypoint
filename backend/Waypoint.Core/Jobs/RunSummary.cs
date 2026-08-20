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
	int JobCountBlocked);
