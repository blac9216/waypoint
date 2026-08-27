/**
 * Compliance Scan Results — docs/ui/prototype/README.md screen 4, the last
 * screen of milestone M2 (issue #27, part of epic #13). Renamed from
 * "Results & History" and filtered to compliance-owned run types only
 * (issue #591, ADR-0019: non-compliance run types route to their own domain
 * screens via the Live Jobs type renderers) — `useRunList.ts`'s
 * `COMPLIANCE_RUN_TYPES` is the filter. Two panes: a searchable run history
 * list (left rail, scan/remediate runs only) and a run detail pane (KPI
 * tiles, per-target artifacts table, attestations-applied + upload-status
 * sidebar, export and Remediate actions).
 *
 * This component is the orchestrator only (issue #416 decomposition — no
 * behavior change): run-list/search/selection state lives in `useRunList`,
 * the selected run's detail/jobs/artifacts/attestations in `useRunDetail`,
 * the CKL export action in `useCklExport`, KPI derivation in
 * `results-metrics.ts`, and the three panels (`ArtifactsTable`,
 * `AttestationsPanel`, `UploadStatusPanel`) are separate presentation
 * components in this directory.
 *
 * Data sources — see results.ts's module doc for the full breakdown:
 *   - Run list/detail/jobs: `GET /runs`, `GET /runs/{id}`, `GET /runs/{id}/jobs`
 *     (all merged — RunsController).
 *   - Per-target artifacts + STIG Manager upload status:
 *     `GET /runs/{id}/artifacts` (issue #299/#305). Rows carry
 *     `counts_available`; the CAT counts are omitted (not zero) when the
 *     HDF is absent/corrupt, and this screen renders that as an explicit
 *     "n/a" pill rather than a false "0 open" (issue #307 — a corrupt scan
 *     must never look clean). Still renders a graceful "not available yet"
 *     empty state if the call itself fails.
 *   - Attestations-applied: `GET /runs/{id}/attestations-applied`
 *     (#299/#305, wire shape updated by #306/PR #336) is the primary source
 *     for the sidebar's waiver list. Each row is a PERSISTED, AT-SCAN-TIME
 *     snapshot (see `results.ts`'s `AppliedAttestation` doc comment) —
 *     immutable per run, not re-derived on load — and the panel shows the
 *     real `applied_at` scan-time timestamp rather than the old
 *     live-resolution caveat (issue #307's caveat no longer applies; #306
 *     closed the gap it described). The sidebar also still calls the
 *     MERGED `GET /config-docs/resolve?profile&target&kind=attestation`
 *     (issue #266) to flag which of those rows are currently EXPIRED,
 *     mirroring what the persisted ledger records server-side.
 *
 * Severity labels (AC1, design-brief "Layout Rules Learned the Hard Way" #4):
 * always rendered as the full "CAT I"/"CAT II"/"CAT III" text, in a pill wide
 * enough for the longest label with no `text-overflow: ellipsis` — never
 * abbreviated to a bare Roman numeral. See ResultsScreen.test.tsx for the
 * non-truncation assertion. (Rendered by `ArtifactsTable.tsx`.)
 *
 * STIG Manager upload status + retry: derived from job state via
 * `stigManagerStatusLabel` (results.ts), matching `LiveRunScreen`'s
 * "state model drives presentation" convention. The retry button is a
 * visible-but-disabled stub (`roleGateProps`-style treatment, reason text
 * instead of a role) — actually retrying uploads needs the STIG Manager
 * integration (issue #25) to land first.
 *
 * Remediate: visible, disabled, Admin-gated via `roleGateProps` exactly like
 * the pre-existing placeholder in screens.tsx (which this component
 * replaces) — stubbed until M4 (epic #15).
 */
import { useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { roleGateProps } from "../../lib/roles";
import { ArtifactsTable } from "./ArtifactsTable";
import { AttestationsPanel } from "./AttestationsPanel";
import { ComponentResultsPanel } from "./ComponentResultsPanel";
import { kpiTiles } from "./results-metrics";
import { formatRunDuration, formatTimestamp, scopeSiteId } from "./results";
import "./ResultsScreen.css";
import { PurgeRunPanel } from "./PurgeRunPanel";
import { RunHistoryDeletionPanel } from "./RunHistoryDeletionPanel";
import { UploadStatusPanel } from "./UploadStatusPanel";
import { useCklExport } from "./useCklExport";
import { useComponentResults } from "./useComponentResults";
import { useRunDetail } from "./useRunDetail";
import { useRunList } from "./useRunList";

export function ResultsScreen() {
	const { user, token } = useAuth();
	const { runsLoading, runsError, search, setSearch, filteredRuns, selectedRunId, selectedRow, handleSelectRun } = useRunList();

	const { run, jobs, artifacts, artifactsUnavailable, expiredAttestations, attestationsApplied, loading, loadError } =
		useRunDetail(selectedRunId, selectedRow);

	const { exporting, handleExport } = useCklExport(run, jobs, token);

	// Issue #745 remainder: the component-results rollup is fetched
	// independently of jobs/artifacts/attestations above -- a purged run
	// renders the tombstone panel below and never reaches this fetch at all
	// (the `!purged` guard around the whole results pane), so "purged runs
	// render the tombstone state unchanged" holds without any extra branching
	// here.
	const { rollup, loading: rollupLoading, unavailable: rollupUnavailable } = useComponentResults(selectedRunId);

	// Purged (issue #656/#594): once PurgeRunPanel confirms the tombstone
	// outcome, the results panes/export/remediate actions are hidden — a
	// purged run's artifacts/attestations are genuinely gone server-side, so
	// this screen must render that honestly rather than keep showing stale
	// rows next to the tombstone. Reset per-run via `key` below (PurgeRunPanel
	// re-mounts and re-checks on selection change).
	const [purged, setPurged] = useState(false);
	// eslint-disable-next-line react-hooks/exhaustive-deps
	useEffect(() => setPurged(false), [selectedRunId]);

	// Remediate is a stub regardless of role (epic #15/M4 has not landed) — but
	// still runs through roleGateProps for the same visible-but-disabled
	// treatment and opacity every other role-gated control uses, then the
	// stub note is layered on top of (never instead of) the role reason so a
	// sub-Admin role sees why AND that it's not built yet.
	const remediateRoleGate = user ? roleGateProps(user.role, "Admin") : { disabled: true, style: { opacity: 0.42 } };
	const remediateGate = {
		...remediateRoleGate,
		disabled: true,
		title:
			user && user.role !== "Admin"
				? "Requires Admin — remediation is not available to your role, and is stubbed until M4 (epic #15) for every role"
				: "Remediation is stubbed until M4 (epic #15) — typed confirmation not yet implemented",
	};

	if (runsLoading) {
		return (
			<div className="results-screen">
				<div className="results__empty">Loading run history…</div>
			</div>
		);
	}

	if (runsError) {
		return (
			<div className="results-screen">
				<div className="results__empty results__empty--error">{runsError}</div>
			</div>
		);
	}

	return (
		<div className="results-screen">
			<div className="results__sidebar-list">
				<div className="results__search-row">
					<input
						className="results__search"
						placeholder="search runs…"
						value={search}
						onChange={(e) => setSearch(e.target.value)}
						aria-label="Search runs"
					/>
				</div>
				<div className="results__run-list">
					{filteredRuns.map((row) => (
						<button
							type="button"
							key={row.id}
							className={`results__run-row ${row.id === selectedRunId ? "is-selected" : ""}`}
							onClick={() => handleSelectRun(row)}
						>
							<div className="results__run-row-top">
								<span className={`results__run-dot results__run-dot--${row.state}`} />
								<span className="mono results__run-id">{row.id}</span>
								<span className="results__run-kind">{row.run_type}</span>
							</div>
							<div className="results__run-row-meta">
								{scopeSiteId(row.scope) ?? "—"} · {row.job_count} targets ·{" "}
								{formatRunDuration(row.started_at, row.completed_at)}
							</div>
							<div className="mono results__run-row-when">
								{formatTimestamp(row.created_at)} · {row.initiated_by ?? "system"}
							</div>
						</button>
					))}
					{filteredRuns.length === 0 && <div className="results__empty">No runs match "{search}".</div>}
				</div>
			</div>

			<div className="results__detail">
				{!run && <div className="results__empty">Select a run to view details.</div>}
				{run && (
					<>
						<div className="results__detail-header">
							<div className="results__title-block">
								<div className="mono results__run-title">{run.id}</div>
								<div className="results__run-subtitle">
									{scopeSiteId(run.scope) ?? "—"} · {run.job_count} targets · {run.run_type} ·{" "}
									{run.completed_at ? `completed ${formatTimestamp(run.completed_at)}` : run.state} in{" "}
									{formatRunDuration(run.started_at, run.completed_at)} · initiated by {run.initiated_by ?? "system"}
								</div>
							</div>
							<div className="results__spacer" />
							{!purged && (
								<>
									<button
										type="button"
										className="results__export-btn"
										onClick={handleExport}
										disabled={exporting || jobs.length === 0}
									>
										{exporting ? "Exporting…" : "Export CKL bundle"}
									</button>
									<button type="button" className="results__remediate-btn" {...remediateGate}>
										Remediate findings…
									</button>
								</>
							)}
						</div>

						<PurgeRunPanel key={run.id} run={run} onPurged={() => setPurged(true)} />

						{/* Issue #592 (epic #588): generic operational-history deletion is a
						   distinct, later-ordered action from the purge above -- the server
						   itself enforces "purge first" (409 requires_domain_purge_first) for
						   scan/remediate runs, so this panel only renders once purged is
						   true rather than let an operator hit that refusal from this screen. */}
						{purged && <RunHistoryDeletionPanel key={`history-${run.id}`} run={run} />}

						{!purged && (
							<>
								<div className="results__kpis">
									{kpiTiles(run, artifacts).map((tile) => (
										<div key={tile.label} className="results__kpi-tile">
											<div className="results__kpi-label">{tile.label}</div>
											<div className={`results__kpi-value ${tile.className}`}>{tile.value}</div>
										</div>
									))}
								</div>

								{loadError && <div className="results__action-error">{loadError}</div>}

								<div className="results__panes">
									<ArtifactsTable loading={loading} artifacts={artifacts} unavailable={artifactsUnavailable} jobs={jobs} />
									<div className="results__side-panels">
										<AttestationsPanel expired={expiredAttestations} applied={attestationsApplied} />
										<UploadStatusPanel jobs={jobs} />
									</div>
								</div>

								<ComponentResultsPanel
									rollup={rollup}
									rollupLoading={rollupLoading}
									rollupUnavailable={rollupUnavailable}
									jobs={jobs}
									artifacts={artifacts}
								/>
							</>
						)}
					</>
				)}
			</div>
		</div>
	);
}
