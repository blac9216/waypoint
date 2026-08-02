namespace Waypoint.Api.Contracts;

/// <summary>A single row from the scaffold stub list — stands in for a real resource until one lands.</summary>
public sealed record ScaffoldStubItem(string Id, string Name);

/// <summary>Response body for <c>GET /api/v1/_stub/admin-only</c>.</summary>
public sealed record ScaffoldStubMessage(string Message);
