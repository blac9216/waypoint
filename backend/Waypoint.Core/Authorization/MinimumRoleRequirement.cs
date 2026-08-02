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

using Microsoft.AspNetCore.Authorization;

namespace Waypoint.Core.Authorization;

/// <summary>
/// Authorization requirement satisfied when the current principal's
/// <see cref="WaypointClaimTypes.Role"/> claim is <paramref name="MinimumRole"/> or
/// higher in the Viewer &lt; Cyber &lt; Operator &lt; Admin hierarchy.
/// </summary>
public sealed class MinimumRoleRequirement : IAuthorizationRequirement
{
	public MinimumRoleRequirement(WaypointRole minimumRole)
	{
		MinimumRole = minimumRole;
	}

	public WaypointRole MinimumRole { get; }
}
