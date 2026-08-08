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
/// Storage for the <c>appliance_state</c> singleton (issue #226). One implementation
/// (<c>Waypoint.Infrastructure.SystemState.ApplianceStateRepository</c>, plain Npgsql --
/// same "no ORM for this layer" convention as
/// <c>Waypoint.Infrastructure.Catalog.DepotArtifactRepository</c>).
/// </summary>
public interface IApplianceStateRepository
{
	/// <summary>
	/// Reads the singleton row. Migration 0001 seeds it unconditionally
	/// (<c>id = 1</c>), so a null return means the row was deleted out of band rather
	/// than "not yet configured" -- callers should treat that as a server error, not a
	/// normal empty state.
	/// </summary>
	Task<ApplianceState?> GetAsync(CancellationToken cancellationToken);
}
