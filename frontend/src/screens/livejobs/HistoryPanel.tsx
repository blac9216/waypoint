/**
 * History mode for the Jobs workspace (issue #708/#689, epic #706) — browses
 * TERMINAL runs alongside the active-work mode `LiveJobsScreen.tsx` already
 * renders, via the new server-side filtered/cursor-paged `GET /runs/history`
 * (`useRunHistory.ts`). Reuses #591's `resolveJobDetailRenderer` registry and
 * the #581 event-history client (through `GenericJobDetail`'s
 * `useJobHistory.ts`, the same terminal-job code path active mode's detail
 * pane already uses for a finished job) rather than building a second detail
 * presentation — a history-mode row is exactly a terminal `LiveRunGroup`, so
 * it is mapped through the SAME `mapRunListItem`/`mapRunJobItem` helpers
 * `useLiveJobs.ts` uses for the active list.
 *
 * Windowing vs. deletion (epic #706's central distinction for this issue):
 * the default filter excludes `scan`/`remediate` from `DEFAULT_STATE_FILTER`'s
 * run_type scope -- compliance runs are reachable via the explicit "Include
 * compliance runs" toggle, never shown by default. This is a client-side
 * default query, NOT a deletion -- flipping the toggle calls the exact same
 * `GET /runs/history` endpoint with a wider `run_type` filter and shows
 * whatever compliance runs still exist (purged or not; purge/deletion status
 * is a separate concern rendered per-row via `useHistoryTombstone.ts`).
 */
import { useEffect, useMemo, useState } from "react";
import { fetchRunJobs, type RunListItem } from "../results/results";
import { resolveJobDetailRenderer } from "./detailRenderers.registry";
import { useHistoryTombstone } from "./useHistoryTombstone";
import { mapRunJobItem, mapRunListItem, type LiveJobRow, type LiveRunGroup } from "./livejobs";
import { useRunHistory, type RunHistoryFilters } from "./useRunHistory";

const TERMINAL_STATES = "completed,completed_with_failures,aborted";

/** Every closed `runs.run_type` value except the two compliance-owned ones
 * (`scan`, `remediate`) -- ADR-0019/epic #706: compliance run history is
 * windowed out of the default view, reachable only by explicit filter. Must
 * mirror the backend closed set (`Waypoint.Core.Jobs.RunTypes.All`, authoritative
 * `runs_run_type_check` as of migration 0042) minus scan/remediate; the sync test
 * `runTypes.test.ts` asserts this against the backend list. */
export const NON_COMPLIANCE_RUN_TYPES =
	"discover,download,catalog-index,bundle-export,bundle-import,content-library-sync,content-pull,content-import,update,credential-test,tool-install,purge";

function defaultFilters(includeCompliance: boolean): RunHistoryFilters {
	return {
		state: TERMINAL_STATES,
		runType: includeCompliance ? undefined : NON_COMPLIANCE_RUN_TYPES,
	};
}

const STATE_LABEL: Record<string, string> = {
	completed: "Completed",
	completed_with_failures: "Completed with failures",
	aborted: "Aborted",
};

export function HistoryPanel() {
	const [includeCompliance, setIncludeCompliance] = useState(false);
	const filters = useMemo(() => defaultFilters(includeCompliance), [includeCompliance]);
	const { items, loading, loadError, hasMore, loadMore } = useRunHistory(filters);
	const [selectedRunId, setSelectedRunId] = useState<string | undefined>(undefined);
	const [selectedJobs, setSelectedJobs] = useState<LiveJobRow[] | null>(null);
	const [jobsLoading, setJobsLoading] = useState(false);
	const [jobsError, setJobsError] = useState<string | null>(null);

	const selectedRun = items.find((r) => r.id === selectedRunId);
	const { tombstone } = useHistoryTombstone(selectedRunId);

	useEffect(() => {
		if (!selectedRun) {
			setSelectedJobs(null);
			return;
		}
		let cancelled = false;
		setJobsLoading(true);
		setJobsError(null);
		fetchRunJobs(selectedRun.id)
			.then((jobs) => {
				if (!cancelled) {
					setSelectedJobs(jobs.map((j) => mapRunJobItem(selectedRun.id, j)));
				}
			})
			.catch((err: unknown) => {
				if (!cancelled) {
					setJobsError(err instanceof Error ? err.message : "Could not load this run's jobs.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setJobsLoading(false);
				}
			});
		return () => {
			cancelled = true;
		};
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [selectedRun?.id]);

	const selectedGroup: LiveRunGroup | null = selectedRun && selectedJobs ? mapRunListItem(selectedRun, selectedJobs) : null;

	if (loading && items.length === 0) {
		return (
			<div className="live-jobs__body">
				<div className="live-jobs__empty">Loading run history…</div>
			</div>
		);
	}

	if (loadError && items.length === 0) {
		return (
			<div className="live-jobs__body">
				<div className="live-jobs__empty live-jobs__empty--error">{loadError}</div>
			</div>
		);
	}

	return (
		<>
			<div className="live-jobs__toolbar live-jobs__toolbar--history">
				<label className="live-jobs__history-filter">
					<input type="checkbox" checked={includeCompliance} onChange={(e) => setIncludeCompliance(e.target.checked)} />
					Include compliance runs (scan/remediate)
				</label>
				<span className="live-jobs__spacer" />
				<span className="live-jobs__count mono">{items.length} run{items.length === 1 ? "" : "s"}</span>
			</div>
			<div className="live-jobs__body">
				<div className="live-jobs__list" role="listbox" aria-label="Run history">
					{items.length === 0 && <div className="live-jobs__empty">No matching run history.</div>}
					{items.map((run) => {
						const selected = run.id === selectedRunId;
						return (
							<div
								key={run.id}
								role="option"
								aria-selected={selected}
								className={`live-jobs__run-row ${selected ? "is-selected" : ""}`}
								onClick={() => setSelectedRunId(run.id)}
							>
								<span className="live-jobs__run-type">{run.run_type}</span>
								<span className="mono live-jobs__run-id">{run.id}</span>
								<span className="live-jobs__history-state">{STATE_LABEL[run.state] ?? run.state}</span>
								<span className="live-jobs__run-progress mono">
									{run.job_count_completed}/{run.job_count} complete
									{run.job_count_failed > 0 ? ` · ${run.job_count_failed} failed` : ""}
								</span>
								<span className="live-jobs__run-initiator">{run.initiated_by ?? "—"}</span>
								<span className="live-jobs__history-completed mono">{run.completed_at ?? "—"}</span>
							</div>
						);
					})}
					{hasMore && (
						<button type="button" className="live-jobs__load-more" onClick={loadMore} disabled={loading}>
							{loading ? "Loading…" : "Load more"}
						</button>
					)}
				</div>

				<div className="live-jobs__detail-pane">
					{!selectedRun && <div className="live-jobs__empty">Select a run to see its history.</div>}
					{selectedRun && tombstone && (tombstone.outcome === "Completed" || tombstone.outcome === "AlreadyDeleted") && (
						<div className="live-jobs-detail" role="region" aria-label={`History deleted: ${selectedRun.id}`}>
							<div className="live-jobs__empty">
								This run's operational history was deleted by {tombstone.actor || "—"} at {tombstone.occurred_at || "—"} (prior state:{" "}
								{tombstone.prior_state || "—"}). The run itself is retained for referential integrity but its detail/log is no longer
								available here.
							</div>
						</div>
					)}
					{selectedRun && !tombstone && (
						<HistoryRunDetail run={selectedRun} group={selectedGroup} jobsLoading={jobsLoading} jobsError={jobsError} />
					)}
				</div>
			</div>
		</>
	);
}

interface HistoryRunDetailProps {
	run: RunListItem;
	group: LiveRunGroup | null;
	jobsLoading: boolean;
	jobsError: string | null;
}

function HistoryRunDetail({ run, group, jobsLoading, jobsError }: HistoryRunDetailProps) {
	if (jobsError) {
		return <div className="live-jobs__empty live-jobs__empty--error">{jobsError}</div>;
	}
	if (jobsLoading || !group) {
		return <div className="live-jobs__empty">Loading run detail…</div>;
	}
	if (group.jobs.length === 0) {
		return (
			<div className="live-jobs-detail" role="region" aria-label={`Run detail: ${run.id}`}>
				<div className="live-jobs__empty">This run has no jobs.</div>
			</div>
		);
	}

	return (
		<div className="live-jobs-detail live-jobs-detail--history">
			<dl className="live-jobs-detail__facts">
				<div>
					<dt>Run</dt>
					<dd className="mono">{run.id}</dd>
				</div>
				<div>
					<dt>Type</dt>
					<dd>{run.run_type}</dd>
				</div>
				<div>
					<dt>State</dt>
					<dd>{STATE_LABEL[run.state] ?? run.state}</dd>
				</div>
				<div>
					<dt>Initiated by</dt>
					<dd>{run.initiated_by ?? "—"}</dd>
				</div>
				<div>
					<dt>Completed</dt>
					<dd>{run.completed_at ?? "—"}</dd>
				</div>
			</dl>
			<p className="live-jobs__hint">Jobs in this run — select a job for its persisted log history.</p>
			<div className="live-jobs__list live-jobs__list--nested" role="listbox" aria-label={`Jobs in run ${run.id}`}>
				{group.jobs.map((job) => (
					<HistoryJobRow key={job.job_id} job={job} group={group} />
				))}
			</div>
		</div>
	);
}

function HistoryJobRow({ job, group }: { job: LiveJobRow; group: LiveRunGroup }) {
	const [expanded, setExpanded] = useState(false);
	const DetailRenderer = resolveJobDetailRenderer(job.job_type);

	return (
		<div className="live-jobs__job-row-container">
			<div role="option" aria-selected={expanded} className="live-jobs__job-row" onClick={() => setExpanded((v) => !v)}>
				<span className="live-jobs__job-target">{job.target_name ?? job.target_id ?? job.job_id}</span>
				<span className="live-jobs__state-badge">{job.state}</span>
				{job.finished_at && <span className="live-jobs__job-finished mono">{job.finished_at}</span>}
			</div>
			{expanded && <DetailRenderer job={job} group={group} />}
		</div>
	);
}
