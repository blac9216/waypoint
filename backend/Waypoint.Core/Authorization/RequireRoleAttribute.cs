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
