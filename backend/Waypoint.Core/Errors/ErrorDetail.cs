namespace Waypoint.Core.Errors;

/// <summary>
/// The <c>error</c> object of the documented envelope
/// (<c>docs/api-contract.md</c> Conventions): <c>{ "error": { code, message, detail? } }</c>.
/// Serialized snake_case by the API's global JSON options.
/// </summary>
/// <param name="Code">Stable, machine-readable error code (e.g. <c>mode_unavailable</c>).</param>
/// <param name="Message">Human-readable summary, safe to show a user.</param>
/// <param name="Detail">Optional extra context; omitted from the payload when null.</param>
public sealed record ErrorDetail(string Code, string Message, string? Detail = null);

/// <summary>The envelope itself — the sole top-level shape for every error response.</summary>
public sealed record ErrorResponse(ErrorDetail Error);
