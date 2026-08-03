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
/// Database startup behavior, bound from the <c>Database</c> section. The connection
/// string itself lives in the standard ASP.NET Core <c>ConnectionStrings:Waypoint</c>
/// slot (<c>IConfiguration.GetConnectionString("Waypoint")</c>), not here.
/// </summary>
public sealed class WaypointDatabaseOptions
{
	public const string SectionName = "Database";

	/// <summary>
	/// Whether the host applies pending schema migrations during startup, before the
	/// app begins accepting traffic (ADR-0009 expects this for the update flow; see
	/// <see cref="SectionName"/> and <c>Waypoint.Infrastructure.Data.ISchemaMigrator</c>).
	/// Defaults to <c>true</c>; the "Testing" environment configuration turns it off
	/// because the in-process test host (<c>WebApplicationFactory</c>) has no Postgres
	/// to migrate against.
	/// </summary>
	public bool RunMigrationsOnStartup { get; set; } = true;
}
