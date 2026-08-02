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
