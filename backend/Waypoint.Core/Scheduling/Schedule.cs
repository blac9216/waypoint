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

using Waypoint.Core.Authorization;

namespace Waypoint.Core.Scheduling;

/// <summary>
/// A persisted <c>schedules</c> row (migration 0030, issue #31). <see cref="JobType"/>
/// is restricted, both by <c>schedules_job_type_check</c> and by
/// <see cref="ScheduleJobTypes"/>'s server-side validation, to the read-only subset of
/// <c>jobs_job_type_check</c> -- scan/discover/credential-test/catalog-index.
/// <see cref="Enabled"/> is the operator's own on/off switch (from <c>PUT
/// /schedules/{id}</c>); <see cref="PausedReason"/> is set only by the auto-pause path
/// (docs/api-contract.md: "auto-paused states in air-gapped mode for depot kinds") and
/// is independent of it -- a schedule can be operator-enabled but appliance-auto-paused
/// at the same time, and the dispatcher skips a due schedule if either is true.
/// </summary>
public sealed record Schedule(
	Guid Id,
	string Name,
	string JobType,
	string CronExpression,
	string ScopeJson,
	Guid? CredentialId,
	bool Enabled,
	string? PausedReason,
	DateTimeOffset? NextRunAt,
	DateTimeOffset? LastRunAt,
	Guid? LastRunId,
	string? LastResult,
	string CreatedBy,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>
/// The closed, read-only-only set of job types a schedule may target
/// (docs/api-contract.md `/schedules`: "Read-only job types only (server-rejects
/// `remediate` etc. — domain rule)"). Mirrors <c>schedules_job_type_check</c>
/// (migration 0030) so the API rejects an invalid <c>job_type</c> with a clear error
/// before ever reaching the database constraint. <see cref="DepotKinds"/> is the subset
/// that auto-pauses in disconnected mode (docs/api-contract.md: "auto-paused states in
/// air-gapped mode for depot kinds") -- exactly <see cref="Waypoint.Core.Jobs.JobCapabilities.Download"/>'s
/// read-only member, <c>catalog-index</c>.
/// </summary>
public static class ScheduleJobTypes
{
	public const string Scan = "scan";
	public const string Discover = "discover";
	public const string CredentialTest = "credential-test";
	public const string CatalogIndex = "catalog-index";

	public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
	{
		Scan, Discover, CredentialTest, CatalogIndex
	};

	/// <summary>Depot (download-domain) job types -- auto-paused when the appliance is in disconnected mode.</summary>
	public static readonly IReadOnlySet<string> DepotKinds = new HashSet<string>(StringComparer.Ordinal)
	{
		CatalogIndex
	};

	/// <summary>
	/// The minimum <see cref="WaypointRole"/> a caller must hold to create, modify, or
	/// delete a schedule of each job type. A schedule is a deferred, recurring instance
	/// of a direct action, so its write floor MUST equal the role that action's own
	/// endpoint requires -- otherwise the scheduling surface becomes a privilege-escalation
	/// path (issue #517 review): a Cyber user could schedule -- and the dispatcher would
	/// then execute -- a job they are forbidden from triggering directly. The mapping is:
	/// <list type="bullet">
	/// <item><c>scan</c> -> Cyber -- matches <c>RunsController.CreateRun</c>'s scan floor.</item>
	/// <item><c>discover</c> -> Admin -- matches <c>POST /targets/{id}/discover</c> (<c>DiscoveryController</c>, <c>[RequireAdminRole]</c>).</item>
	/// <item><c>credential-test</c> -> Admin -- matches <c>POST /credentials/{id}/test</c> (<c>CredentialsController</c>, <c>[RequireAdminRole]</c>).</item>
	/// <item><c>catalog-index</c> -> Admin -- matches <c>POST /catalog/sync</c> (<c>CatalogController</c>, <c>[RequireAdminRole]</c>).</item>
	/// </list>
	/// This map is the single source of truth: every member of <see cref="All"/> has an
	/// entry, and <see cref="RequiredRole"/> throws for any type not declared here, so a
	/// future schedulable job type cannot be added without also declaring its write floor.
	/// </summary>
	private static readonly Dictionary<string, WaypointRole> MinimumRoleByJobType =
		new(StringComparer.Ordinal)
		{
			[Scan] = WaypointRole.Cyber,
			[Discover] = WaypointRole.Admin,
			[CredentialTest] = WaypointRole.Admin,
			[CatalogIndex] = WaypointRole.Admin,
		};

	public static bool IsValid(string jobType) => All.Contains(jobType);

	/// <summary>
	/// The minimum role required to write (create/update/delete) a schedule of
	/// <paramref name="jobType"/>. Throws <see cref="ArgumentOutOfRangeException"/> for an
	/// undeclared type -- callers must gate on <see cref="IsValid"/> first, and any new
	/// schedulable type must be added to <see cref="MinimumRoleByJobType"/> (fails closed).
	/// </summary>
	public static WaypointRole RequiredRole(string jobType) =>
		MinimumRoleByJobType.TryGetValue(jobType, out WaypointRole role)
			? role
			: throw new ArgumentOutOfRangeException(
				nameof(jobType), jobType, "No schedule write-role floor is declared for this job type.");
}
