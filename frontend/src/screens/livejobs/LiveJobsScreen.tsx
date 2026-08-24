/**
 * Live Jobs — the global concurrent operational workspace (issue #590,
 * ADR-0019, epic #588). Replaces the scan-only Live Run screen
 * (`../liverun/LiveRunScreen.tsx`) as the top-level "what is happening right
 * now" surface: every active run, grouped, with jobs selectable among
 * concurrent work — never implying only one run/job can be active.
 *
 * Composition mirrors the Live Run screen's own decomposition: this file is
 * the orchestrator only. `useLiveJobs.ts` owns the REST seed + global SSE
 * subscription; `useSelectionFromQuery.ts` owns deep-link URL state;
 * `livejobs.ts` is the pure view-model/reducer; `detailRenderers.tsx` is the
 * renderer-registry seam #591 extends with type-specific detail views.
 */
import { useEffect, useMemo, useRef, type KeyboardEvent } from "react";
import { useAuth } from "../../lib/auth-context";
import { resolveJobDetailRenderer } from "./detailRenderers";
import { isActiveGroup, isTerminalJobState, waitReasonForJob, type LiveJobRow, type LiveRunGroup } from "./livejobs";
import { useLiveJobs } from "./useLiveJobs";
import { useSelectionFromQuery } from "./useSelectionFromQuery";
import "./LiveJobsScreen.css";

/** Flattened (group, job) pair — the unit keyboard navigation moves between. */
interface SelectableRow {
	group: LiveRunGroup;
	job: LiveJobRow | null;
}

function flattenRows(groups: LiveRunGroup[]): SelectableRow[] {
	const rows: SelectableRow[] = [];
	for (const group of groups) {
		rows.push({ group, job: null });
		for (const job of group.jobs) {
			rows.push({ group, job });
		}
	}
	return rows;
}

function rowKey(row: SelectableRow): string {
	return row.job ? `job:${row.job.job_id}` : `run:${row.group.run_id}`;
}

const STATE_TONE: Record<string, string> = {
	running: "acc",
	queued: "txt3",
	blocked: "bad",
	failed: "bad",
	"auth-failed": "bad",
	cancelled: "txt3",
	done: "ok",
	uploaded: "ok",
	completed: "ok",
	completed_with_failures: "warn",
	aborted: "bad",
	pending: "txt3",
};

function StateBadge({ state }: { state: string }) {
	const tone = STATE_TONE[state] ?? "txt3";
	return <span className={`live-jobs__state-badge live-jobs__state-badge--${tone}`}>{state}</span>;
}

export function LiveJobsRoute() {
	return <LiveJobsScreen />;
}

export function LiveJobsScreen() {
	const { user } = useAuth();
	const { snapshot, loading, loadError, connectionState } = useLiveJobs();
	const { runId, jobId, select } = useSelectionFromQuery();
	const listRef = useRef<HTMLDivElement>(null);

	const groups = useMemo(() => (snapshot ? snapshot.runs.filter(isActiveGroup) : []), [snapshot]);
	const rows = useMemo(() => flattenRows(groups), [groups]);

	// Selected row resolution: prefer an exact (run, job) match; fall back to
	// the run-header row if only `run` is set; null if the deep-linked
	// run/job no longer exists in the active set (AC4: "degrade clearly when
	// terminal/removed").
	const selectedRow = useMemo<SelectableRow | null>(() => {
		if (!runId) {
			return null;
		}
		const group = groups.find((g) => g.run_id === runId);
		if (!group) {
			return null;
		}
		if (jobId) {
			const job = group.jobs.find((j) => j.job_id === jobId) ?? null;
			return job ? { group, job } : { group, job: null };
		}
		return { group, job: null };
	}, [groups, runId, jobId]);

	// Auto-select the first row once data loads if nothing is selected yet —
	// mirrors ResultsScreen's "auto-select first row" precedent so the detail
	// pane isn't empty on first load, without overriding an explicit deep link.
	useEffect(() => {
		if (!runId && rows.length > 0) {
			const first = rows[0];
			select({ runId: first.group.run_id, jobId: first.job?.job_id });
		}
	}, [runId, rows, select]);

	const selectedIndex = selectedRow ? rows.findIndex((r) => rowKey(r) === rowKey(selectedRow)) : -1;

	const handleKeyDown = (event: KeyboardEvent) => {
		if (rows.length === 0) {
			return;
		}
		let nextIndex = selectedIndex;
		if (event.key === "ArrowDown") {
			nextIndex = selectedIndex < 0 ? 0 : Math.min(rows.length - 1, selectedIndex + 1);
		} else if (event.key === "ArrowUp") {
			nextIndex = selectedIndex < 0 ? 0 : Math.max(0, selectedIndex - 1);
		} else if (event.key === "Home") {
			nextIndex = 0;
		} else if (event.key === "End") {
			nextIndex = rows.length - 1;
		} else {
			return;
		}
		event.preventDefault();
		const next = rows[nextIndex];
		select({ runId: next.group.run_id, jobId: next.job?.job_id });
	};

	if (loading && !snapshot) {
		return (
			<div className="live-jobs-screen">
				<div className="live-jobs__empty">Loading active work…</div>
			</div>
		);
	}

	if (loadError && !snapshot) {
		return (
			<div className="live-jobs-screen">
				<div className="live-jobs__empty live-jobs__empty--error">{loadError}</div>
			</div>
		);
	}

	const DetailRenderer = selectedRow?.job ? resolveJobDetailRenderer(selectedRow.job.job_type) : null;

	return (
		<div className="live-jobs-screen">
			<div className="live-jobs__toolbar">
				<h1 className="live-jobs__title">Live Jobs</h1>
				<span className="live-jobs__count mono">{groups.length} active run{groups.length === 1 ? "" : "s"}</span>
				<span className="live-jobs__spacer" />
				{connectionState !== "open" && <span className="live-jobs__connection-note mono">stream {connectionState}…</span>}
			</div>

			<div className="live-jobs__body">
				<div
					className="live-jobs__list"
					ref={listRef}
					role="listbox"
					aria-label="Active runs and jobs"
					aria-activedescendant={selectedRow ? `live-jobs-row-${rowKey(selectedRow)}` : undefined}
					tabIndex={0}
					onKeyDown={handleKeyDown}
				>
					{rows.length === 0 && <div className="live-jobs__empty">No active runs or jobs right now.</div>}
					{groups.map((group) => (
						<div className="live-jobs__group" key={group.run_id}>
							<div
								id={`live-jobs-row-run:${group.run_id}`}
								role="option"
								aria-selected={selectedRow?.group.run_id === group.run_id && !selectedRow.job}
								className={`live-jobs__run-row ${selectedRow?.group.run_id === group.run_id && !selectedRow.job ? "is-selected" : ""}`}
								onClick={() => select({ runId: group.run_id, jobId: undefined })}
							>
								<span className="live-jobs__run-type">{group.run_type}</span>
								<span className="mono live-jobs__run-id">{group.run_id}</span>
								<StateBadge state={group.state} />
								{group.blocked && <span className="live-jobs__wait-reason">{group.blocked_reason ?? "blocked"}</span>}
								<span className="live-jobs__run-progress mono">
									{group.job_count_completed}/{group.job_count} complete
									{group.job_count_failed > 0 ? ` · ${group.job_count_failed} failed` : ""}
								</span>
								<span className="live-jobs__run-initiator">{group.initiated_by ?? "—"}</span>
							</div>
							{group.jobs.map((job) => {
								const selected = selectedRow?.job?.job_id === job.job_id;
								const reason = waitReasonForJob(job, group);
								return (
									<div
										id={`live-jobs-row-job:${job.job_id}`}
										key={job.job_id}
										role="option"
										aria-selected={selected}
										className={`live-jobs__job-row ${selected ? "is-selected" : ""}`}
										onClick={() => select({ runId: group.run_id, jobId: job.job_id })}
									>
										<span className="live-jobs__job-target">{job.target_name ?? job.target_id ?? job.job_id}</span>
										<StateBadge state={job.state} />
										{job.stage && <span className="live-jobs__job-stage">{job.stage}</span>}
										{reason && <span className="live-jobs__wait-reason">{reason}</span>}
										{isTerminalJobState(job.state) && job.finished_at && (
											<span className="live-jobs__job-finished mono">{job.finished_at}</span>
										)}
									</div>
								);
							})}
						</div>
					))}
				</div>

				<div className="live-jobs__detail-pane">
					{!selectedRow && <div className="live-jobs__empty">Select a run or job to see its detail.</div>}
					{selectedRow && !selectedRow.job && (
						<div className="live-jobs-detail" role="region" aria-label={`Run detail: ${selectedRow.group.run_id}`}>
							<dl className="live-jobs-detail__facts">
								<div>
									<dt>Run</dt>
									<dd className="mono">{selectedRow.group.run_id}</dd>
								</div>
								<div>
									<dt>Type</dt>
									<dd>{selectedRow.group.run_type}</dd>
								</div>
								<div>
									<dt>State</dt>
									<dd>{selectedRow.group.state}</dd>
								</div>
								<div>
									<dt>Initiated by</dt>
									<dd>{selectedRow.group.initiated_by ?? "—"}</dd>
								</div>
								<div>
									<dt>Jobs</dt>
									<dd>
										{selectedRow.group.job_count_completed}/{selectedRow.group.job_count} complete
										{selectedRow.group.job_count_failed > 0 ? `, ${selectedRow.group.job_count_failed} failed` : ""}
									</dd>
								</div>
								{selectedRow.group.blocked && (
									<div>
										<dt>Wait reason</dt>
										<dd>{selectedRow.group.blocked_reason ?? "blocked"}</dd>
									</div>
								)}
							</dl>
							<p className="live-jobs__hint">Select a job row for job-level detail and log.</p>
						</div>
					)}
					{selectedRow?.job && DetailRenderer && <DetailRenderer job={selectedRow.job} group={selectedRow.group} />}
				</div>
			</div>

			{user && <p className="live-jobs__role-note mono">viewing as {user.role}</p>}
		</div>
	);
}
