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
/// Succeeds a <see cref="MinimumRoleRequirement"/> when the current principal carries a
/// <see cref="WaypointClaimTypes.Role"/> claim at or above the required role. A missing
/// or unparseable claim never satisfies the requirement (fails closed).
/// </summary>
public sealed class MinimumRoleAuthorizationHandler : AuthorizationHandler<MinimumRoleRequirement>
{
	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		MinimumRoleRequirement requirement)
	{
		string? roleClaim = context.User.FindFirst(WaypointClaimTypes.Role)?.Value;

		if (roleClaim is not null
			&& Enum.TryParse(roleClaim, ignoreCase: true, out WaypointRole role)
			&& role >= requirement.MinimumRole)
		{
			context.Succeed(requirement);
		}

		return Task.CompletedTask;
	}
}
