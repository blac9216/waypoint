namespace Waypoint.Core.Authorization;

/// <summary>
/// Claim type constants shared by every authentication scheme (local session today,
/// Keycloak OIDC from issue #29 on). Keeping the claim type here — rather than
/// hardcoding a string at each call site — is part of the swappable-auth seam: a new
/// identity provider only has to populate these claims the same way.
/// </summary>
public static class WaypointClaimTypes
{
	/// <summary>The authenticated principal's <see cref="WaypointRole"/>, serialized as its enum name.</summary>
	public const string Role = "waypoint:role";
}
