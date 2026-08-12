/**
 * Live Run board presentation — the three layout modes (priority queues
 * default, state board, log-first — #287) plus the shared per-target row.
 * Pure rendering of a `RunSnapshot`/`RunJob[]`; all board mutation lives in
 * liverun.ts's `applyEvent` reducer, and all control wiring (cancel gating,
 * handlers) is passed in via `JobControlProps` from useRunControls.ts.
 */
import { useEffect, useMemo, useRef } from "react";
import type { RunJob, RunSnapshot } from "./liverun";
import type { JobControlProps } from "./useRunControls";
import type { LogFirstLine } from "./useRunLog";

const STATE_COLOR: Record<string, string> = {
	queued: "var(--txt3)",
	blocked: "var(--txt3)",
	running: "var(--acc)",
	attesting: "var(--acc)",
	converting: "var(--acc)",
	uploaded: "var(--ok)",
	done: "var(--ok)",
	failed: "var(--bad)",
	"auth-failed": "var(--bad)",
};

export function stateColor(state: string): string {
	return STATE_COLOR[state] ?? "var(--txt3)";
}

const IN_FLIGHT_STATES = new Set(["running", "attesting", "converting"]);
/** #277's cooperative per-job cancel is honored for queued and in-flight jobs
 * alike (queued cancels immediately; in-flight sets `cancel_requested` and the
 * heartbeat loop honors it cooperatively) — a terminal job has nothing left
 * to cancel. */
const CANCELLABLE_STATES = new Set(["queued", "running", "attesting", "converting"]);

interface BoardColumn {
	label: string;
	color: string;
	jobs: RunJob[];
}

function buildBoardColumns(jobs: RunJob[]): BoardColumn[] {
	const columns: BoardColumn[] = [
		{ label: "QUEUED", color: "var(--txt3)", jobs: [] },
		{ label: "RUNNING", color: "var(--acc)", jobs: [] },
		{ label: "ATTESTING", color: "var(--acc)", jobs: [] },
		{ label: "CONVERTING", color: "var(--acc)", jobs: [] },
		{ label: "UPLOADED", color: "var(--ok)", jobs: [] },
		{ label: "FAILED / BLOCKED", color: "var(--bad)", jobs: [] },
	];
	for (const job of jobs) {
		switch (job.state) {
			case "queued":
				columns[0].jobs.push(job);
				break;
			case "running":
				columns[1].jobs.push(job);
				break;
			case "attesting":
				columns[2].jobs.push(job);
				break;
			case "converting":
				columns[3].jobs.push(job);
				break;
			case "uploaded":
			case "done":
				columns[4].jobs.push(job);
				break;
			default:
				// failed, auth-failed, blocked
				columns[5].jobs.push(job);
				break;
		}
	}
	return columns;
}

export function QueueLayout({
	header,
	jobs,
	jobControls,
}: {
	header: RunSnapshot["header"];
	jobs: RunJob[];
	jobControls: JobControlProps;
}) {
	const byQueue = useMemo(() => {
		const groups = new Map<string, RunJob[]>();
		for (const job of jobs) {
			const list = groups.get(job.queue) ?? [];
			list.push(job);
			groups.set(job.queue, list);
		}
		return groups;
	}, [jobs]);

	const queues = [...header.queues].sort((a, b) => a.priority - b.priority);

	return (
		<div className="live-run__queues">
			{queues.map((queue) => {
				const rows = byQueue.get(queue.key) ?? [];
				const done = rows.filter((r) => r.state === "uploaded" || r.state === "done").length;
				const statusText = queue.blocked
					? `HALTED — ${queue.blocked_reason ?? "credential failure"}`
					: done === rows.length && rows.length > 0
						? `complete · ${rows.length} targets`
						: `${done} / ${rows.length} complete`;
				return (
					<div key={queue.key} className="live-run__queue">
						<div className="live-run__queue-header">
							<span className="live-run__queue-pri mono">P{queue.priority}</span>
							<span className="live-run__queue-name">{queue.name}</span>
							<span className="live-run__queue-bench mono">{queue.benchmark}</span>
							<span className="live-run__spacer" />
							<span className="mono" style={{ color: queue.blocked ? "var(--bad)" : "var(--txt3)" }}>
								{statusText}
							</span>
						</div>
						<table className="live-run__table">
							<tbody>
								{rows.map((job) => (
									<TargetRow key={job.job_id} job={job} jobControls={jobControls} />
								))}
							</tbody>
						</table>
					</div>
				);
			})}
		</div>
	);
}

function TargetRow({ job, jobControls }: { job: RunJob; jobControls: JobControlProps }) {
	const color = stateColor(job.state);
	const inFlight = IN_FLIGHT_STATES.has(job.state);
	const cancellable = CANCELLABLE_STATES.has(job.state);
	return (
		<tr className="live-run__row">
			<td className="live-run__cell live-run__cell--target">
				<span className={`live-run__dot ${inFlight ? "live-run__dot--pulse" : ""}`} style={{ background: color }} />
				<span className="mono live-run__target-name">{job.target}</span>
			</td>
			<td className="live-run__cell live-run__cell--state">
				<span className="live-run__state-pill mono" style={{ color, borderColor: color }}>
					{job.state}
				</span>
			</td>
			<td className="live-run__cell live-run__cell--progress">
				<div className="live-run__bar">
					<div className="live-run__bar-fill" style={{ width: `${job.progress_percent}%`, background: color }} />
				</div>
			</td>
			<td className="live-run__cell live-run__cell--controls">
				{cancellable && (
					<button
						type="button"
						className="live-run__job-cancel"
						{...jobControls.cancelGate}
						onClick={() => jobControls.onCancel(job)}
						aria-label={`Cancel ${job.target}`}
						title={jobControls.cancelGate.title ?? `Cancel ${job.target}`}
					>
						✕
					</button>
				)}
			</td>
			<td className="live-run__cell live-run__cell--pass mono">{job.pass ?? ""}</td>
			<td className="live-run__cell live-run__cell--fail mono">{job.fail ?? ""}</td>
			<td className="live-run__cell live-run__cell--na mono">{job.na ?? ""}</td>
			<td className="live-run__cell live-run__cell--note">{job.note}</td>
		</tr>
	);
}

export function BoardLayout({ jobs }: { jobs: RunJob[] }) {
	const columns = useMemo(() => buildBoardColumns(jobs), [jobs]);
	return (
		<div className="live-run__board">
			{columns.map((col) => (
				<div key={col.label} className="live-run__board-col">
					<div className="live-run__board-col-header">
						<span className="live-run__dot" style={{ background: col.color }} />
						<span className="live-run__board-col-label">{col.label}</span>
						<span className="live-run__spacer" />
						<span className="mono" style={{ color: col.color }}>
							{col.jobs.length}
						</span>
					</div>
					<div className="live-run__board-col-body">
						{col.jobs.map((job) => (
							<div key={job.job_id} className="live-run__board-chip" style={{ borderLeftColor: stateColor(job.state) }}>
								<div className="mono live-run__board-chip-name">{job.target}</div>
								<div className="mono live-run__board-chip-meta">
									{job.queue} · {job.note}
								</div>
							</div>
						))}
					</div>
				</div>
			))}
		</div>
	);
}

/**
 * Log-first layout (README screen 1: "Narrow target list (380px, own
 * scroll) beside a full-height log pane"). The target list reuses the same
 * per-target rows the other two layouts render (just a flat list, sorted by
 * queue priority then target — the queue grouping itself belongs to the
 * Priority Queues layout); the log pane is fed by `useRunLog` and follows
 * the tail the same way the global Job Log Drawer does (`scrollTop =
 * scrollHeight`, never `scrollIntoView`).
 */
export function LogFirstLayout({ jobs, lines }: { jobs: RunJob[]; lines: LogFirstLine[] }) {
	const sorted = useMemo(() => [...jobs].sort((a, b) => a.priority - b.priority || a.target.localeCompare(b.target)), [jobs]);
	const logRef = useRef<HTMLDivElement>(null);

	useEffect(() => {
		const el = logRef.current;
		if (el) {
			el.scrollTop = el.scrollHeight;
		}
	}, [lines]);

	return (
		<div className="live-run__log-first">
			<div className="live-run__log-first-targets">
				{sorted.map((job) => {
					const color = stateColor(job.state);
					const inFlight = IN_FLIGHT_STATES.has(job.state);
					return (
						<div key={job.job_id} className="live-run__log-first-target">
							<span className={`live-run__dot ${inFlight ? "live-run__dot--pulse" : ""}`} style={{ background: color }} />
							<span className="mono live-run__log-first-target-name">{job.target}</span>
							<span className="live-run__spacer" />
							<span className="live-run__state-pill mono" style={{ color, borderColor: color }}>
								{job.state}
							</span>
						</div>
					);
				})}
			</div>
			<div className="live-run__log-first-log" ref={logRef} role="log" aria-live="polite">
				{lines.length === 0 && <div className="live-run__log-first-empty">No log lines yet.</div>}
				{lines.map((line) => (
					<div className="live-run__log-first-line" key={line.seq}>
						<span className="mono live-run__log-first-ts">{line.ts}</span>
						{line.target && <span className="mono live-run__log-first-target-tag">{line.target}</span>}
						<span className="live-run__log-first-msg">{line.message}</span>
					</div>
				))}
			</div>
		</div>
	);
}

export function Counter({ label, value, color }: { label: string; value: number; color: string }) {
	return (
		<div className="live-run__counter">
			<div className="mono live-run__counter-value" style={{ color }}>
				{value}
			</div>
			<div className="live-run__counter-label">{label}</div>
		</div>
	);
}
