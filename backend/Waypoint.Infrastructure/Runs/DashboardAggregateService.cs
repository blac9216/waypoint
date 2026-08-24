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

using Microsoft.Extensions.Options;
using Waypoint.Core.Discovery;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>Everything <c>DashboardController</c> needs beyond what <c>/system</c> and
/// <c>/schedules</c> already answer -- see <c>DashboardResponse</c>'s doc comment for
/// why those two are excluded from this aggregate.</summary>
public sealed record DashboardAggregate(
	int TargetTotal,
	IReadOnlyDictionary<string, int> TargetsByKind,
	bool FindingsCountsAvailable,
	int FindingsCatIOpen,
	int FindingsCatIIOpen,
	int FindingsCatIIIOpen,
	IReadOnlyList<DashboardSiteRow> Sites,
	IReadOnlyList<RunSummary> RecentRuns,
	IReadOnlyList<DashboardAttentionRow> Attention);

public sealed record DashboardSiteRow(
	Guid Id, string Name, int TargetCount, double? CompliancePercent,
	int? CatIOpen, int? CatIIOpen, int? CatIIIOpen, DateTimeOffset? LastScanAt);

public sealed record DashboardAttentionRow(string Kind, string Text, string Meta);

/// <summary>
/// Issue #513: aggregates fleet KPIs, per-site posture, recent runs, and the in-scope
/// ATTENTION signals for <c>GET /dashboard</c>. CAT I/II/III counts are HDF-derived
/// (filesystem, not a DB column -- see <see cref="RunArtifactProjectionService"/>), so
/// this walks each site's SINGLE most recent completed <c>scan</c> run rather than the
/// full run history, bounding the filesystem work to (recent runs scanned) x (jobs in
/// that run) regardless of fleet size or run count.
///
/// ATTENTION signals, scoped by the issue's 2026-08-19 feasibility audit:
///   - credential_auth_failing: <c>credentials.health = 'auth_failing'</c> (queryable
///     today via <see cref="CredentialRepository"/>).
///   - inventory_stale: <c>targets.last_refreshed</c> age against
///     <see cref="DiscoveryOptions.StaleAfterMinutes"/> -- the same staleness rule
///     <c>DiscoveryController.GetInventory</c> already applies per-target, evaluated
///     here across the whole fleet.
///   - depot_token_expiring: DORMANT. No depot/download-tool credential in this
///     codebase carries an expiry today (grepped <c>Waypoint.Core.Secrets</c> and
///     <c>Waypoint.Core.Catalog</c> — neither <c>CredentialResponse</c> nor
///     <c>DepotArtifact</c> has an expiry field), so this signal type never appears in
///     <see cref="DashboardAggregate.Attention"/> until that metadata exists; wiring it
///     is a follow-up once a token-expiry-bearing credential type lands, not invented
///     here.
///   - Unattested CAT I fleet aggregate is explicitly OUT (tracked in #514).
/// </summary>
public sealed class DashboardAggregateService
{
	private const string ScanJobType = "scan";
	private const int RecentRunsScanned = 20;
	private const int RecentRunsReturned = 8;

	/// <summary>
	/// The RECENT RUNS card is a compliance summary (docs/ui/prototype/README.md screen
	/// 2) and every row deep-links into Results, which itself shows only these
	/// compliance-owned <c>run_type</c> values (frontend <c>useRunList.ts</c>'s
	/// <c>COMPLIANCE_RUN_TYPES</c>). Issue #717: filter to these BEFORE the cap so the
	/// returned list is always the N most recent COMPLIANCE runs -- capping an
	/// unfiltered fetch let a burst of operational runs (discover/credential-test/
	/// content-pull/tool-install/etc.) push every scan/remediate row past the cap,
	/// permanently under-displaying compliance activity that still exists.
	/// <see cref="RunTypes.Scan"/>/<see cref="RunTypes.Remediate"/> is the authoritative
	/// closed set (byte-identical to the CHECK constraint via
	/// <c>RunTypesConstraintDriftTests</c>); the frontend twin is bound to it by
	/// <c>runTypes.test.ts</c>/<c>dashboard.runTypes.test.ts</c>.
	/// </summary>
	private static readonly IReadOnlyList<string> ComplianceRunTypes =
		[RunTypes.Scan, RunTypes.Remediate];

	private readonly IJobControlRepository _jobs;
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly CredentialRepository _credentials;
	private readonly RunArtifactProjectionService _artifacts;
	private readonly IOptions<DiscoveryOptions> _discoveryOptions;

	public DashboardAggregateService(
		IJobControlRepository jobs, SiteRepository sites, TargetRepository targets,
		CredentialRepository credentials, RunArtifactProjectionService artifacts,
		IOptions<DiscoveryOptions> discoveryOptions)
	{
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(discoveryOptions);
		_jobs = jobs;
		_sites = sites;
		_targets = targets;
		_credentials = credentials;
		_artifacts = artifacts;
		_discoveryOptions = discoveryOptions;
	}

	public async Task<DashboardAggregate> BuildAsync(CancellationToken cancellationToken)
	{
		(IReadOnlyList<Site> sites, _) = await _sites.ListAsync(new PageRequest { Limit = 200 }, cancellationToken).ConfigureAwait(false);
		RunListResult runList = await _jobs.ListRunsAsync(RecentRunsScanned, 0, cancellationToken).ConfigureAwait(false);

		// RECENT RUNS is a compliance-only, DB-filtered fetch (issue #717) so operational
		// runs can never starve scan/remediate rows out of the capped list. Reuses the
		// keyset-paged history query's run_type allow-list rather than filtering the
		// unfiltered 20-run window above (which drives the per-site scan rollup only).
		RunHistoryPage recentCompliancePage = await _jobs.ListRunHistoryAsync(
			new RunHistoryQuery(
				States: null,
				RunTypes: ComplianceRunTypes,
				Since: null,
				Until: null,
				AfterCreatedAt: null,
				AfterId: null,
				Limit: RecentRunsReturned),
			cancellationToken).ConfigureAwait(false);

		IReadOnlyList<Target> allTargets = await ListAllTargetsAsync(sites, cancellationToken).ConfigureAwait(false);

		// One most-recent-completed scan run per site, newest-first since runList already is.
		Dictionary<Guid, RunSummary> latestScanRunBySite = [];
		foreach (RunSummary run in runList.Items)
		{
			if (!string.Equals(run.RunType, ScanJobType, StringComparison.Ordinal) || !IsCompleted(run.State))
			{
				continue;
			}

			Guid? siteId = TryParseSiteId(run.ScopeJson);
			if (siteId is { } id && !latestScanRunBySite.ContainsKey(id))
			{
				latestScanRunBySite[id] = run;
			}
		}

		List<DashboardSiteRow> siteRows = [];
		bool anyCountable = false;
		int fleetCatI = 0, fleetCatII = 0, fleetCatIII = 0;
		foreach (Site site in sites)
		{
			int targetCount = allTargets.Count(t => t.SiteId == site.Id);
			(double? pct, int? catI, int? catII, int? catIII, DateTimeOffset? lastScan) =
				latestScanRunBySite.TryGetValue(site.Id, out RunSummary? run)
					? await SummarizeRunAsync(run, cancellationToken).ConfigureAwait(false)
					: (null, null, null, null, null);

			if (catI is not null)
			{
				anyCountable = true;
				fleetCatI += catI.Value;
				fleetCatII += catII!.Value;
				fleetCatIII += catIII!.Value;
			}

			siteRows.Add(new DashboardSiteRow(site.Id, site.Name, targetCount, pct, catI, catII, catIII, lastScan));
		}

		Dictionary<string, int> byKind = allTargets
			.GroupBy(t => t.Kind, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

		List<DashboardAttentionRow> attention = [];
		attention.AddRange(await BuildCredentialAttentionAsync(cancellationToken).ConfigureAwait(false));
		attention.AddRange(BuildStaleInventoryAttention(allTargets));

		return new DashboardAggregate(
			TargetTotal: allTargets.Count,
			TargetsByKind: byKind,
			FindingsCountsAvailable: anyCountable,
			FindingsCatIOpen: fleetCatI,
			FindingsCatIIOpen: fleetCatII,
			FindingsCatIIIOpen: fleetCatIII,
			Sites: siteRows,
			RecentRuns: recentCompliancePage.Items,
			Attention: attention);
	}

	private async Task<IReadOnlyList<Target>> ListAllTargetsAsync(IReadOnlyList<Site> sites, CancellationToken cancellationToken)
	{
		List<Target> all = [];
		foreach (Site site in sites)
		{
			all.AddRange(await _targets.ListAllForSiteAsync(site.Id, cancellationToken).ConfigureAwait(false));
		}

		return all;
	}

	/// <summary>
	/// Reads <paramref name="run"/>'s scan-job artifacts (reusing
	/// <see cref="RunArtifactProjectionService"/> -- the same HDF parsing
	/// <c>GET /runs/{id}/artifacts</c> uses) and rolls them into one site-level
	/// compliance percent and CAT I/II/III sum. A run where no job's HDF is countable
	/// yields all-null (uncountable), never a fabricated 100%/0.
	/// </summary>
	private async Task<(double? Pct, int? CatI, int? CatII, int? CatIII, DateTimeOffset? LastScan)> SummarizeRunAsync(
		RunSummary run, CancellationToken cancellationToken)
	{
		IReadOnlyList<RunArtifactRow> rows = await _artifacts.GetArtifactsAsync(run.Id, cancellationToken).ConfigureAwait(false);
		List<RunArtifactRow> countable = [.. rows.Where(r => r.CountsAvailable)];
		DateTimeOffset? lastScan = run.CompletedAt is { } completed && DateTimeOffset.TryParse(completed, out DateTimeOffset parsed) ? parsed : null;

		if (countable.Count == 0)
		{
			return (null, null, null, null, lastScan);
		}

		int catI = countable.Sum(r => r.CatIOpen!.Value);
		int catII = countable.Sum(r => r.CatIIOpen!.Value);
		int catIII = countable.Sum(r => r.CatIIIOpen!.Value);

		// Compliance % = passing controls / total controls is not derivable from open-finding
		// counts alone (open findings != total control count without the passed/n-a rows the
		// HDF also carries, which RunArtifactRow does not surface). Approximate with "share of
		// countable targets that have zero open findings of any severity" -- consistent with
		// the KPI tile's own framing ("X of Y controls passing" is a fleet-detail figure the
		// artifacts endpoint does not expose; this is the coarser per-target signal that is
		// actually available here).
		int clean = countable.Count(r => r.CatIOpen == 0 && r.CatIIOpen == 0 && r.CatIIIOpen == 0);
		double pct = Math.Round(100.0 * clean / countable.Count, 1);

		return (pct, catI, catII, catIII, lastScan);
	}

	private async Task<IReadOnlyList<DashboardAttentionRow>> BuildCredentialAttentionAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<CredentialResponse> credentials = await _credentials.ListAsync(cancellationToken).ConfigureAwait(false);
		return [.. credentials
			.Where(c => string.Equals(c.Health, CredentialHealthStates.AuthFailing, StringComparison.Ordinal))
			.Select(c => new DashboardAttentionRow(
				"credential_auth_failing",
				$"Credential '{c.Name}' is failing authentication",
				c.Owner))];
	}

	private IReadOnlyList<DashboardAttentionRow> BuildStaleInventoryAttention(IReadOnlyList<Target> targets)
	{
		DateTimeOffset staleBefore = DateTimeOffset.UtcNow.AddMinutes(-_discoveryOptions.Value.StaleAfterMinutes);
		return [.. targets
			.Where(t => t.LastRefreshed is null || t.LastRefreshed.Value < staleBefore)
			.Select(t => new DashboardAttentionRow(
				"inventory_stale",
				$"Target '{t.Name}' inventory is stale",
				t.LastRefreshed is { } refreshed ? $"last refreshed {refreshed:yyyy-MM-dd HH:mm}Z" : "never refreshed"))];
	}

	private static bool IsCompleted(string state) =>
		state is "completed" or "completed_with_failures" or "aborted";

	private static Guid? TryParseSiteId(string scopeJson)
	{
		try
		{
			return ScanScopeParser.Parse(scopeJson).SiteId;
		}
		catch (FormatException)
		{
			return null;
		}
	}
}
