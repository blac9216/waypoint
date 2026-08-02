using Waypoint.Core.Authorization;

namespace Waypoint.Core.Auth;

/// <summary>An issued local-auth session: the token handed to the client plus the identity it represents.</summary>
/// <param name="Token">Opaque bearer token; the only thing the client presents on subsequent requests.</param>
/// <param name="Username">The authenticated username.</param>
/// <param name="Role">The role granted to this session.</param>
/// <param name="ExpiresAt">UTC expiry; the session is invalid at or after this instant.</param>
public sealed record LocalSession(string Token, string Username, WaypointRole Role, DateTimeOffset ExpiresAt);
