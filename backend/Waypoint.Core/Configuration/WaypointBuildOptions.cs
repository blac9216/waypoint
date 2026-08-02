namespace Waypoint.Core.Configuration;

/// <summary>
/// Version/build metadata surfaced by <c>GET /api/v1/health</c>. Bound from the
/// <c>Build</c> configuration section, which the container build populates via
/// environment variables (<c>Build__Version</c>, <c>Build__Sha</c>,
/// <c>Build__BuiltAt</c>) rather than baking values into source — deploy/CI concerns
/// stay out of the backend project. Defaults describe an un-stamped dev build.
/// </summary>
public sealed class WaypointBuildOptions
{
	public const string SectionName = "Build";

	/// <summary>Application/release version (e.g. a semver or milestone tag).</summary>
	public string Version { get; set; } = "0.0.0-dev";

	/// <summary>Source commit SHA the running image was built from.</summary>
	public string Sha { get; set; } = "unknown";

	/// <summary>UTC build timestamp, ISO-8601, as a string so an unset value stays "unknown" rather than a misleading default date.</summary>
	public string BuiltAt { get; set; } = "unknown";
}
