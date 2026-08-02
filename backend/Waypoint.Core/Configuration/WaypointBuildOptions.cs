// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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
