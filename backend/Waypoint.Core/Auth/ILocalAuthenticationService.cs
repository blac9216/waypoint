namespace Waypoint.Core.Auth;

/// <summary>
/// The dev-grade local authentication abstraction (ADR-0004 rollout note). This is the
/// seam issue #29 replaces with Keycloak OIDC token validation — callers (the login
/// endpoint, the authentication handler) depend on this interface only, never on the
/// in-memory implementation directly, so swapping it is a DI registration change, not
/// a rewrite.
/// </summary>
public interface ILocalAuthenticationService
{
	/// <summary>Validates a username/password pair and issues a new session, or returns null on any failure.</summary>
	LocalSession? Authenticate(string username, string password);

	/// <summary>Resolves a bearer token to its session, or returns null if the token is unknown or expired.</summary>
	LocalSession? ValidateToken(string token);
}
