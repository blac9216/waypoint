/**
 * Dashboard — docs/ui/prototype/README.md screen 2 (issue #513, consolidating
 * #31's SCHEDULES card and #32's ATTENTION card). Four KPI tiles, a SITE
 * POSTURE table, RECENT RUNS, and an APPLIANCE / SCHEDULES / ATTENTION
 * sidebar. Replaces the `PlaceholderScreen` that stood in for this screen
 * since #11.
 *
 * Data sources:
 *   - KPI tiles, SITE POSTURE, RECENT RUNS, ATTENTION: `GET /dashboard`
 *     (`useDashboard`/`dashboard.ts`) — the new aggregate this issue adds
 *     (`DashboardController`).
 *   - APPLIANCE card: `useSystem()` (`GET /system`, already polled by the
 *     always-on chrome) — version/mode/update-available, matching the
 *     existing top-bar's data source rather than a second fetch.
 *   - SCHEDULES card: `GET /schedules` (`lib/schedules.ts`) — next-run/
 *     last-result, empty state when none exist yet.
 *
 * ATTENTION is scoped to exactly the in-scope signals from the issue's
 * 2026-08-19 feasibility audit (credential auth-failing, stale inventory);
 * unattested CAT I is explicitly OUT (tracked in #514) and depot-token-expiry
 * is a dormant signal type that never appears on the wire yet (see
 * `DashboardAttentionItem`'s doc comment) — this screen renders whatever
 * `attention` sends without inventing a fourth kind client-side.
 */
import { useAuth } from "../../lib/auth-context";
import { roleGateProps } from "../../lib/roles";
import { scheduleStateLabel } from "../../lib/schedules";
import { useRouter } from "../../lib/router-context";
import { useSystem } from "../../lib/system-context";
import { complianceTone, formatTimestamp, isComplianceRun, runStateTone, scopeSiteId } from "./dashboard";
import "./DashboardScreen.css";
import { useDashboard } from "./useDashboard";

const ATTENTION_LABELS: Record<string, string> = {
	credential_auth_failing: "Credential failing",
	inventory_stale: "Inventory stale",
	depot_token_expiring: "Depot token expiring",
};

export function DashboardScreen() {
	const { user } = useAuth();
	const { system, mode } = useSystem();
	const { navigate } = useRouter();
	const { data, loading, error, schedules } = useDashboard();

	const adminGate = user ? roleGateProps(user.role, "Admin") : { disabled: true, style: { opacity: 0.42 } };

	if (loading) {
		return (
			<div className="dashboard-screen">
				<div className="dashboard__empty">Loading dashboard…</div>
			</div>
		);
	}

	if (error || !data) {
		return (
			<div className="dashboard-screen">
				<div className="dashboard__empty dashboard__empty--error">{error ?? "Could not load the dashboard."}</div>
			</div>
		);
	}

	const kindLabel: Record<string, string> = { vsphere: "vCenter", "nsx-api": "NSX", ssh: "SSH" };
	// RECENT RUNS is a compliance summary (issue #717) — operational run
	// types (discover/credential-test/content-pull/catalog-index/etc.) are
	// excluded here since they'd link into Results only to be filtered back
	// out by `useRunList.ts`'s matching COMPLIANCE_RUN_TYPES set. Operational
	// activity stays reachable via its own Jobs/history surface.
	const recentComplianceRuns = data.recent_runs.filter((run) => isComplianceRun(run.run_type));

	return (
		<div className="dashboard-screen">
			<div className="dashboard__kpis">
				<div className="dashboard__tile">
					<div className="dashboard__tile-label">OPEN FINDINGS</div>
					{data.findings.counts_available ? (
						<div className="dashboard__findings-row">
							<div className="dashboard__finding dashboard__finding--cat-i">
								<div className="dashboard__finding-value">{data.findings.cat_i_open}</div>
								<div className="dashboard__finding-label">CAT I</div>
							</div>
							<div className="dashboard__finding dashboard__finding--cat-ii">
								<div className="dashboard__finding-value">{data.findings.cat_ii_open}</div>
								<div className="dashboard__finding-label">CAT II</div>
							</div>
							<div className="dashboard__finding dashboard__finding--cat-iii">
								<div className="dashboard__finding-value">{data.findings.cat_iii_open}</div>
								<div className="dashboard__finding-label">CAT III</div>
							</div>
						</div>
					) : (
						<div className="dashboard__tile-nodata">No completed scans yet</div>
					)}
				</div>

				<div className="dashboard__tile">
					<div className="dashboard__tile-label">TARGETS</div>
					<div className="dashboard__tile-headline">
						<span className="dashboard__tile-big">{data.targets.total}</span>
						<span className="dashboard__tile-sub mono">across {data.targets.site_count} sites</span>
					</div>
					<div className="dashboard__chips">
						{Object.entries(data.targets.by_kind).map(([kind, count]) => (
							<span key={kind} className="dashboard__chip mono">
								{count} {kindLabel[kind] ?? kind}
							</span>
						))}
					</div>
				</div>

				<div className="dashboard__tile">
					<div className="dashboard__tile-label">SITES</div>
					<div className="dashboard__tile-headline">
						<span className="dashboard__tile-big">{data.sites.length}</span>
					</div>
					<div className="dashboard__tile-nodata mono">
						{data.sites.filter((s) => s.compliance_percent !== null).length} with scan data
					</div>
				</div>

				<div className="dashboard__tile">
					<div className="dashboard__tile-label">DEPLOYMENT</div>
					<div className={`dashboard__tile-headline dashboard__mode--${mode}`}>
						<span className="dashboard__tile-big" style={{ fontSize: 18 }}>
							{mode === "connected" ? "Connected" : mode === "disconnected" ? "Air-gapped" : "Checking…"}
						</span>
					</div>
					<div className="dashboard__tile-nodata mono">{system ? `v${system.version}` : "—"}</div>
				</div>
			</div>

			<div className="dashboard__grid">
				<div className="dashboard__column">
					<div className="dashboard__card">
						<div className="dashboard__card-header">
							<div className="dashboard__card-title">SITE POSTURE</div>
						</div>
						<table className="dashboard__table">
							<thead>
								<tr>
									<th>SITE</th>
									<th>TARGETS</th>
									<th>COMPLIANCE</th>
									<th className="dashboard__table-num">CAT I</th>
									<th className="dashboard__table-num">CAT II</th>
									<th className="dashboard__table-num">CAT III</th>
									<th>LAST SCAN</th>
								</tr>
							</thead>
							<tbody>
								{data.sites.map((site) => (
									<tr key={site.id}>
										<td>{site.name}</td>
										<td className="mono">{site.target_count}</td>
										<td>
											{site.compliance_percent === null ? (
												<span className="dashboard__nodata">no scan data</span>
											) : (
												<div className="dashboard__compliance">
													<div className="dashboard__compliance-bar">
														<div
															className={`dashboard__compliance-fill dashboard__compliance-fill--${complianceTone(site.compliance_percent)}`}
															style={{ width: `${site.compliance_percent}%` }}
														/>
													</div>
													<span className="mono">{site.compliance_percent}%</span>
												</div>
											)}
										</td>
										<td className="dashboard__table-num mono dashboard__cat-i">{site.cat_i_open ?? "—"}</td>
										<td className="dashboard__table-num mono dashboard__cat-ii">{site.cat_ii_open ?? "—"}</td>
										<td className="dashboard__table-num mono">{site.cat_iii_open ?? "—"}</td>
										<td className="mono">{formatTimestamp(site.last_scan_at)}</td>
									</tr>
								))}
								{data.sites.length === 0 && (
									<tr>
										<td colSpan={7} className="dashboard__empty-row">
											No sites configured yet.
										</td>
									</tr>
								)}
							</tbody>
						</table>
					</div>

					<div className="dashboard__card">
						<div className="dashboard__card-header">
							<div className="dashboard__card-title">RECENT RUNS</div>
							<div className="dashboard__spacer" />
							<button type="button" className="dashboard__link-btn" onClick={() => navigate("/results")}>
								View all →
							</button>
						</div>
						{recentComplianceRuns.map((run) => (
							<button
								type="button"
								key={run.id}
								className="dashboard__run-row"
								onClick={() => navigate(`/results?run=${encodeURIComponent(run.id)}`)}
							>
								<span className={`dashboard__dot dashboard__dot--${runStateTone(run.state)}`} />
								<span className="mono dashboard__run-id">{run.id}</span>
								<span className="dashboard__run-kind">{run.run_type}</span>
								<span className="dashboard__run-meta">
									{scopeSiteId(run.scope) ?? "—"} · {run.job_count} targets
								</span>
								<span className={`mono dashboard__run-state dashboard__run-state--${runStateTone(run.state)}`}>
									{run.state}
								</span>
								<span className="mono dashboard__run-when">{formatTimestamp(run.created_at)}</span>
							</button>
						))}
						{recentComplianceRuns.length === 0 && <div className="dashboard__empty-row">No scan or remediation runs yet.</div>}
					</div>
				</div>

				<div className="dashboard__column">
					<div className="dashboard__card dashboard__card--pad">
						<div className="dashboard__card-title dashboard__card-title--spaced">APPLIANCE</div>
						<div className="dashboard__kv">
							<span className="dashboard__kv-label">Version</span>
							<span className="mono">{system?.version ?? "—"}</span>
						</div>
						<div className="dashboard__kv">
							<span className="dashboard__kv-label">Deployment</span>
							<span className={`mono dashboard__mode--${mode}`}>
								{mode === "connected" ? "Connected" : mode === "disconnected" ? "Air-gapped" : "Checking…"}
							</span>
						</div>
						{system?.update_available && (
							<div className="dashboard__update-banner">
								<div className="dashboard__update-title">Update {system.update_available} available</div>
								<button type="button" className="dashboard__update-btn" {...adminGate} onClick={() => navigate("/config")}>
									Review update
								</button>
							</div>
						)}
					</div>

					<div className="dashboard__card dashboard__card--pad">
						<div className="dashboard__card-title dashboard__card-title--spaced">SCHEDULES</div>
						{schedules.map((schedule) => {
							const { label, tone } = scheduleStateLabel(schedule);
							return (
								<div key={schedule.id} className="dashboard__schedule-row">
									<div className="dashboard__schedule-top">
										<span className={`dashboard__dot dashboard__dot--${tone}`} />
										<span className="dashboard__schedule-name">{schedule.name}</span>
										<span className={`mono dashboard__schedule-state dashboard__schedule-state--${tone}`}>{label}</span>
									</div>
									<div className="mono dashboard__schedule-meta">
										{schedule.job_type} · next {formatTimestamp(schedule.next_run_at)}
										{schedule.last_result ? ` · last ${schedule.last_result}` : ""}
									</div>
								</div>
							);
						})}
						{schedules.length === 0 && <div className="dashboard__tile-nodata">No schedules configured yet.</div>}
					</div>

					<div className="dashboard__card dashboard__card--pad">
						<div className="dashboard__card-title dashboard__card-title--spaced">ATTENTION</div>
						{data.attention.map((item, index) => (
							<div key={`${item.kind}-${index}`} className={`dashboard__attention-row dashboard__attention-row--${item.kind}`}>
								<div className="dashboard__attention-spine" />
								<div>
									<div className="dashboard__attention-text">{item.text}</div>
									<div className="mono dashboard__attention-meta">
										{ATTENTION_LABELS[item.kind] ?? item.kind} · {item.meta}
									</div>
								</div>
							</div>
						))}
						{data.attention.length === 0 && <div className="dashboard__tile-nodata">Nothing needs attention.</div>}
					</div>
				</div>
			</div>
		</div>
	);
}
