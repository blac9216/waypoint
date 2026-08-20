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

namespace Waypoint.Core.SystemState;

/// <summary>
/// Storage for depot-sync status (issue #241, follow-up to #226/#240). Unlike
/// <see cref="IApplianceStateRepository"/> and <see cref="IWorkerRegistryReader"/>,
/// there is no dedicated table for this -- <c>catalog-index</c> runs are already
/// recorded in the existing <c>runs</c> table (api-contract.md "Runs &amp; jobs";
/// <c>POST /catalog/sync</c> creates one, <c>Waypoint.Api.Controllers.CatalogController</c>),
/// so "depot last-sync" is derived by reading the most recent completed one rather
/// than tracked as separate appliance state. One implementation
/// (<c>Waypoint.Infrastructure.SystemState.DepotSyncStatusRepository</c>, same
/// plain-Npgsql convention as the sibling repositories in this namespace).
/// </summary>
public interface IDepotSyncStatusRepository
{
	/// <summary>
	/// The most recent <c>catalog-index</c> run that reached a completed state
	/// (<c>completed</c> or <c>completed_with_failures</c> -- either means the sync
	/// actually ran to completion, api-contract.md's `runs_state_check`), or
	/// <c>null</c> when no <c>catalog-index</c> run has ever completed (fresh
	/// appliance, or one that has only run scans/discovers so far).
	/// </summary>
	Task<DepotSyncStatus?> GetLastSyncAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The depot's last completed <c>catalog-index</c> run, projected for <c>GET
/// /system</c>'s "depot sync" field.
/// </summary>
/// <param name="CompletedAt">When the run finished (its <c>runs.completed_at</c>).</param>
/// <param name="Succeeded">True for <c>completed</c>; false for <c>completed_with_failures</c> -- the sync still ran, but at least one job in it did not.</param>
public sealed record DepotSyncStatus(DateTimeOffset CompletedAt, bool Succeeded);
