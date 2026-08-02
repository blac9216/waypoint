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
