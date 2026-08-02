namespace Waypoint.Api.Contracts;

/// <summary>Request body for <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Response body for a successful login.</summary>
public sealed record LoginResponse(string Token, string Role, DateTimeOffset ExpiresAt);

/// <summary>Response body for <c>GET /api/v1/auth/me</c>.</summary>
public sealed record CurrentUserResponse(string Username, string Role);
