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

namespace Waypoint.Core.Authorization;

/// <summary>
/// Single source of truth for authorization policy names, shared by policy
/// registration (composition root) and the <c>Require*Role</c> attributes so the two
/// can never drift apart.
/// </summary>
public static class WaypointAuthorizationPolicies
{
	/// <summary>Returns the policy name for "this role or higher", e.g. <c>MinimumRole:Cyber</c>.</summary>
	public static string MinimumRole(WaypointRole role)
	{
		return $"MinimumRole:{role}";
	}
}
