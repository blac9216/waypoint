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

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Controllers;
using Waypoint.Core.Authorization;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #30's core deliverable: a reflection-discovered endpoint x role matrix,
/// asserted against a hand-authored expected table. The table is the contract --
/// derived from docs/domain-model.md's Roles (Viewer/Cyber/Operator/Admin), NOT
/// from the code. Every controller action reachable via an <c>[Http*]</c> attribute
/// (the same discovery precedent as <see cref="AllControllersResponseShapeTests"/>)
/// must have an entry here or the test fails closed -- an endpoint added later
/// without an explicit expectation is a build-red event, not a silent gap, because
/// there is no ASP.NET Core <c>FallbackPolicy</c> configured in Program.cs: an action
/// with no <c>[Authorize]</c>/<c>[Require*Role]</c>/<c>[AllowAnonymous]</c> attribute
/// (declared on itself or its controller) is anonymously accessible by default.
///
/// What this test checks per action:
/// <list type="bullet">
/// <item>An explicit guard exists (fail-closed on an undecorated action).</item>
/// <item>The guard matches the expected minimum role (or is anonymous, matched
/// against a table of justifications).</item>
/// </list>
/// Special semantics that a bare attribute cannot express -- run-ownership scoping
/// (<see cref="RunsController.EnforceRunOwnership(System.Security.Claims.ClaimsPrincipal, Waypoint.Infrastructure.Runs.RunQueueState)"/>),
/// per-job-type schedule floors (<see cref="Waypoint.Core.Scheduling.ScheduleJobTypes.RequiredRole"/>),
/// and step-up gating on credential secret overwrite
/// (<see cref="RequireFreshAuthAttribute.Check"/> inside <c>CredentialsController.Update</c>)
/// -- are covered by their own targeted tests elsewhere (<c>RunsEndpointTests</c>,
/// scheduling tests, step-up tests) and are documented, not re-asserted, here; this
/// test's job is the uniform floor every action declares via its attribute.
/// </summary>
public sealed class EndpointRoleMatrixTests
{
	private static readonly string[] HttpVerbAttributeNames =
	{
		"HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute", "HttpPatchAttribute",
	};

	/// <summary>
	/// One row per controller action. Key: "ControllerName.MethodName" (unique in this
	/// codebase -- no controller overloads an action name). Value: the minimum role, or
	/// <c>null</c> for an anonymous action, which must also appear in
	/// <see cref="AnonymousJustifications"/>.
	/// </summary>
	private static readonly Dictionary<string, WaypointRole?> Expected = new(StringComparer.Ordinal)
	{
		// AuthController -- /login and /config are pre-authentication by nature; /me
		// requires any authenticated identity (Viewer floor).
		["AuthController.Config"] = null,
		["AuthController.Login"] = null,
		["AuthController.Me"] = WaypointRole.Viewer,

		// HealthController -- unauthenticated liveness/version probe (nginx, monitoring).
		["HealthController.Get"] = null,

		// AuditController -- Cyber+ per domain-model.md ("Cyber ... + full audit history").
		["AuditController.List"] = WaypointRole.Cyber,

		// CatalogController -- browse is Viewer+; re-index is Admin (matches the
		// accepted ScheduleJobTypes.RequiredRole mapping for catalog-index, issue #517).
		["CatalogController.ListArtifacts"] = WaypointRole.Viewer,
		["CatalogController.Sync"] = WaypointRole.Admin,

		// LibraryController -- issue #36. Both read the depot catalog re-presented as
		// mode-aware presence; same Viewer+ floor as CatalogController.ListArtifacts
		// (the Library tab is browsable by anyone who can see the appliance).
		["LibraryController.ListItems"] = WaypointRole.Viewer,
		["LibraryController.RequestManifest"] = WaypointRole.Viewer,

		// ComplianceContentController -- reads (config, pull history) Viewer+; writes
		// (config PUT, pull trigger) Admin, matching CatalogController's browse/re-index
		// split (issue #40).
		["ComplianceContentController.Get"] = WaypointRole.Viewer,
		["ComplianceContentController.Put"] = WaypointRole.Admin,
		["ComplianceContentController.ListPulls"] = WaypointRole.Viewer,
		["ComplianceContentController.Pull"] = WaypointRole.Admin,

		// ProfilesController -- inventory read is Viewer+ (issue #40, feeds Benchmarks #559).
		// ListControls (issue #598) is the same Viewer+ read-mostly gate as every other
		// profile/config-doc read in this table.
		["ProfilesController.List"] = WaypointRole.Viewer,
		["ProfilesController.ListControls"] = WaypointRole.Viewer,

		// ConfigDocsController -- reads Viewer+; Save (write) Admin (config authoring).
		["ConfigDocsController.List"] = WaypointRole.Viewer,
		["ConfigDocsController.Resolve"] = WaypointRole.Viewer,
		["ConfigDocsController.Get"] = WaypointRole.Viewer,
		["ConfigDocsController.ListVersions"] = WaypointRole.Viewer,
		["ConfigDocsController.GetVersion"] = WaypointRole.Viewer,
		["ConfigDocsController.Save"] = WaypointRole.Admin,

		// CredentialsController -- reads Viewer+; every write (including the
		// shared-credential-only Create per ADR-0011) Admin. Update additionally step-up
		// gates a secret overwrite in-action -- see class doc comment; not re-checked here.
		["CredentialsController.List"] = WaypointRole.Viewer,
		["CredentialsController.Get"] = WaypointRole.Viewer,
		["CredentialsController.Create"] = WaypointRole.Admin,
		["CredentialsController.Update"] = WaypointRole.Admin,
		["CredentialsController.Test"] = WaypointRole.Admin,
		["CredentialsController.Delete"] = WaypointRole.Admin,

		// DashboardController -- read-only aggregate, Viewer+.
		["DashboardController.Get"] = WaypointRole.Viewer,

		// DiscoveryController -- inventory read Viewer+; on-demand discover Admin
		// (matches ScheduleJobTypes.RequiredRole's discover -> Admin mapping).
		["DiscoveryController.GetInventory"] = WaypointRole.Viewer,
		["DiscoveryController.Discover"] = WaypointRole.Admin,

		// DownloadsController -- Operator+ management (domain-model.md "Operator: ...
		// download/catalog/content-library management"). Issue #30 finding: POST and
		// DELETE were Admin-gated as an M1 stopgap predating the Operator role; RBAC
		// has now landed and api-contract.md's `/downloads` row already documents
		// "Operator+" -- fixed in this PR to match both the contract and the frontend's
		// existing Operator+ gate (DownloadCatalogScreen), which had already drifted
		// ahead of the stricter backend guard.
		["DownloadsController.QueueDownloads"] = WaypointRole.Operator,
		["DownloadsController.ListDownloads"] = WaypointRole.Viewer,
		["DownloadsController.CancelDownload"] = WaypointRole.Operator,
		// Issue #560: combined readiness is operational chrome (what's missing before a
		// download can run), not privileged data -- same Viewer+ floor as ListDownloads.
		["DownloadsController.GetReadiness"] = WaypointRole.Viewer,

		// ManagedToolController -- issue #39, ADR-0015 install paths. Same Operator+
		// floor as DownloadsController.QueueDownloads (a write that starts real work);
		// install history read is Viewer+, matching ListDownloads/GetReadiness.
		["ManagedToolController.Install"] = WaypointRole.Operator,
		["ManagedToolController.Upload"] = WaypointRole.Operator,
		["ManagedToolController.Fetch"] = WaypointRole.Operator,
		["ManagedToolController.ListInstalls"] = WaypointRole.Viewer,

		// DepotEnrollmentController -- issue #691. Read is Viewer+ (operational chrome,
		// same floor as GetReadiness above). Every state-changing action (generate,
		// accept-code, validate, reset) is Admin-only: this flow's terminal effect is
		// always a write to the Activation Code credential CredentialsController
		// already gates Admin-only, so the enrollment entry points carry the same
		// floor rather than a looser one that would let an Operator indirectly rotate
		// a credential CredentialsController itself would refuse them.
		["DepotEnrollmentController.Get"] = WaypointRole.Viewer,
		["DepotEnrollmentController.GenerateDepotId"] = WaypointRole.Admin,
		["DepotEnrollmentController.AcceptActivationCode"] = WaypointRole.Admin,
		["DepotEnrollmentController.Validate"] = WaypointRole.Admin,
		["DepotEnrollmentController.Reset"] = WaypointRole.Admin,

		// EventStreamController -- SSE reads, Viewer+.
		["EventStreamController.GlobalStream"] = WaypointRole.Viewer,
		["EventStreamController.RunStream"] = WaypointRole.Viewer,

		// JobsController -- per-job cancel and upload-retry are Operator+ (own runs),
		// Admin any (RunsController.EnforceRunOwnership, checked in-action -- see class
		// doc comment); artifact download is Viewer+.
		["JobsController.CancelJob"] = WaypointRole.Operator,
		["JobsController.GetArtifact"] = WaypointRole.Viewer,
		["JobsController.RetryUpload"] = WaypointRole.Operator,

		// RunsController -- CreateRun is Cyber+ at the attribute floor (remediation's
		// additional Admin + confirmation gate is checked in-action, not attribute-level
		// -- see class doc comment); reads Viewer+; pause/resume/abort/retry are
		// Operator+ (own runs), Admin any (EnforceRunOwnership, in-action);
		// resume-blocked (credential-swap-resume) is Admin.
		["RunsController.CreateRun"] = WaypointRole.Cyber,
		["RunsController.ListRuns"] = WaypointRole.Viewer,
		["RunsController.GetRun"] = WaypointRole.Viewer,
		["RunsController.GetJobs"] = WaypointRole.Viewer,
		["RunsController.RetryJob"] = WaypointRole.Operator,
		["RunsController.GetArtifacts"] = WaypointRole.Viewer,
		["RunsController.GetAttestationsApplied"] = WaypointRole.Viewer,
		// Issue #581 (ADR-0019): bounded historical job-log/event reads are Viewer+,
		// matching every other run read -- visibility of operational history is not a
		// domain action (ADR-0019 decision 6).
		["RunsController.GetEventHistory"] = WaypointRole.Viewer,
		["RunsController.PauseRun"] = WaypointRole.Operator,
		["RunsController.ResumeRun"] = WaypointRole.Operator,
		["RunsController.AbortRun"] = WaypointRole.Operator,
		["RunsController.ResumeBlocked"] = WaypointRole.Admin,
		// Issue #594 (epic #577): purge is Admin-only, stronger than pause/resume/
		// abort's Operator+-own-runs, matching resume-blocked's precedent for an
		// action with wider blast radius than ordinary dispatch control.
		["RunsController.PurgeRun"] = WaypointRole.Admin,
		["RunsController.GetPurgeStatus"] = WaypointRole.Viewer,
		// Issue #592 (epic #588): generic operational-history deletion is Admin-only,
		// same wider-blast-radius gate as purge above.
		["RunsController.DeleteRunHistory"] = WaypointRole.Admin,
		["RunsController.GetRunHistoryDeletionStatus"] = WaypointRole.Viewer,
		// Issue #708/#689: filtered, cursor-paged run history list -- Viewer+, same
		// floor as every other run read (ADR-0019 decision 6).
		["RunsController.ListRunHistory"] = WaypointRole.Viewer,

		// SchedulesController -- reads Viewer+; writes are Cyber+ at the attribute floor
		// (the coarse "lowest schedulable role"), refined per-job_type in-action by
		// ScheduleJobTypes.RequiredRole -- see class doc comment and Schedule.cs.
		["SchedulesController.List"] = WaypointRole.Viewer,
		["SchedulesController.Get"] = WaypointRole.Viewer,
		["SchedulesController.Create"] = WaypointRole.Cyber,
		["SchedulesController.Update"] = WaypointRole.Cyber,
		["SchedulesController.Delete"] = WaypointRole.Cyber,

		// SitesController -- reads Viewer+; every write Admin (domain-model.md: sites are
		// Admin-only config).
		["SitesController.List"] = WaypointRole.Viewer,
		["SitesController.Get"] = WaypointRole.Viewer,
		["SitesController.Create"] = WaypointRole.Admin,
		["SitesController.Update"] = WaypointRole.Admin,
		["SitesController.Delete"] = WaypointRole.Admin,

		// StigManagerController -- global/per-site GET are Viewer+; every PUT/test Admin
		// (connection config + credentials).
		["StigManagerController.GetGlobal"] = WaypointRole.Viewer,
		["StigManagerController.PutGlobal"] = WaypointRole.Admin,
		["StigManagerController.GetForSite"] = WaypointRole.Viewer,
		["StigManagerController.PutForSite"] = WaypointRole.Admin,
		["StigManagerController.Test"] = WaypointRole.Admin,

		// SystemController -- read-only appliance/mode info, Viewer+.
		["SystemController.Get"] = WaypointRole.Viewer,

		// TargetsController -- reads Viewer+; every write Admin (same as Sites).
		// Issue #584: the credential-binding CRUD surface is Admin-only too, same
		// gate as every other target write (SetBinding/ClearBinding write a
		// credential reference onto a target's live connection configuration).
		["TargetsController.ListForSite"] = WaypointRole.Viewer,
		["TargetsController.Get"] = WaypointRole.Viewer,
		["TargetsController.Create"] = WaypointRole.Admin,
		["TargetsController.Update"] = WaypointRole.Admin,
		["TargetsController.Delete"] = WaypointRole.Admin,
		["TargetsController.SetBinding"] = WaypointRole.Admin,
		["TargetsController.ClearBinding"] = WaypointRole.Admin,

		// UsersController -- Admin-only in full (domain-model.md: "users/roles" is an
		// Admin capability; role itself is a read-only IdP mirror -- see class doc
		// comment -- but the whole surface, including reads, is still Admin-gated).
		["UsersController.List"] = WaypointRole.Admin,
		["UsersController.Get"] = WaypointRole.Admin,
		["UsersController.Create"] = WaypointRole.Admin,
		["UsersController.Update"] = WaypointRole.Admin,
	};

	/// <summary>
	/// Every key in <see cref="Expected"/> mapped to <c>null</c> (anonymous) must have a
	/// one-line justification here, so an anonymous endpoint is a reviewed decision, not
	/// an oversight -- the same "narrow, justified allowlist" discipline
	/// <see cref="AllControllersResponseShapeTests.Allowlist"/> uses for the secret-shape
	/// walk.
	/// </summary>
	private static readonly Dictionary<string, string> AnonymousJustifications = new(StringComparer.Ordinal)
	{
		["AuthController.Config"] = "Feature-detection for the SPA's sign-in screen (dev-flag local-auth + OIDC authority/client id) before any sign-in attempt -- issue #534.",
		["AuthController.Login"] = "The credential-bearing entry point itself; dev-flag-gated (LocalAuthOptions.Enabled, off by default) and 404s outright when the flag is off.",
		["HealthController.Get"] = "Unauthenticated liveness/version probe -- what nginx and operator monitoring hit first, before any identity exists.",
	};

	[Fact]
	public void EveryControllerAction_HasAnExplicitRoleGuardMatchingTheDomainModel()
	{
		List<string> missingFromTable = [];
		List<string> mismatches = [];
		List<string> ungated = [];
		List<string> unjustifiedAnonymous = [];

		foreach ((Type controllerType, MethodInfo action) in DiscoverActions())
		{
			string key = $"{controllerType.Name}.{action.Name}";

			WaypointRole? actualMinimum = ResolveMinimumRole(controllerType, action, out bool isAnonymous);

			if (!Expected.TryGetValue(key, out WaypointRole? expectedMinimum))
			{
				missingFromTable.Add(key);
				continue;
			}

			if (expectedMinimum is null)
			{
				if (!isAnonymous)
				{
					mismatches.Add($"{key}: expected AllowAnonymous, found guard requiring {actualMinimum}");
				}
				else if (!AnonymousJustifications.ContainsKey(key))
				{
					unjustifiedAnonymous.Add(key);
				}

				continue;
			}

			if (isAnonymous)
			{
				mismatches.Add($"{key}: expected minimum role {expectedMinimum}, found AllowAnonymous");
				continue;
			}

			if (actualMinimum is null)
			{
				ungated.Add(key);
				continue;
			}

			if (actualMinimum != expectedMinimum)
			{
				mismatches.Add($"{key}: expected minimum role {expectedMinimum}, found {actualMinimum}");
			}
		}

		// Fail closed: an action reachable via [Http*] with no row in Expected must not
		// silently pass -- it must be added to the table (and reviewed) before it ships.
		Assert.True(
			missingFromTable.Count == 0,
			"Controller action(s) with no entry in EndpointRoleMatrixTests.Expected -- every action must have an " +
			"explicit, reviewed role expectation (issue #30, fail-closed on unknown endpoints): " +
			string.Join(", ", missingFromTable));

		Assert.True(
			ungated.Count == 0,
			"Controller action(s) with NO [Require*Role]/[AllowAnonymous] guard at all (anonymously accessible -- " +
			"Program.cs sets no FallbackPolicy): " + string.Join(", ", ungated));

		Assert.True(
			unjustifiedAnonymous.Count == 0,
			"Anonymous action(s) with no entry in AnonymousJustifications: " + string.Join(", ", unjustifiedAnonymous));

		Assert.True(mismatches.Count == 0, "Role guard mismatch(es) vs. the hand-authored table: " + string.Join("; ", mismatches));
	}

	/// <summary>
	/// Proves the matrix actually found real work, not an empty/near-empty set that would
	/// pass every assertion above vacuously.
	/// </summary>
	[Fact]
	public void Sweep_DiscoversASubstantialNumberOfActions()
	{
		int count = DiscoverActions().Count();
		Assert.True(count > 40, $"Expected the sweep to discover many controller actions, only found {count}.");
		Assert.True(Expected.Count >= count, "The Expected table must have at least one row per discovered action.");
	}

	private static IEnumerable<(Type Controller, MethodInfo Action)> DiscoverActions()
	{
		foreach (Type controllerType in typeof(CredentialsController).Assembly.GetTypes()
			.Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t)))
		{
			foreach (MethodInfo method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			{
				bool isAction = method.GetCustomAttributes(inherit: true)
					.Any(a => HttpVerbAttributeNames.Contains(a.GetType().Name));
				if (isAction)
				{
					yield return (controllerType, method);
				}
			}
		}
	}

	/// <summary>
	/// Resolves the effective minimum role for one action: an action-level
	/// <see cref="AuthorizeAttribute"/>-derived guard wins; otherwise falls back to a
	/// controller-level one. <paramref name="isAnonymous"/> is true when an
	/// <see cref="AllowAnonymousAttribute"/> is present at either level (ASP.NET Core's
	/// own precedence: AllowAnonymous at any level suppresses Authorize at any level).
	/// Returns <c>null</c> minimum with <paramref name="isAnonymous"/> false when NO
	/// guard of any kind is present -- the fail-closed "ungated" case.
	/// </summary>
	private static WaypointRole? ResolveMinimumRole(Type controllerType, MethodInfo action, out bool isAnonymous)
	{
		bool actionAnonymous = action.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
		bool controllerAnonymous = controllerType.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
		isAnonymous = actionAnonymous || controllerAnonymous;

		if (isAnonymous)
		{
			return null;
		}

		RequireRoleAttribute? actionGuard = action.GetCustomAttribute<RequireRoleAttribute>();
		if (actionGuard is not null)
		{
			return MinimumRoleFor(actionGuard);
		}

		RequireRoleAttribute? controllerGuard = controllerType.GetCustomAttribute<RequireRoleAttribute>();
		if (controllerGuard is not null)
		{
			return MinimumRoleFor(controllerGuard);
		}

		return null;
	}

	private static WaypointRole MinimumRoleFor(RequireRoleAttribute attribute) => attribute switch
	{
		RequireViewerRoleAttribute => WaypointRole.Viewer,
		RequireCyberRoleAttribute => WaypointRole.Cyber,
		RequireOperatorRoleAttribute => WaypointRole.Operator,
		RequireAdminRoleAttribute => WaypointRole.Admin,
		_ => throw new InvalidOperationException($"Unrecognized RequireRoleAttribute subtype: {attribute.GetType().Name}"),
	};
}
