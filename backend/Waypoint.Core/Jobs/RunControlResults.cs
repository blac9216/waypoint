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

/// <summary>A run's current queue-level flags (ADR-0008: "paused and blocked are queue-level flags, not run states").</summary>
public sealed record RunQueueState(bool Paused, bool Blocked, string? BlockedReason);

/// <summary>
/// The effect of aborting a run: queued (and blocked) jobs are cancelled immediately by
/// the database write; in-flight jobs cannot be force-stopped by SQL alone, so their
/// ids come back for the caller to request cooperative cancellation on (see
/// <c>JobDispatcherHostedService</c>).
/// </summary>
public sealed record AbortRunResult(IReadOnlyList<Guid> CancelledJobIds, IReadOnlyList<Guid> InFlightJobIds);

/// <summary>
/// The effect of an <c>auth-failed</c> completion tripping the consecutive-failure
/// threshold (ADR-0008: "N consecutive auth failures... halt that queue"). Empty lists
/// mean the threshold was not (yet) tripped.
/// </summary>
public sealed record AuthFailureHaltResult(IReadOnlyList<Guid> BlockedRunIds, IReadOnlyList<Guid> BlockedJobIds);
