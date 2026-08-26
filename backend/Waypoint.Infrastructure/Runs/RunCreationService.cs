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

using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Discovery;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Components;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #414: control-plane orchestration for <c>POST /api/v1/runs</c>, extracted out
/// of <see cref="Waypoint.Api.Controllers.RunsController"/> so the controller action is
/// left with only HTTP validation (role/confirmation gates, which stay in the
/// controller because they read <c>ClaimsPrincipal</c>) and response mapping. Stays
/// control-plane per ADR-0013: every operation here goes through
/// <see cref="IJobControlRepository"/>'s enqueue surface and <see cref="IRunSecretStore"/>
/// -- no claim/lease/PowerShell/execution responsibility is absorbed.
///
/// Two run shapes are handled: a <c>scan</c> run resolves its site/target scope and
/// fans out one <c>scan</c> job per target plus any stale-inventory <c>discover</c> jobs
/// (issue #273/#259) before the caller ever sees a response; every other run type keeps
/// the pre-#273 behavior of a bare <see cref="IJobControlRepository.CreateRunAsync"/>
/// call with <c>scope</c> passed through uninterpreted (no job rows created here --
/// their own initiators, e.g. <c>DownloadsController</c>, own their own fan-out).
/// </summary>
public sealed class RunCreationService
{
	private const string ScanRunType = "scan";
	private const string ScanJobType = "scan";
	private const string DiscoverJobType = "discover";

	/// <summary>
	/// Fan-out priority for an auto-triggered <c>discover</c> job (issue #259). Tied
	/// with <see cref="ScanTargetPriority.Nsx"/> (1) -- the highest scan tier -- rather
	/// than set below it, because <c>jobs_priority_check</c> (migration 0001) bounds
	/// <c>priority</c> to 1-6; there is no headroom above the existing top tier. Tying
	/// the top tier is sufficient: a stale target's discover job dispatches at least as
	/// early as any scan job in the same run once the queue is contended (<c>ORDER BY
	/// priority, created_at</c>, <see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository"/>),
	/// and discover specs are appended after every scan spec in
	/// <see cref="BuildStaleDiscoverSpecs"/>'s caller, so a same-priority tie always
	/// resolves in the scan jobs' favor on <c>created_at</c> rather than the reverse --
	/// which is fine, since this is ordering only, not a hard dependency (see
	/// <see cref="BuildStaleDiscoverSpecs"/>'s design-decision note for why the scan
	/// itself does not block on it).
	/// </summary>
	private const short AutoDiscoverPriority = ScanTargetPriority.Nsx;

	private readonly IJobControlRepository _repository;
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly TargetCredentialBindingRepository _bindings;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly IProfileRepository _profiles;
	private readonly IRunSecretStore _runSecrets;
	private readonly IOptions<DiscoveryOptions> _discoveryOptions;
	private readonly IOptions<RunSecretOptions> _runSecretOptions;
	private readonly ScopeResolutionService _scopeResolution;
	private readonly IRunScopeSnapshotRepository _scopeSnapshots;
	private readonly ScanPlannerService _planner;
	private readonly Waypoint.Core.Scans.IScanPlanRepository _plans;
	private readonly IComponentRepository _components;

	public RunCreationService(
		IJobControlRepository repository,
		SiteRepository sites,
		TargetRepository targets,
		TargetCredentialBindingRepository bindings,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		IProfileRepository profiles,
		IRunSecretStore runSecrets,
		IOptions<DiscoveryOptions> discoveryOptions,
		IOptions<RunSecretOptions> runSecretOptions,
		ScopeResolutionService scopeResolution,
		IRunScopeSnapshotRepository scopeSnapshots,
		ScanPlannerService planner,
		Waypoint.Core.Scans.IScanPlanRepository plans,
		IComponentRepository components)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(bindings);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(runSecrets);
		ArgumentNullException.ThrowIfNull(discoveryOptions);
		ArgumentNullException.ThrowIfNull(runSecretOptions);
		ArgumentNullException.ThrowIfNull(scopeResolution);
		ArgumentNullException.ThrowIfNull(scopeSnapshots);
		ArgumentNullException.ThrowIfNull(planner);
		ArgumentNullException.ThrowIfNull(plans);
		ArgumentNullException.ThrowIfNull(components);
		_repository = repository;
		_sites = sites;
		_targets = targets;
		_bindings = bindings;
		_credentials = credentials;
		_profiles = profiles;
		_runSecrets = runSecrets;
		_discoveryOptions = discoveryOptions;
		_runSecretOptions = runSecretOptions;
		_scopeResolution = scopeResolution;
		_scopeSnapshots = scopeSnapshots;
		_planner = planner;
		_plans = plans;
		_components = components;
	}

	/// <summary>
	/// Creates any non-scan run: a bare <see cref="IJobControlRepository.CreateRunAsync"/>
	/// call, <c>scope</c> passed through uninterpreted. The caller (the controller) has
	/// already applied every role/confirmation gate for the run type.
	/// <paramref name="scheduleId"/> is null for every controller call site; only
	/// <see cref="Waypoint.Infrastructure.Scheduling.ScheduleDispatchService"/> passes one
	/// (issue #515).
	/// </summary>
	public async Task<Guid> CreateRunAsync(
		string runType, string scopeJson, Guid? credentialId, string initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null)
	{
		return await _repository.CreateRunAsync(runType, scopeJson, credentialId, initiatedBy, cancellationToken, scheduleId)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Validates a scan run's <c>scope</c> (site + optional target selection, all must
	/// resolve to existing rows -- docs/api-contract.md `/runs`: "POST body: site_id,
	/// scope... credential"), then creates the run and fans out one <c>scan</c>
	/// <see cref="JobSpec"/> per target, ordered by <see cref="ScanTargetPriority"/>.
	/// Every target's job is created up front in one <see cref="IJobControlRepository.FanOutJobsAsync"/>
	/// call -- an individual target's later execution failure cannot affect its
	/// siblings (ADR-0008 Continue policy; each is an independent job row) -- but
	/// validation happens entirely before that call so a bad site/target/credential
	/// reference never leaves a partially created run.
	/// </summary>
	public async Task<Guid> CreateScanRunAsync(
		string scopeJson,
		Guid? credentialId,
		RunSecretCredentialRequest? credential,
		string initiatedBy,
		CancellationToken cancellationToken,
		Guid? scheduleId = null,
		IReadOnlyList<RunCredentialOverride>? credentialOverrides = null,
		IReadOnlyList<RunAdHocCredential>? adHocCredentials = null)
	{
		ScanScope scope;
		try
		{
			scope = ScanScopeParser.Parse(scopeJson);
		}
		catch (FormatException exception)
		{
			throw ApiException.Validation("scope is not valid.", exception.Message);
		}

		if (scope.SiteId is not { } siteId)
		{
			throw ApiException.Validation(
				"scope.site_id is required for a scan run.",
				"Set \"scope\": { \"site_id\": \"<uuid>\" } (optionally with \"target_ids\") in the request body.");
		}

		if (scope.ProfileId is not { } profileId)
		{
			throw ApiException.Validation(
				"scope.profile_id is required for a scan run.",
				"Set \"scope\": { \"profile_id\": \"<uuid>\" } to the id of a profile from GET /profiles (issue #639: a scan must select which pulled compliance-content profile to execute).");
		}

		Site? site = await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
		if (site is null)
		{
			throw ApiException.NotFound("Site not found.", $"Site '{siteId}' does not exist.");
		}

		// The profile must be an installed row in the pulled-content inventory (issue
		// #639 AC "profile must exist in the inventory, or actionable 4xx") -- resolved
		// once here to the on-disk-directory-name profile_key, carried on every fanned-
		// out job's payload instead of a re-lookup per job/target.
		Profile? profile = await _profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
		if (profile is null)
		{
			throw ApiException.NotFound(
				"Profile not found.",
				$"Profile '{profileId}' does not exist; pick one from GET /profiles (pull compliance content first via POST /compliance-content/pull if the list is empty).");
		}

		IReadOnlyList<Target> targets = await ResolveScanTargetsAsync(siteId, scope.TargetIds, cancellationToken).ConfigureAwait(false);
		if (targets.Count == 0)
		{
			throw ApiException.Validation(
				"Site has no targets to scan.",
				$"Site '{siteId}' has no targets; add at least one before starting a scan.");
		}

		// Issue #733 (epic #726 Wave 2, ADR-0023): additive component-scope
		// resolution. Only runs when the caller actually supplied `target_scope` --
		// a request that still uses only the legacy target_ids/profile_id shape is
		// completely unaffected (see ScanScope.TargetScope's doc comment on the
		// transitional coexistence). Resolved strictly BEFORE the run row is
		// created, matching every other pre-creation validation in this method: an
		// unrunnable requested scope must never leave a run/job behind.
		ResolvedTargetScope? resolvedTargetScope = null;
		if (scope.TargetScope is { } targetScopeRequest)
		{
			if (!TargetScopeModes.IsValid(targetScopeRequest.Mode))
			{
				throw ApiException.Validation(
					"scope.target_scope.mode is not valid.",
					$"\"scope.target_scope.mode\" must be one of: {string.Join(", ", new[] { TargetScopeModes.All, TargetScopeModes.Explicit })}.");
			}

			// ADR-0023 "No scan silently falls back from an empty explicit selection
			// to the whole site" (issue #733 AC): distinguished here, before
			// resolution, from an "all" request that happens to resolve to zero
			// components (which is instead ADR-0023's honest empty-coverage case,
			// evaluated below via HasAnyResolvedComponent).
			bool isExplicitEmpty = string.Equals(targetScopeRequest.Mode, TargetScopeModes.Explicit, StringComparison.Ordinal)
				&& (targetScopeRequest.ComponentIds is null || targetScopeRequest.ComponentIds.Count == 0);

			resolvedTargetScope = await _scopeResolution.ResolveAsync(siteId, targetScopeRequest, cancellationToken).ConfigureAwait(false);

			// ADR-0023 "initiation fails only when refresh validates no runnable
			// component": a non-empty request that resolves to zero runnable
			// components is rejected outright; a deliberately empty explicit
			// request is instead allowed through as an honest zero-execution scope
			// (there is nothing to widen from, so there is no conflict to report).
			if (!resolvedTargetScope.HasAnyResolvedComponent && !isExplicitEmpty)
			{
				throw new ApiException(
					System.Net.HttpStatusCode.BadRequest,
					"no_runnable_component",
					"No component in the requested scope could be validated as runnable.",
					"Every requested target/component was unreachable, absent, retired, conflicted, or catalog-incompatible. "
						+ "Refresh inventory and adjust the selection, or resolve the reported conflicts, before starting this scan.",
					scopeOmissions: resolvedTargetScope.Omissions);
			}
		}

		// Issue #734 (epic #726 Wave 2, ADR-0023/0024): compile the resolved component
		// scope into the immutable execution plan BEFORE the run row is created --
		// same pre-creation validation discipline as scope resolution above. A
		// candidate component that individually fails to plan (no active baseline, no
		// mapped benchmark, unsupported) is an explicit ScanPlanSkip and its siblings
		// still plan (ADR-0023/0024's per-component isolation -- see
		// ScanPlannerService's doc comment for the full skip-vs-fail reconciliation);
		// only a plan with ZERO accepted items for a non-empty resolved scope is
		// rejected outright, mirroring HasAnyResolvedComponent's gate immediately
		// above. Component-granular job/attempt rows are NOT created from this plan in
		// this slice (ADR-0024: #735-#737) -- the plan is recorded for provenance/
		// digest/history only; job fan-out below remains the existing target-granular
		// path.
		Waypoint.Core.Scans.ScanPlan? plan = null;
		IReadOnlyDictionary<Guid, PlanTargetRequirement> planRequirementsByTarget = new Dictionary<Guid, PlanTargetRequirement>();
		if (resolvedTargetScope is not null)
		{
			plan = await _planner.CompileAsync(null, resolvedTargetScope.ResolvedComponentIds, cancellationToken).ConfigureAwait(false);
			if (!plan.IsRunnable && resolvedTargetScope.ResolvedComponentIds.Count > 0)
			{
				throw new ApiException(
					System.Net.HttpStatusCode.BadRequest,
					"no_plannable_component",
					"No component in the resolved scope could be compiled into a runnable plan.",
					"Every resolved component was missing an active baseline, an unmapped benchmark, or otherwise failed planning. "
						+ $"{plan.Explanation} Activate a compatible baseline or resolve the reported gaps before starting this scan.");
			}

			// Issue #736 (epic #726 Wave 2, ADR-0024): from here on, a target that has
			// accepted plan items resolves credentials against those items' own
			// catalog-derived RequiredPurposes, not the coarse static
			// CredentialPurposeMatrix -- see PlanCredentialRequirements' doc comment. A
			// target with no plan items (a legacy target_ids/profile_id-only request, or
			// a target whose every candidate component was itself skipped by the
			// planner above) keeps the pre-#736 static-matrix behavior unchanged.
			planRequirementsByTarget = await PlanCredentialRequirements.GroupByTargetAsync(plan, _components, cancellationToken).ConfigureAwait(false);
		}

		bool useRunSecret = credential is not null;
		if (useRunSecret && credentialOverrides is { Count: > 0 })
		{
			// The legacy flat ad hoc ("my credentials") tier is one shared username/secret
			// for the whole run (issue #434) -- wire compat, see the type doc comment.
			// Mixing it with saved-credential overrides has no defined semantics.
			throw ApiException.Validation(
				"credential_overrides cannot be combined with an inline credential.",
				"Saved-credential overrides apply to stored-credential scans only; use ad_hoc_credentials for per-target ad hoc secrets (issue #586).");
		}

		if (useRunSecret && adHocCredentials is { Count: > 0 })
		{
			throw ApiException.Validation(
				"ad_hoc_credentials cannot be combined with an inline credential.",
				"The flat inline \"credential\" and per-target \"ad_hoc_credentials\" are alternative ways to supply ad hoc secrets for the same run; use one or the other.");
		}

		// Issue #585/#586 (epic #582, ADR-0021 §5): resolve every purpose each selected
		// target's scan requires -- target-assigned bindings, the legacy run-level
		// credential_id (reinterpreted as a default-purpose override), validated
		// per-target/per-purpose SAVED overrides, and validated per-target/per-purpose
		// AD HOC overrides -- BEFORE any run/job row exists, so an unresolvable plan is a
		// clean 4xx enumerating every gap, never a partial run.
		//
		// Issue #736: a target that HAS accepted plan items (planRequirementsByTarget)
		// resolves against those items' own catalog-derived purposes instead of the
		// static per-kind matrix, and -- per ADR-0024 ("A missing, incompatible, or
		// ambiguous credential affects only components requiring that purpose... The run
		// is incomplete, not rejected wholesale") -- an unresolved purpose there demotes
		// only the plan item(s) that require it to an explicit ScanPlanSkip rather than
		// failing the whole run; see DemotePlanItemsWithUnresolvedCredentialsAsync.
		CredentialBindingResolution resolution = useRunSecret
			? new CredentialBindingResolution(
				new Dictionary<Guid, IReadOnlyDictionary<string, Guid>>(),
				new Dictionary<Guid, IReadOnlyDictionary<string, RunAdHocCredential>>())
			: await ResolveCredentialBindingsAsync(
				targets, credentialId, credentialOverrides, adHocCredentials, planRequirementsByTarget, cancellationToken).ConfigureAwait(false);
		IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>> resolvedBindings = resolution.SavedByTarget;
		IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, RunAdHocCredential>> adHocByTarget = resolution.AdHocByTarget;

		// Issue #736 (ADR-0024): demote exactly the plan item(s) whose required
		// purpose failed to resolve to an explicit ScanPlanSkip -- per-component
		// isolation, never a whole-run rejection for a per-component credential gap.
		// Only meaningful when a plan exists and at least one target was plan-driven;
		// a no-plan (legacy) request is untouched (resolution.PlanDrivenGaps is empty
		// in that case, since ResolveCredentialBindingsAsync only populates it for
		// plan-driven targets).
		if (plan is not null && resolution.PlanDrivenGaps is { Count: > 0 } planDrivenGaps)
		{
			plan = DemotePlanItemsWithUnresolvedCredentials(plan, planRequirementsByTarget, planDrivenGaps);
		}

		// Ad hoc credentials are stored under (run, target, purpose) BEFORE fan-out: a job
		// claimed the instant it is queued must already be able to find its row. Collected
		// here (not written per-target below) so the write happens once run creation
		// itself succeeds, in the same "no jobs without a committed secret" discipline the
		// legacy flat tier already has.
		List<(Guid TargetId, string Purpose, RunAdHocCredential Credential)> adHocToStore = [.. adHocByTarget
			.SelectMany(pair => pair.Value.Select(inner => (pair.Key, inner.Key, inner.Value)))];

		List<JobSpec> specs = [];
		foreach (Target target in targets)
		{
			// target_kind is the shape-routing signal JobShapes.ForJob reads (issue #309):
			// every scan fans out as job_type = 'scan' regardless of kind, so the payload
			// is the only place the dispatcher can learn "this is an ssh (SRG) target"
			// before a handler ever resolves the target row. profile_key (issue #639) is
			// the content-store-relative directory name ScanJobHandler resolves under
			// ComplianceContentOptions.ContentPath -- carried per-job (not re-derived from
			// scope) so a job replayed after a later content-pull still scans the exact
			// profile the operator picked at run-creation time.
			string payload = JsonSerializer.Serialize(new { target_id = target.Id, site_id = siteId, target_kind = target.Kind, profile_key = profile.ProfileKey });
			if (useRunSecret)
			{
				// No credential_id at all for an ad hoc job -- the secret lives only in
				// run_secrets, keyed by the run id (one legacy row per run, issue #434)
				// rather than one row per job. Falling back to target.CredentialId here
				// would silently mix tiers (a "my credentials" run quietly using a stored
				// service secret). No job_credential_bindings rows either: this is the
				// pre-#586 flat shape, not the per-purpose one.
				specs.Add(new JobSpec(
					ScanJobType,
					ScanTargetPriority.ForTargetKind(target.Kind),
					TargetId: target.Id,
					TargetName: target.Name,
					Payload: payload,
					HasRunSecret: true));
			}
			else
			{
				// Migration 0044's dual-write contract: jobs.credential_id keeps carrying
				// the execution purpose's resolved credential (the kind's default purpose
				// -- the one today's wrappers authenticate with), and the full per-purpose
				// snapshot (including e.g. a vsphere target's vcsa-ssh binding, which has
				// no jobs-column slot) rides CredentialBindings into job_credential_bindings
				// in the same fan-out transaction. An ad hoc purpose (issue #586) never
				// has a jobs.credential_id slot -- if the target's DEFAULT purpose itself
				// resolved ad hoc, effectiveCredentialId stays null (the legacy column has
				// no ad hoc representation; ScanJobHandler's snapshot-preferred resolution
				// reads the job_credential_bindings row, not this column, whenever any
				// snapshot rows exist at all).
				IReadOnlyDictionary<string, Guid> purposes = resolvedBindings.TryGetValue(target.Id, out IReadOnlyDictionary<string, Guid>? foundSaved)
					? foundSaved
					: new Dictionary<string, Guid>();
				IReadOnlyDictionary<string, RunAdHocCredential> adHocPurposes = adHocByTarget.TryGetValue(target.Id, out IReadOnlyDictionary<string, RunAdHocCredential>? foundAdHoc)
					? foundAdHoc
					: new Dictionary<string, RunAdHocCredential>();

				Guid? effectiveCredentialId =
					CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(target.Kind, out string? defaultPurpose)
					&& purposes.TryGetValue(defaultPurpose, out Guid executionCredentialId)
						? executionCredentialId
						: null;

				List<JobCredentialBindingSpec> bindingSpecs = [
					.. purposes.Select(pair => new JobCredentialBindingSpec(pair.Key, pair.Value)),
					.. adHocPurposes.Keys.Select(purpose => new JobCredentialBindingSpec(purpose, CredentialId: null, IsRunSecret: true)),
				];
				bindingSpecs.Sort((a, b) => string.CompareOrdinal(a.Purpose, b.Purpose));

				specs.Add(new JobSpec(
					ScanJobType,
					ScanTargetPriority.ForTargetKind(target.Kind),
					TargetId: target.Id,
					TargetName: target.Name,
					CredentialId: effectiveCredentialId,
					Payload: payload,
					CredentialBindings: bindingSpecs.Count == 0 ? null : bindingSpecs));
			}
		}

		specs.AddRange(await BuildStaleDiscoverSpecsAsync(targets, useRunSecret ? null : resolvedBindings, cancellationToken).ConfigureAwait(false));

		Guid runId = await _repository.CreateRunAsync(ScanRunType, scopeJson, credentialId, initiatedBy, cancellationToken, scheduleId)
			.ConfigureAwait(false);

		// Issue #733: freeze the requested-versus-resolved component scope for this
		// run's history/audit (migration 0056) the instant the run row exists --
		// same "commit before anything that could be claimed" discipline the run
		// secret writes below already follow. Only written when the caller actually
		// supplied target_scope; a legacy target_ids/profile_id-only request leaves
		// no snapshot row (IRunScopeSnapshotRepository.GetForRunAsync documents the
		// null case).
		Guid? runScopeSnapshotId = null;
		if (scope.TargetScope is { } recordedTargetScope && resolvedTargetScope is not null)
		{
			await _scopeSnapshots.RecordAsync(
				runId,
				resolvedTargetScope.Mode,
				JsonSerializer.Serialize(recordedTargetScope),
				resolvedTargetScope.ResolvedComponentIds,
				resolvedTargetScope.Omissions,
				cancellationToken).ConfigureAwait(false);

			// IRunScopeSnapshotRepository.RecordAsync returns void (issue #733's shape) --
			// re-read the row we just wrote to learn its id for scan_plans'
			// run_scope_snapshot_id FK, one extra read on an already-uncommon
			// (target_scope-bearing) request path rather than widening #733's contract.
			runScopeSnapshotId = (await _scopeSnapshots.GetForRunAsync(runId, cancellationToken).ConfigureAwait(false))?.Id;
		}

		// Issue #734: persist the plan compiled above against the now-real run id, in
		// the same "commit before anything that could be claimed" position as the
		// scope snapshot immediately above -- a job claimed the instant it is queued
		// must already be able to find its run's plan. Only written when a plan was
		// actually compiled (i.e. the caller supplied target_scope); a legacy
		// target_ids/profile_id-only request has no plan, matching IScanPlanRepository.GetForRunAsync's
		// documented null case.
		if (plan is not null)
		{
			await _plans.RecordAsync(runId, runScopeSnapshotId, plan, cancellationToken).ConfigureAwait(false);
		}

		if (useRunSecret)
		{
			// Stored BEFORE fan-out: a job claimed the instant it is queued must already
			// be able to find its run's secret row. One row per run (not per job/target)
			// -- every target in this scan shares the same ad hoc credential, matching
			// the pre-#434 in-memory cache's per-run semantics (RunsController supplied
			// the same EphemeralCredential value to every fanned-out job). Fail closed:
			// if the encrypted write or its paired audit row does not commit
			// (IRunSecretStore.StoreAsync's fail-closed contract), this throws and no
			// jobs are ever created for the run -- CreateRunAsync above already committed
			// the (otherwise empty) run row, but a run with zero jobs is inert, not a
			// half-armed credential leak.
			RunSecretCredential runSecretCredential = new(credential!.Username, credential.Secret);
			await _runSecrets.StoreAsync(runId, runSecretCredential, initiatedBy, _runSecretOptions.Value.Expiry, cancellationToken)
				.ConfigureAwait(false);
		}

		// Issue #586: one run_secrets row per (target, purpose) ad hoc override, each
		// under its own RunSecretKey -- fail closed exactly like the legacy tier above; a
		// write that does not commit throws before any job is fanned out, so a run either
		// has every ad hoc secret its jobs will need, or has no jobs at all.
		foreach ((Guid targetId, string purpose, RunAdHocCredential adHoc) in adHocToStore)
		{
			RunSecretCredential runSecretCredential = new(adHoc.Username, adHoc.Secret);
			await _runSecrets.StoreAsync(
				runId, RunSecretKey.For(targetId, purpose), runSecretCredential, initiatedBy, _runSecretOptions.Value.Expiry, cancellationToken)
				.ConfigureAwait(false);
		}

		await _repository.FanOutJobsAsync(runId, specs, initiatedBy, cancellationToken).ConfigureAwait(false);

		return runId;
	}

	/// <summary>
	/// Issue #259 (deferred half of #21's AC): builds one <c>discover</c>
	/// <see cref="JobSpec"/> per <c>vsphere</c> target in <paramref name="targets"/>
	/// whose cached inventory is stale or has never been populated -- the same
	/// staleness test <see cref="Waypoint.Api.Controllers.DiscoveryController.GetInventory"/>
	/// exposes on the wire (<c>LastRefreshed is null</c>, or older than
	/// <see cref="DiscoveryOptions.StaleAfterMinutes"/>), reused here so "stale" means
	/// one thing everywhere it's evaluated. Only <c>vsphere</c> targets are eligible --
	/// <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/> rejects any
	/// other kind outright, and <c>nsx-api</c>/<c>ssh</c> targets have no inventory
	/// cache to refresh in the first place.
	///
	/// <b>Design decision (fire-and-forget, not scan-blocking):</b> queued into the
	/// same run as the scan fan-out, ordered ahead of every scan job via
	/// <see cref="AutoDiscoverPriority"/>, but the scan jobs are NOT made to depend on
	/// or wait for these -- the job queue has no dependency/blocking primitive between
	/// sibling jobs in a run (<see cref="Waypoint.Infrastructure.Jobs.JobQueueRepository.FanOutJobsAsync"/>
	/// only orders dispatch by priority/created_at; ADR-0008's Continue-on-failure
	/// policy treats every job in a run as independent). More fundamentally,
	/// <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/> never reads the
	/// inventory cache (<see cref="Waypoint.Infrastructure.Discovery.InventoryRepository"/>)
	/// at all -- it drives InSpec/PowerCLI directly against the target's own
	/// <c>connection.host</c>, the same way it always has. The cache exists solely to
	/// back the Start-a-Scan checkbox tree (<c>GET /targets/{id}/inventory</c>) that
	/// runs BEFORE this endpoint is called. So blocking this scan on a fresh discover
	/// would add latency and a new failure-coupling path (a discover auth failure
	/// could halt a scan that never touches inventory) for zero benefit to the run
	/// being started; the real benefit is a fresher cache for the NEXT time an
	/// operator opens the checkbox tree or starts another scan. If a future slice
	/// makes scan execution inventory-aware (e.g. per-inventory-item fan-out), this
	/// call site is exactly where a hard dependency would need to be introduced.
	/// </summary>
	private async Task<List<JobSpec>> BuildStaleDiscoverSpecsAsync(
		IReadOnlyList<Target> targets,
		IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>>? resolvedBindings,
		CancellationToken cancellationToken)
	{
		DateTimeOffset staleBefore = DateTimeOffset.UtcNow.AddMinutes(-_discoveryOptions.Value.StaleAfterMinutes);

		List<Target> staleTargets = [];
		foreach (Target target in targets)
		{
			if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal))
			{
				continue;
			}

			bool stale = target.LastRefreshed is null || target.LastRefreshed.Value < staleBefore;
			if (stale)
			{
				staleTargets.Add(target);
			}
		}

		if (staleTargets.Count == 0)
		{
			return [];
		}

		// Issue #585: discovery requires exactly vsphere-api (ADR-0021 §3, the #580
		// defect's fix made law). A stored-credential scan already resolved that purpose
		// for every vsphere target (it is scan-required too), so the piggybacked discover
		// job reuses the scan's resolution -- which also honors a vsphere-api override
		// naturally. A run-secret scan resolves no stored bindings for its scan jobs
		// (resolvedBindings null), so the discover jobs -- which always run on the
		// stored/service tier, never on the run's ad hoc secret -- read the target's own
		// vsphere-api binding directly; a target with none fans out with a NULL
		// credential exactly as the legacy target.CredentialId path did (fire-and-forget:
		// the discover job fails auth-style without affecting the scan, see the
		// design-decision note above).
		IReadOnlyDictionary<Guid, IReadOnlyList<TargetCredentialBinding>>? bindingsByTarget = resolvedBindings is null
			? await _bindings.ListForTargetsAsync([.. staleTargets.Select(t => t.Id)], cancellationToken).ConfigureAwait(false)
			: null;

		List<JobSpec> specs = [];
		foreach (Target target in staleTargets)
		{
			Guid? discoverCredentialId = null;
			if (resolvedBindings is not null)
			{
				if (resolvedBindings.TryGetValue(target.Id, out IReadOnlyDictionary<string, Guid>? purposes)
					&& purposes.TryGetValue(CredentialPurposes.VSphereApi, out Guid resolvedId))
				{
					discoverCredentialId = resolvedId;
				}
			}
			else if (bindingsByTarget!.TryGetValue(target.Id, out IReadOnlyList<TargetCredentialBinding>? targetBindings))
			{
				discoverCredentialId = targetBindings
					.FirstOrDefault(b => string.Equals(b.Purpose, CredentialPurposes.VSphereApi, StringComparison.Ordinal))
					?.CredentialId;
			}

			string payload = JsonSerializer.Serialize(new { target_id = target.Id });
			specs.Add(new JobSpec(
				DiscoverJobType,
				AutoDiscoverPriority,
				TargetId: target.Id,
				TargetName: target.Name,
				CredentialId: discoverCredentialId,
				Payload: payload,
				CredentialBindings: discoverCredentialId is { } boundId
					? [new JobCredentialBindingSpec(CredentialPurposes.VSphereApi, boundId)]
					: null));
		}

		return specs;
	}

	/// <summary>
	/// Issue #585/#586 (epic #582, ADR-0021 §§4-6): resolves, per selected target, every
	/// credential purpose its scan requires (plus any component-conditional purpose that
	/// happens to be bound/overridden) into either a concrete SAVED credential id or an
	/// AD HOC (run_secrets-backed) source. Precedence per (target, purpose): ad hoc
	/// override (issue #586, the most explicit action an operator can take for a single
	/// purpose) &gt; explicit SAVED override &gt; the legacy run-level
	/// <paramref name="runCredentialId"/> (default/execution purpose only -- its
	/// pre-#585 semantics, "use this credential for all targets," preserved but now
	/// type-checked) &gt; the target's own <c>target_credential_bindings</c> row. Every
	/// problem is collected -- never first-failure-only -- and thrown as one
	/// <c>credential_binding_gaps</c> 400 enumerating each (target, purpose, reason)
	/// triple, the resolution counterpart of #593/#655's blocker-category breakdown.
	/// Binding-sourced credentials are not re-validated for type here: the 0043 CHECK/
	/// UPSERT surface and its backfill only ever admit compatible rows. Ad hoc entries
	/// carry no credential TYPE to check -- ADR-0021 §2's compatibility matrix applies to
	/// stored <c>credentials</c> rows, which an ad hoc secret never becomes (ADR-0011
	/// "no personal rows, ever"); the caller (RunsController) already rejected a
	/// (target, purpose) pair supplied by both <paramref name="adHocCredentials"/> and
	/// <paramref name="overrides"/>, but this method re-checks it defensively (a service
	/// caller other than the controller must not be able to silently pick a winner).
	/// </summary>
	private async Task<CredentialBindingResolution> ResolveCredentialBindingsAsync(
		IReadOnlyList<Target> targets,
		Guid? runCredentialId,
		IReadOnlyList<RunCredentialOverride>? overrides,
		IReadOnlyList<RunAdHocCredential>? adHocCredentials,
		IReadOnlyDictionary<Guid, PlanTargetRequirement> planRequirementsByTarget,
		CancellationToken cancellationToken)
	{
		Dictionary<Guid, Target> targetsById = targets.ToDictionary(t => t.Id);
		IReadOnlyDictionary<Guid, IReadOnlyList<TargetCredentialBinding>> bindingsByTarget =
			await _bindings.ListForTargetsAsync([.. targetsById.Keys], cancellationToken).ConfigureAwait(false);

		// One lookup per distinct referenced credential (overrides + the legacy
		// run-level id) -- binding rows are FK-guaranteed to exist and were
		// type-validated at write time, so only request-supplied ids need fetching.
		Dictionary<Guid, CredentialResponse> referencedCredentials = [];
		IEnumerable<Guid> referencedIds = (overrides ?? []).Select(o => o.CredentialId);
		if (runCredentialId is { } runCredential)
		{
			referencedIds = referencedIds.Append(runCredential);
		}

		foreach (Guid referencedId in referencedIds.Distinct())
		{
			CredentialResponse? found = await _credentials.GetAsync(referencedId, cancellationToken).ConfigureAwait(false);
			if (found is not null)
			{
				referencedCredentials[referencedId] = found;
			}
		}

		List<CredentialBindingGap> gaps = [];

		// The legacy run-level credential must at least exist -- pre-#585 this was
		// only caught by the runs.credential_id FK as an unmapped error; now it is the
		// same clean 404 an unknown site/profile gets.
		if (runCredentialId is { } legacyCredentialId && !referencedCredentials.ContainsKey(legacyCredentialId))
		{
			throw ApiException.NotFound(
				"Credential not found.",
				$"Credential '{legacyCredentialId}' does not exist; pick one from GET /credentials.");
		}

		// Issue #586: ad hoc overrides are validated first (highest precedence) --
		// target-in-scope and purpose-applicable exactly mirror the saved-override checks
		// below, but there is no credential to look up/type-check (ADR-0011: an ad hoc
		// secret is never a `credentials` row). A pair also named by a saved override is
		// a gap either way (DuplicateOverride) -- the two tiers can never silently pick a
		// winner for the same (target, purpose).
		Dictionary<(Guid TargetId, string Purpose), RunAdHocCredential> acceptedAdHoc = [];
		HashSet<(Guid TargetId, string Purpose)> overridePairs = overrides is { Count: > 0 }
			? [.. overrides.Select(o => (o.TargetId, o.Purpose))]
			: [];
		foreach (RunAdHocCredential adHoc in adHocCredentials ?? [])
		{
			if (!targetsById.TryGetValue(adHoc.TargetId, out Target? adHocTarget))
			{
				gaps.Add(new CredentialBindingGap(
					adHoc.TargetId, null, adHoc.Purpose, CredentialBindingGapReasons.TargetNotInScope));
				continue;
			}

			if (!CredentialPurposeMatrix.ApplicablePurposes(adHocTarget.Kind).Contains(adHoc.Purpose, StringComparer.Ordinal))
			{
				gaps.Add(new CredentialBindingGap(
					adHocTarget.Id, adHocTarget.Name, adHoc.Purpose, CredentialBindingGapReasons.PurposeNotApplicable));
				continue;
			}

			if (acceptedAdHoc.ContainsKey((adHoc.TargetId, adHoc.Purpose)) || overridePairs.Contains((adHoc.TargetId, adHoc.Purpose)))
			{
				gaps.Add(new CredentialBindingGap(
					adHocTarget.Id, adHocTarget.Name, adHoc.Purpose, CredentialBindingGapReasons.DuplicateOverride));
				continue;
			}

			acceptedAdHoc[(adHoc.TargetId, adHoc.Purpose)] = adHoc;
		}

		Dictionary<(Guid TargetId, string Purpose), Guid> acceptedOverrides = [];
		foreach (RunCredentialOverride @override in overrides ?? [])
		{
			if (!targetsById.TryGetValue(@override.TargetId, out Target? overrideTarget))
			{
				gaps.Add(new CredentialBindingGap(
					@override.TargetId, null, @override.Purpose, CredentialBindingGapReasons.TargetNotInScope, @override.CredentialId));
				continue;
			}

			if (!CredentialPurposeMatrix.ApplicablePurposes(overrideTarget.Kind).Contains(@override.Purpose, StringComparer.Ordinal))
			{
				gaps.Add(new CredentialBindingGap(
					overrideTarget.Id, overrideTarget.Name, @override.Purpose, CredentialBindingGapReasons.PurposeNotApplicable, @override.CredentialId));
				continue;
			}

			if (acceptedOverrides.ContainsKey((@override.TargetId, @override.Purpose)))
			{
				gaps.Add(new CredentialBindingGap(
					overrideTarget.Id, overrideTarget.Name, @override.Purpose, CredentialBindingGapReasons.DuplicateOverride, @override.CredentialId));
				continue;
			}

			if (!referencedCredentials.TryGetValue(@override.CredentialId, out CredentialResponse? overrideCredential))
			{
				gaps.Add(new CredentialBindingGap(
					overrideTarget.Id, overrideTarget.Name, @override.Purpose, CredentialBindingGapReasons.CredentialNotFound, @override.CredentialId));
				continue;
			}

			if (!CredentialPurposeMatrix.IsCompatible(@override.Purpose, overrideCredential.CredentialType))
			{
				gaps.Add(new CredentialBindingGap(
					overrideTarget.Id, overrideTarget.Name, @override.Purpose, CredentialBindingGapReasons.IncompatibleCredentialType, @override.CredentialId));
				continue;
			}

			acceptedOverrides[(@override.TargetId, @override.Purpose)] = @override.CredentialId;
		}

		Dictionary<Guid, IReadOnlyDictionary<string, Guid>> resolved = [];
		Dictionary<Guid, IReadOnlyDictionary<string, RunAdHocCredential>> resolvedAdHoc = [];

		// Issue #736 (ADR-0024): a target with accepted plan items resolves ONLY the
		// purposes those items' catalog execution profiles actually declare
		// (PlanTargetRequirement.RequiredPurposes -- e.g. vcsa-ssh appears here only
		// when a selected VCSA component's execution profile requires it, never merely
		// because the target's KIND is vsphere). A gap on one of these purposes is
		// collected into planGaps (never thrown) -- the caller demotes exactly the
		// plan item(s) needing that purpose to a ScanPlanSkip, per ADR-0024's
		// per-component isolation, instead of failing the whole run. A target with no
		// plan items (legacy target_ids/profile_id-only request, or every candidate
		// component already skipped by the planner) keeps the pre-#736 static-matrix
		// gap-collection-then-throw behavior below, completely unchanged.
		List<CredentialBindingGap> planGaps = [];
		foreach (Target target in targets)
		{
			Dictionary<string, Guid> purposes = new(StringComparer.Ordinal);
			Dictionary<string, RunAdHocCredential> adHocPurposes = new(StringComparer.Ordinal);
			IReadOnlyList<TargetCredentialBinding> targetBindings =
				bindingsByTarget.TryGetValue(target.Id, out IReadOnlyList<TargetCredentialBinding>? found) ? found : [];
			CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(target.Kind, out string? defaultPurpose);

			bool isPlanDriven = planRequirementsByTarget.TryGetValue(target.Id, out PlanTargetRequirement? planRequirement);
			List<CredentialBindingGap> gapSink = isPlanDriven ? planGaps : gaps;
			IEnumerable<string> requiredPurposes = isPlanDriven ? planRequirement!.RequiredPurposes : CredentialPurposeMatrix.RequiredScanPurposes(target.Kind);

			foreach (string purpose in requiredPurposes)
			{
				// Issue #586: ad hoc has the highest precedence -- checked before the saved
				// override, the run-level default, and the target's own binding.
				if (acceptedAdHoc.TryGetValue((target.Id, purpose), out RunAdHocCredential? adHocCredential))
				{
					adHocPurposes[purpose] = adHocCredential;
					continue;
				}

				if (acceptedOverrides.TryGetValue((target.Id, purpose), out Guid overrideCredentialId))
				{
					purposes[purpose] = overrideCredentialId;
					continue;
				}

				if (runCredentialId is { } runLevelCredentialId && string.Equals(purpose, defaultPurpose, StringComparison.Ordinal))
				{
					// Pre-#585 the run-level credential was copied to every job with no
					// type check at all -- a vcenter credential silently "scanned" ssh
					// targets and failed at execution. Rejecting the incompatible pair up
					// front is the epic's own AC ("atomically rejects all
					// missing/incompatible bindings before dispatch").
					if (CredentialPurposeMatrix.IsCompatible(purpose, referencedCredentials[runLevelCredentialId].CredentialType))
					{
						purposes[purpose] = runLevelCredentialId;
					}
					else
					{
						gapSink.Add(new CredentialBindingGap(
							target.Id, target.Name, purpose, CredentialBindingGapReasons.IncompatibleCredentialType, runLevelCredentialId));
					}

					continue;
				}

				TargetCredentialBinding? binding = targetBindings
					.FirstOrDefault(b => string.Equals(b.Purpose, purpose, StringComparison.Ordinal));
				if (binding is null)
				{
					gapSink.Add(new CredentialBindingGap(target.Id, target.Name, purpose, CredentialBindingGapReasons.MissingBinding));
				}
				else
				{
					purposes[purpose] = binding.CredentialId;
				}
			}

			// The static conditional-purpose (opportunistic) pass only applies to a
			// non-plan-driven target -- a plan-driven target's RequiredPurposes above
			// already IS the exact, catalog-derived set (required and conditional are
			// no longer distinguished per-target once the catalog resolves them
			// per-component; see ScanPlannerService).
			if (!isPlanDriven)
			{
				foreach (string purpose in CredentialPurposeMatrix.ConditionalScanPurposes(target.Kind))
				{
					if (acceptedAdHoc.TryGetValue((target.Id, purpose), out RunAdHocCredential? adHocCredential))
					{
						adHocPurposes[purpose] = adHocCredential;
						continue;
					}

					if (acceptedOverrides.TryGetValue((target.Id, purpose), out Guid overrideCredentialId))
					{
						purposes[purpose] = overrideCredentialId;
						continue;
					}

					TargetCredentialBinding? binding = targetBindings
						.FirstOrDefault(b => string.Equals(b.Purpose, purpose, StringComparison.Ordinal));
					if (binding is not null)
					{
						purposes[purpose] = binding.CredentialId;
					}
				}
			}

			resolved[target.Id] = purposes;
			resolvedAdHoc[target.Id] = adHocPurposes;
		}

		if (gaps.Count > 0)
		{
			throw new ApiException(
				System.Net.HttpStatusCode.BadRequest,
				"credential_binding_gaps",
				"One or more selected targets cannot resolve every required credential purpose.",
				"Each gap names the target, the credential purpose, and why it could not resolve. "
					+ "Assign the missing/compatible bindings on the targets (PUT /targets/{id}/credential-bindings/{purpose}) or supply valid credential_overrides, "
					+ "or supply a valid ad_hoc_credentials entry (issue #586).",
				bindingGaps: gaps);
		}

		return new CredentialBindingResolution(resolved, resolvedAdHoc, planGaps);
	}

	/// <summary>
	/// Issue #736 (ADR-0024 "A missing, incompatible, or ambiguous credential affects
	/// only components requiring that purpose... The run is incomplete, not rejected
	/// wholesale"): removes from <paramref name="plan"/> every accepted item that
	/// requires a purpose named in <paramref name="planDrivenGaps"/> for that item's
	/// owning target, and records each removal as an explicit
	/// <see cref="ScanPlanSkipReasons"/>-style skip (using the same
	/// <c>CredentialBindingGapReasons</c> value as the skip reason, so run history shows
	/// exactly why the component never ran) -- its siblings, including other items on
	/// the SAME target that need only purposes which DID resolve, are unaffected. The
	/// plan's digest/explanation are recomputed so persisted history reflects the
	/// post-demotion accepted/skip sets, matching <see cref="ScanPlannerService"/>'s own
	/// determinism contract.
	/// </summary>
	private static Waypoint.Core.Scans.ScanPlan DemotePlanItemsWithUnresolvedCredentials(
		Waypoint.Core.Scans.ScanPlan plan,
		IReadOnlyDictionary<Guid, PlanTargetRequirement> planRequirementsByTarget,
		IReadOnlyList<CredentialBindingGap> planDrivenGaps)
	{
		// (targetId, purpose) -> true for every gap -- a plan item on that target
		// requiring that purpose is demoted.
		HashSet<(Guid TargetId, string Purpose)> unresolvedPairs = [.. planDrivenGaps.Select(g => (g.TargetId, g.Purpose))];

		Dictionary<Guid, Guid> targetByComponent = [];
		foreach ((Guid targetId, PlanTargetRequirement requirement) in planRequirementsByTarget)
		{
			foreach (Waypoint.Core.Scans.ScanPlanItem item in requirement.Items)
			{
				targetByComponent[item.ComponentId] = targetId;
			}
		}

		List<Waypoint.Core.Scans.ScanPlanItem> survivingItems = [];
		List<Waypoint.Core.Scans.ScanPlanSkip> newSkips = [];
		foreach (Waypoint.Core.Scans.ScanPlanItem item in plan.Items)
		{
			if (!targetByComponent.TryGetValue(item.ComponentId, out Guid ownerTargetId))
			{
				// Not a plan-driven item at all (should not happen -- every plan item
				// came from GroupByTargetAsync's own enumeration of plan.Items -- but
				// keep it rather than drop it silently if the invariant ever breaks).
				survivingItems.Add(item);
				continue;
			}

			string? unresolvedPurpose = item.RequiredPurposes
				.FirstOrDefault(purpose => unresolvedPairs.Contains((ownerTargetId, purpose)));
			if (unresolvedPurpose is null)
			{
				survivingItems.Add(item);
				continue;
			}

			CredentialBindingGap gap = planDrivenGaps.First(g => g.TargetId == ownerTargetId && g.Purpose == unresolvedPurpose);
			newSkips.Add(new Waypoint.Core.Scans.ScanPlanSkip(
				item.ComponentId,
				gap.Reason,
				$"Component '{item.ComponentId}' requires credential purpose '{unresolvedPurpose}', which could not resolve ({gap.Reason})."));
		}

		if (newSkips.Count == 0)
		{
			return plan;
		}

		List<Waypoint.Core.Scans.ScanPlanSkip> allSkips = [.. plan.Skips, .. newSkips];
		Guid[] scopeSeed = [.. plan.Items.Select(i => i.ComponentId).Concat(plan.Skips.Select(s => s.ComponentId)).Distinct()];
		string digest = Waypoint.Core.Scans.ScanPlanDigest.Compute(plan.PlanSchemaVersion, scopeSeed, survivingItems);
		string explanation = $"{survivingItems.Count} of {plan.Items.Count + plan.Skips.Count} requested component(s) accepted into the plan "
			+ $"after credential resolution; {allSkips.Count} skipped (including {newSkips.Count} for an unresolved required credential).";

		return plan with { Items = survivingItems, Skips = allSkips, PlanDigest = digest, Explanation = explanation };
	}

	/// <summary>
	/// Resolves the scan's target set: every target under the site when
	/// <paramref name="requestedIds"/> is null/empty (a full-site scan), or exactly the
	/// requested ids -- each of which must belong to <paramref name="siteId"/>, so a
	/// target id from a different site is a clean 404 rather than silently scanning
	/// the wrong site's target.
	/// </summary>
	private async Task<IReadOnlyList<Target>> ResolveScanTargetsAsync(
		Guid siteId, IReadOnlyList<Guid>? requestedIds, CancellationToken cancellationToken)
	{
		if (requestedIds is null || requestedIds.Count == 0)
		{
			// Issue #279: a full-site scan must fan out over every target under the
			// site, not a PageRequest-clamped page of at most 200 -- ListAllForSiteAsync
			// is the dedicated unpaginated repository method for exactly this caller.
			return await _targets.ListAllForSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
		}

		List<Target> resolved = [];
		foreach (Guid targetId in requestedIds)
		{
			Target? target = await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
			if (target is null || target.SiteId != siteId)
			{
				throw ApiException.NotFound(
					"Target not found.",
					$"Target '{targetId}' does not exist under site '{siteId}'.");
			}

			resolved.Add(target);
		}

		return resolved;
	}
}

/// <summary>
/// Inline ("my credentials", ADR-0011) credential carried on a scan-run create request,
/// decoupled from the wire contract type (<c>Waypoint.Api.Contracts.EphemeralCredentialRequest</c>)
/// so this service does not depend on <c>Waypoint.Api</c>.
/// </summary>
public sealed record RunSecretCredentialRequest(string Username, string Secret);

/// <summary>
/// One structured per-target/per-purpose saved-credential override on a scan-run
/// create request (issue #585, ADR-0021 §4), decoupled from the wire contract type
/// (<c>Waypoint.Api.Contracts.RunCredentialOverrideRequest</c>) so this service does
/// not depend on <c>Waypoint.Api</c>. Applies to exactly the named (target, purpose)
/// pair -- never any other target, never any other purpose of the same target.
/// </summary>
public sealed record RunCredentialOverride(Guid TargetId, string Purpose, Guid CredentialId);

/// <summary>
/// One structured per-target/per-purpose AD HOC ("my credentials", ADR-0011) override on
/// a scan-run create request (issue #586, epic #582, ADR-0021 §4) -- the ad hoc
/// counterpart of <see cref="RunCredentialOverride"/>, decoupled from the wire contract
/// type (<c>Waypoint.Api.Contracts.RunAdHocCredentialRequest</c>) so this service does not
/// depend on <c>Waypoint.Api</c>. Applies to exactly the named (target, purpose) pair;
/// <see cref="Username"/>/<see cref="Secret"/> are stored ONLY as an envelope-encrypted
/// <c>run_secrets</c> row keyed by <c>RunSecretKey.For(TargetId, Purpose)</c> under the
/// created run -- never a <c>credentials</c>/<c>credential_secrets</c> row.
/// </summary>
public sealed record RunAdHocCredential(Guid TargetId, string Purpose, string Username, string Secret);

/// <summary>
/// The result of <see cref="RunCreationService.ResolveCredentialBindingsAsync"/>: per
/// target, the purposes resolved to a SAVED credential id (<see cref="SavedByTarget"/>,
/// pre-#586 shape) and the purposes resolved to an AD HOC inline credential
/// (<see cref="AdHocByTarget"/>, issue #586) -- disjoint per (target, purpose): a purpose
/// appears in exactly one of the two dictionaries for a given target, never both (ad hoc
/// takes precedence at resolution time, so a purpose that resolved ad hoc is never also
/// present in <see cref="SavedByTarget"/>). <see cref="PlanDrivenGaps"/> (issue #736,
/// ADR-0024) carries the (target, purpose) resolution failures for plan-driven targets
/// ONLY -- these never throw from <see cref="RunCreationService.ResolveCredentialBindingsAsync"/>
/// the way a legacy (non-plan) gap does; the caller demotes exactly the plan item(s)
/// requiring the unresolved purpose to an explicit skip instead (per-component isolation).
/// </summary>
public sealed record CredentialBindingResolution(
	IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>> SavedByTarget,
	IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, RunAdHocCredential>> AdHocByTarget,
	IReadOnlyList<CredentialBindingGap>? PlanDrivenGaps = null);
