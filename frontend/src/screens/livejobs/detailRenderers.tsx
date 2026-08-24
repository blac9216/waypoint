/**
 * Job-detail generic fallback (ADR-0019 decision 2: "A renderer selected
 * from the authoritative run/job type presents relevant progress and
 * controls... Unknown types use a safe generic lifecycle/log renderer.").
 *
 * Issue #590 built the workspace shell and this generic fallback. Issue #591
 * added type-specific renderers (scan stage detail, download progress,
 * discovery results, etc.) — the registry mapping `job_type` to a renderer
 * component lives in `detailRenderers.registry.ts` (split out per issue #692
 * so this file exports only the `GenericJobDetail` component, keeping
 * Fast-Refresh-friendly component-only exports). This module stays the
 * fallback every unmapped `job_type` — including any future addition to the
 * closed `job_type` set — resolves to.
 */
import type { ReactElement } from "react";
import type { LiveJobRow, LiveRunGroup } from "./livejobs";
import { isTerminalJobState, waitReasonForJob } from "./livejobs";
import type { WaypointEvent } from "../../lib/events";
import { useJobHistory } from "./useJobHistory";

export interface JobDetailProps {
	job: LiveJobRow;
	group: LiveRunGroup;
}

/** A registered detail renderer, keyed by `job_type` in
 * `detailRenderers.registry.ts`'s `JOB_DETAIL_RENDERERS` (e.g. `"scan"`,
 * `"download"`, `"discover"`, `"credential-test"`). */
export type JobDetailRenderer = (props: JobDetailProps) => ReactElement;

function formatEventLine(event: WaypointEvent): string {
	if (event.type === "job.log") {
		const data = event.data as { line?: string; message?: string };
		return `${event.ts} ${data.line ?? data.message ?? ""}`;
	}
	if (event.type === "job.state") {
		const data = event.data as { from?: string; to?: string };
		return `${event.ts} state: ${data.from ?? "?"} -> ${data.to ?? "?"}`;
	}
	return `${event.ts} ${event.type}`;
}

/**
 * The generic fallback (ADR-0019 decision 2's "safe generic lifecycle/log
 * renderer"). Renders identity, state, timing, target/context, wait reason,
 * and a log — live tail for an active job, persisted history (issue #581
 * client, `useJobHistory.ts`) for a terminal one. This is what every
 * unregistered `job_type` gets, and remains available as the explicit
 * fallback even after #591 registers real renderers for the types it knows.
 */
export function GenericJobDetail({ job, group }: JobDetailProps): ReactElement {
	const terminal = isTerminalJobState(job.state);
	const { events, loading, error } = useJobHistory(group.run_id, job.job_id, terminal);
	const reason = waitReasonForJob(job, group);

	return (
		<div className="live-jobs-detail" role="region" aria-label={`Job detail: ${job.target_name ?? job.job_id}`}>
			<dl className="live-jobs-detail__facts">
				<div>
					<dt>Job</dt>
					<dd className="mono">{job.job_id}</dd>
				</div>
				<div>
					<dt>Type</dt>
					<dd>{job.job_type}</dd>
				</div>
				<div>
					<dt>State</dt>
					<dd>{job.state}</dd>
				</div>
				{job.stage && (
					<div>
						<dt>Stage</dt>
						<dd>{job.stage}</dd>
					</div>
				)}
				<div>
					<dt>Target</dt>
					<dd>{job.target_name ?? job.target_id ?? "—"}</dd>
				</div>
				<div>
					<dt>Attempts</dt>
					<dd>{job.attempt_count}</dd>
				</div>
				<div>
					<dt>Created</dt>
					<dd>{job.created_at}</dd>
				</div>
				{job.started_at && (
					<div>
						<dt>Started</dt>
						<dd>{job.started_at}</dd>
					</div>
				)}
				{job.finished_at && (
					<div>
						<dt>Finished</dt>
						<dd>{job.finished_at}</dd>
					</div>
				)}
				{reason && (
					<div>
						<dt>Wait reason</dt>
						<dd>{reason}</dd>
					</div>
				)}
			</dl>

			<div className="live-jobs-detail__log" role="log" aria-live={terminal ? "off" : "polite"} aria-label="Job log">
				{terminal ? (
					<>
						{loading && <div className="live-jobs-detail__log-empty">Loading history…</div>}
						{error && <div className="live-jobs-detail__log-empty live-jobs-detail__log-empty--error">{error}</div>}
						{!loading && !error && events.length === 0 && (
							<div className="live-jobs-detail__log-empty">No recorded history for this job.</div>
						)}
						{events.map((event) => (
							<div className="live-jobs-detail__log-line" key={event.seq}>
								{formatEventLine(event)}
							</div>
						))}
					</>
				) : (
					<>
						{job.logLines.length === 0 && <div className="live-jobs-detail__log-empty">No log lines yet.</div>}
						{job.logLines.map((line, idx) => (
							<div className="live-jobs-detail__log-line" key={`${job.job_id}-${idx}`}>
								{line}
							</div>
						))}
					</>
				)}
			</div>
		</div>
	);
}
