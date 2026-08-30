/**
 * Component-results panel (issue #745/#744 remainders): the run rollup
 * (six-status vocabulary, CAT severity counts, coverage ledger) plus a
 * per-component detail drill-down that surfaces artifact kinds and the
 * STIG Manager upload-attempt history (issue #744's new
 * `GET /jobs/{id}/upload-attempts` endpoint).
 *
 * Follows the design brief's Results map: a coverage omission (planned but
 * not yet resulted) is rendered as its own honest bucket, never dropped or
 * folded into "completed"; `execution_error` and `skipped` are visually
 * distinct from `completed` (no pass/fail binary); selecting a component
 * loads its upload-attempt history and artifact kinds on demand only (same
 * "log-first, fetch on selection" idiom `ComponentJobBoard` established in
 * #941 — never eagerly loads every job's attempt history up front).
 *
 * The component list mirrors #941's `computeWindow` idiom above
 * `WINDOW_THRESHOLD` rows: below that, every row renders directly (a scan
 * against a handful of targets needs no virtualization machinery), and
 * above it the same fixed-row-height windowing `ComponentJobBoard` uses
 * keeps the DOM bounded regardless of run size.
 */
import { useMemo, useState } from "react";
import { computeWindow } from "../liverun/componentJobs";
import type { RunArtifactRow, RunJobItem } from "./results";
import {
	componentResultStatusClass,
	componentResultStatusLabel,
	totalOpenBySeverity,
	totalResultedComponents,
	unresultedComponentCount,
	type ComponentResultRollup,
} from "./component-results";
import { useUploadAttempts } from "./useUploadAttempts";

const ROW_HEIGHT = 32;
const VIEWPORT_HEIGHT = 320;
const WINDOW_THRESHOLD = 60;

export function ComponentResultsPanel({
	rollup,
	rollupLoading,
	rollupUnavailable,
	jobs,
	artifacts,
}: {
	rollup: ComponentResultRollup | null;
	rollupLoading: boolean;
	rollupUnavailable: boolean;
	jobs: RunJobItem[];
	artifacts: RunArtifactRow[] | null;
}) {
	const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
	const [scrollTop, setScrollTop] = useState(0);
	const { attempts, loading: attemptsLoading, unavailable: attemptsUnavailable } = useUploadAttempts(selectedJobId);

	const artifactsByJob = useMemo(() => {
		const byJob = new Map<string, RunArtifactRow>();
		for (const row of artifacts ?? []) {
			byJob.set(row.job_id, row);
		}
		return byJob;
	}, [artifacts]);

	const virtualize = jobs.length > WINDOW_THRESHOLD;
	const win = virtualize ? computeWindow(scrollTop, VIEWPORT_HEIGHT, ROW_HEIGHT, jobs.length) : null;
	const visibleJobs = win ? jobs.slice(win.start, win.end) : jobs;

	const selectedJob = jobs.find((j) => j.id === selectedJobId) ?? null;
	const selectedArtifact = selectedJobId ? (artifactsByJob.get(selectedJobId) ?? null) : null;

	return (
		<div className="results__panel">
			<div className="results__panel-header">
				<div className="results__panel-title">COMPONENT RESULTS</div>
				<div className="results__spacer" />
				{rollup && (
					<div className="mono results__panel-meta">
						{totalResultedComponents(rollup)} / {rollup.planned_component_count} components resulted
					</div>
				)}
			</div>

			{rollupLoading && <div className="results__empty">Loading component results…</div>}
			{!rollupLoading && rollupUnavailable && (
				<div className="results__empty">
					Component results could not be loaded for this run — GET /runs/{"{id}"}/component-results/summary failed.
				</div>
			)}
			{!rollupLoading && !rollupUnavailable && rollup && rollup.by_status.length === 0 && (
				<div className="results__empty">No component results yet — this run has not completed any components.</div>
			)}

			{!rollupLoading && !rollupUnavailable && rollup && rollup.by_status.length > 0 && (
				<>
					<StatusBuckets rollup={rollup} />
					<CoverageLedger rollup={rollup} />
					<SeverityTotals rollup={rollup} />
				</>
			)}

			<div className="results__cresult-body">
				<div
					className="results__cresult-list"
					style={virtualize ? { height: VIEWPORT_HEIGHT, overflowY: "auto" } : undefined}
					onScroll={virtualize ? (e) => setScrollTop(e.currentTarget.scrollTop) : undefined}
					role="listbox"
					aria-label="Components"
				>
					{win && <div style={{ height: win.topPad }} aria-hidden="true" />}
					{visibleJobs.map((job) => (
						<button
							type="button"
							key={job.id}
							role="option"
							aria-selected={job.id === selectedJobId}
							className={`results__cresult-row ${job.id === selectedJobId ? "is-selected" : ""}`}
							style={virtualize ? { height: ROW_HEIGHT } : undefined}
							onClick={() => setSelectedJobId(job.id)}
						>
							<span className="mono">{job.target_name ?? job.id}</span>
							<span className="results__cresult-row-state">{job.state}</span>
						</button>
					))}
					{win && <div style={{ height: win.bottomPad }} aria-hidden="true" />}
					{jobs.length === 0 && <div className="results__empty">No components in this run.</div>}
				</div>

				<div className="results__cresult-detail">
					{!selectedJob && <div className="results__panel-empty">Select a component to view artifacts and upload history.</div>}
					{selectedJob && (
						<>
							<div className="results__cresult-detail-title mono">{selectedJob.target_name ?? selectedJob.id}</div>
							<div className="results__cresult-detail-row">
								<span className="results__stat-label">Artifacts</span>
								<span className="mono">
									{selectedArtifact && selectedArtifact.artifact_kinds.length > 0
										? selectedArtifact.artifact_kinds
												.map((kind) => `${kind} (${digestSummary(selectedArtifact, kind)})`)
												.join(" · ")
												.toUpperCase()
										: "none recorded"}
								</span>
							</div>

							<div className="results__panel-title results__cresult-subtitle">UPLOAD ATTEMPT HISTORY</div>
							{attemptsLoading && <div className="results__empty">Loading upload attempts…</div>}
							{!attemptsLoading && attemptsUnavailable && (
								<div className="results__empty">
									Upload attempts could not be loaded — GET /jobs/{"{id}"}/upload-attempts failed.
								</div>
							)}
							{!attemptsLoading && !attemptsUnavailable && attempts.length === 0 && (
								<div className="results__empty">No upload attempts recorded for this component.</div>
							)}
							{!attemptsLoading && !attemptsUnavailable && attempts.length > 0 && (
								<table className="results__table results__cresult-attempts">
									<thead>
										<tr>
											<th>#</th>
											<th>ENDPOINT</th>
											<th>COLLECTION</th>
											<th>STATUS</th>
											<th>WHEN</th>
										</tr>
									</thead>
									<tbody>
										{attempts.map((attempt) => (
											<tr key={attempt.attempt_number}>
												<td className="mono">{attempt.attempt_number}</td>
												<td className="mono">{attempt.endpoint ?? "—"}</td>
												<td className="mono">{attempt.collection ?? "—"}</td>
												<td>
													<span className={`results__upload-pill results__upload-pill--${attempt.status}`}>
														{attempt.status}
													</span>
													{attempt.error_detail && <div className="results__cresult-error">{attempt.error_detail}</div>}
												</td>
												<td className="mono">{attempt.attempted_at}</td>
											</tr>
										))}
									</tbody>
								</table>
							)}
						</>
					)}
				</div>
			</div>
		</div>
	);
}

/** Artifact rows carry no digest/size today (`RunArtifactResponse` only has
 * `artifact_kinds`) — the design brief calls for linking digest/size once
 * the per-component finding/artifact read API (PR #952's stated remainder)
 * lands. Until then this reports "recorded", not a fabricated value. */
function digestSummary(_artifact: RunArtifactRow, _kind: string): string {
	return "recorded";
}

function StatusBuckets({ rollup }: { rollup: ComponentResultRollup }) {
	return (
		<div className="results__cresult-buckets">
			{rollup.by_status.map((row) => (
				<div key={row.status} className={`results__cresult-status ${componentResultStatusClass(row.status)}`}>
					<div className="results__cresult-status-label">{componentResultStatusLabel(row.status)}</div>
					<div className="mono results__cresult-status-count">{row.component_count}</div>
					{/* Issue #1144/#1247: execution_error_count is a FINDING count, not a
					   component count -- shown as its own suffix line, distinguished from
					   both component_count above and any open (CAT) finding count, never
					   merged into either. */}
					{row.execution_error_count > 0 && (
						<div
							className="results__cresult-status-suffix"
							title="Controls that produced no genuine compliance verdict (execution error) -- distinct from an open finding."
						>
							{row.execution_error_count} control{row.execution_error_count === 1 ? "" : "s"} errored
						</div>
					)}
				</div>
			))}
		</div>
	);
}

/** Honest coverage ledger: planned vs. resulted, including the "not yet
 * resulted" gap as its own line — never folded into "completed" and never
 * hidden. */
function CoverageLedger({ rollup }: { rollup: ComponentResultRollup }) {
	const resulted = totalResultedComponents(rollup);
	const unresulted = unresultedComponentCount(rollup);
	return (
		<div className="results__stat-row">
			<span className="results__stat-label">Coverage</span>
			<span className="mono results__stat-value">
				{resulted} resulted / {unresulted} not yet resulted / {rollup.planned_component_count} planned
			</span>
		</div>
	);
}

function SeverityTotals({ rollup }: { rollup: ComponentResultRollup }) {
	const totals = totalOpenBySeverity(rollup);
	return (
		<div className="results__cresult-severity-totals">
			<span className="results__severity results__severity--1">
				CAT I <span className="mono">{totals.catI}</span>
			</span>
			<span className="results__severity results__severity--2">
				CAT II <span className="mono">{totals.catII}</span>
			</span>
			<span className="results__severity results__severity--3">
				CAT III <span className="mono">{totals.catIII}</span>
			</span>
		</div>
	);
}
