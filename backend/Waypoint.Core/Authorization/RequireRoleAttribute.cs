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
/// Base for the role guard attributes. Decorating an endpoint with a
/// <c>Require*Role</c> attribute requires both authentication and a
/// <see cref="WaypointClaimTypes.Role"/> claim at or above the named role — see
/// <see cref="MinimumRoleRequirement"/>.
/// </summary>
public abstract class RequireRoleAttribute : AuthorizeAttribute
{
	protected RequireRoleAttribute(WaypointRole role)
	{
		Policy = WaypointAuthorizationPolicies.MinimumRole(role);
	}
}

/// <summary>Requires Viewer or higher (i.e. any authenticated role).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireViewerRoleAttribute : RequireRoleAttribute
{
	public RequireViewerRoleAttribute() : base(WaypointRole.Viewer)
	{
	}
}

/// <summary>Requires Cyber or higher.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireCyberRoleAttribute : RequireRoleAttribute
{
	public RequireCyberRoleAttribute() : base(WaypointRole.Cyber)
	{
	}
}

/// <summary>Requires Operator or higher.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireOperatorRoleAttribute : RequireRoleAttribute
{
	public RequireOperatorRoleAttribute() : base(WaypointRole.Operator)
	{
	}
}

/// <summary>Requires Admin.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireAdminRoleAttribute : RequireRoleAttribute
{
	public RequireAdminRoleAttribute() : base(WaypointRole.Admin)
	{
	}
}
