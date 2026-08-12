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
using Waypoint.Core.Discovery;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
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
	private readonly IRunSecretStore _runSecrets;
	private readonly IOptions<DiscoveryOptions> _discoveryOptions;
	private readonly IOptions<RunSecretOptions> _runSecretOptions;

	public RunCreationService(
		IJobControlRepository repository,
		SiteRepository sites,
		TargetRepository targets,
		IRunSecretStore runSecrets,
		IOptions<DiscoveryOptions> discoveryOptions,
		IOptions<RunSecretOptions> runSecretOptions)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(runSecrets);
		ArgumentNullException.ThrowIfNull(discoveryOptions);
		ArgumentNullException.ThrowIfNull(runSecretOptions);
		_repository = repository;
		_sites = sites;
		_targets = targets;
		_runSecrets = runSecrets;
		_discoveryOptions = discoveryOptions;
		_runSecretOptions = runSecretOptions;
	}

	/// <summary>
	/// Creates any non-scan run: a bare <see cref="IJobControlRepository.CreateRunAsync"/>
	/// call, <c>scope</c> passed through uninterpreted. The caller (the controller) has
	/// already applied every role/confirmation gate for the run type.
	/// </summary>
	public async Task<Guid> CreateRunAsync(
		string runType, string scopeJson, Guid? credentialId, string initiatedBy, CancellationToken cancellationToken)
	{
		return await _repository.CreateRunAsync(runType, scopeJson, credentialId, initiatedBy, cancellationToken)
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
		CancellationToken cancellationToken)
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

		Site? site = await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
		if (site is null)
		{
			throw ApiException.NotFound("Site not found.", $"Site '{siteId}' does not exist.");
		}

		IReadOnlyList<Target> targets = await ResolveScanTargetsAsync(siteId, scope.TargetIds, cancellationToken).ConfigureAwait(false);
		if (targets.Count == 0)
		{
			throw ApiException.Validation(
				"Site has no targets to scan.",
				$"Site '{siteId}' has no targets; add at least one before starting a scan.");
		}

		bool useRunSecret = credential is not null;

		List<JobSpec> specs = [];
		foreach (Target target in targets)
		{
			// target_kind is the shape-routing signal JobShapes.ForJob reads (issue #309):
			// every scan fans out as job_type = 'scan' regardless of kind, so the payload
			// is the only place the dispatcher can learn "this is an ssh (SRG) target"
			// before a handler ever resolves the target row.
			string payload = JsonSerializer.Serialize(new { target_id = target.Id, site_id = siteId, target_kind = target.Kind });
			if (useRunSecret)
			{
				// No credential_id at all for an ad hoc job -- the secret lives only in
				// run_secrets, keyed by the run id (one row per run, issue #434) rather
				// than one row per job. Falling back to target.CredentialId here would
				// silently mix tiers (a "my credentials" run quietly using a stored
				// service secret).
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
				Guid? effectiveCredentialId = credentialId ?? target.CredentialId;
				specs.Add(new JobSpec(
					ScanJobType,
					ScanTargetPriority.ForTargetKind(target.Kind),
					TargetId: target.Id,
					TargetName: target.Name,
					CredentialId: effectiveCredentialId,
					Payload: payload));
			}
		}

		specs.AddRange(BuildStaleDiscoverSpecs(targets));

		Guid runId = await _repository.CreateRunAsync(ScanRunType, scopeJson, credentialId, initiatedBy, cancellationToken)
			.ConfigureAwait(false);

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
	private List<JobSpec> BuildStaleDiscoverSpecs(IReadOnlyList<Target> targets)
	{
		DateTimeOffset staleBefore = DateTimeOffset.UtcNow.AddMinutes(-_discoveryOptions.Value.StaleAfterMinutes);

		List<JobSpec> specs = [];
		foreach (Target target in targets)
		{
			if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal))
			{
				continue;
			}

			bool stale = target.LastRefreshed is null || target.LastRefreshed.Value < staleBefore;
			if (!stale)
			{
				continue;
			}

			string payload = JsonSerializer.Serialize(new { target_id = target.Id });
			specs.Add(new JobSpec(
				DiscoverJobType,
				AutoDiscoverPriority,
				TargetId: target.Id,
				TargetName: target.Name,
				CredentialId: target.CredentialId,
				Payload: payload));
		}

		return specs;
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
