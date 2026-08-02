namespace Waypoint.Api.Contracts;

/// <summary>Response body for <c>GET /api/v1/health</c>.</summary>
public sealed record HealthResponse(string Status, string Version, string Sha, string BuiltAt, DateTimeOffset Timestamp);
