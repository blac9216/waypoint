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
