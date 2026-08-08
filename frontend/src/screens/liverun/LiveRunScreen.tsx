/**
 * Live Run — docs/ui/prototype/README.md screen 1, the hero screen's read
 * side (issue #283, first slice of #26). Renders a run's per-target board
 * driven entirely by SSE (useLiveRun.ts) — header counters, the layout
 * switcher, and two of the prototype's three layout modes: priority queues
 * (default) and the state board. The third (log-first) is proposed as a
 * follow-up in the PR body to keep this slice review-sized; see #283.
 *
 * Read-only by design: no pause/abort/resume wiring. The Pause queue / Abort
 * run / Change-credential-&-resume buttons render visible-but-disabled for
 * EVERY role (README "Roles & Permissions" treatment — visible, not hidden,
 * with a `title` explaining why), since the prototype's header chrome always
 * shows them but the wiring lands with #285. The block is role-independent, so
 * these controls are inert regardless of role until #285 wires them; a
 * privileged Operator/Admin must never see an enabled Abort that does nothing.
 */
import { useMemo, useState } from "react";
import { roleGateProps, type Role } from "../../lib/roles";
import { useAuth } from "../../lib/auth";
import { formatElapsed, progressPercentForState, type RunJob, type RunSnapshot } from "./liverun";
import { useLiveRun } from "./useLiveRun";
import { useRunIdFromQuery } from "./useRunIdFromQuery";
import "./LiveRunScreen.css";

type LayoutMode = "queues" | "board";

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

function stateColor(state: string): string {
	return STATE_COLOR[state] ?? "var(--txt3)";
}

const IN_FLIGHT_STATES = new Set(["running", "attesting", "converting"]);

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

function QueueLayout({ header, jobs }: { header: RunSnapshot["header"]; jobs: RunJob[] }) {
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
									<TargetRow key={job.job_id} job={job} />
								))}
							</tbody>
						</table>
					</div>
				);
			})}
		</div>
	);
}

function TargetRow({ job }: { job: RunJob }) {
	const color = stateColor(job.state);
	const inFlight = IN_FLIGHT_STATES.has(job.state);
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
			<td className="live-run__cell live-run__cell--pass mono">{job.pass ?? ""}</td>
			<td className="live-run__cell live-run__cell--fail mono">{job.fail ?? ""}</td>
			<td className="live-run__cell live-run__cell--na mono">{job.na ?? ""}</td>
			<td className="live-run__cell live-run__cell--note">{job.note}</td>
		</tr>
	);
}

function BoardLayout({ jobs }: { jobs: RunJob[] }) {
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

/** Route-connected entry point — reads `?run=` from the URL. The bare
 * `LiveRunScreen` below takes `runId` as an explicit prop so tests can drive
 * it directly without touching `window.location`. */
export function LiveRunRoute() {
	const runId = useRunIdFromQuery();
	return <LiveRunScreen runId={runId} />;
}

export function LiveRunScreen({ runId }: { runId?: string }) {
	const { user } = useAuth();
	const { snapshot, loading, loadError, connectionState } = useLiveRun(runId);
	const [layout, setLayout] = useState<LayoutMode>("queues");

	// Pause/Abort/Resume are not wired yet: the wiring lands with #285, which is
	// role-independent, so these controls must be inert for EVERY role — an
	// Operator/Admin must not see an enabled Abort that silently does nothing.
	// Keep the visible-but-disabled convention: privileged roles get the
	// "lands with #285" reason; roles below the gate keep the role reason.
	const notBuiltReason = "Run controls (pause / abort / resume) ship in a follow-up — see issue #285";
	function inertControlProps(required: Role) {
		if (!user) {
			return { disabled: true, style: { opacity: 0.42 }, title: notBuiltReason };
		}
		const gate = roleGateProps(user.role, required, notBuiltReason);
		// If the role gate would allow the action, override to disabled with the
		// not-yet-built reason so it stays genuinely inert regardless of role.
		if (!gate.disabled) {
			return { disabled: true, style: { opacity: 0.42 }, title: notBuiltReason };
		}
		return gate;
	}
	const controlGate = inertControlProps("Operator");

	if (!runId) {
		return (
			<div className="live-run-screen">
				<div className="live-run__empty">No run selected.</div>
			</div>
		);
	}

	if (loading && !snapshot) {
		return (
			<div className="live-run-screen">
				<div className="live-run__empty">Loading run…</div>
			</div>
		);
	}

	if (loadError && !snapshot) {
		return (
			<div className="live-run-screen">
				<div className="live-run__empty live-run__empty--error">{loadError}</div>
			</div>
		);
	}

	if (!snapshot) {
		return null;
	}

	const { header, jobs } = snapshot;
	const totalControls = jobs.reduce((sum, job) => sum + progressPercentForState(job.state), 0);
	const percent = header.percent || (jobs.length > 0 ? Math.round(totalControls / (jobs.length * 100) * 100) : 0);

	return (
		<div className="live-run-screen">
			<div className="live-run__header">
				<div className="live-run__header-top">
					<div className="live-run__title-block">
						<div className="live-run__title-row">
							<span className="live-run__pulse-dot" />
							<span className="mono live-run__run-id">{header.id}</span>
							<span className="live-run__mode-pill">SCAN · READ-ONLY</span>
							<span className="live-run__desc">
								{header.site} · {header.target_count} targets · initiated by {header.initiated_by} with{" "}
								<span className="mono">{header.credential_name}</span>
							</span>
						</div>
						<div className="live-run__progress-row">
							<div className="live-run__progress-bar">
								<div className="live-run__progress-fill" style={{ width: `${Math.min(100, Math.max(0, percent))}%` }} />
							</div>
							<div className="mono live-run__progress-readout">
								{header.completed_count}/{header.target_count} complete · {percent}% · elapsed{" "}
								{formatElapsed(header.elapsed_seconds)}
							</div>
						</div>
					</div>
					<div className="live-run__counters">
						<Counter label="PASS" value={header.pass} color="var(--ok)" />
						<Counter label="FAIL" value={header.fail} color="var(--bad)" />
						<Counter label="N/A" value={header.na} color="var(--na)" />
						<div className="live-run__counter-divider" />
						<div className="live-run__controls">
							<button type="button" {...controlGate}>
								Pause queue
							</button>
							<button type="button" className="live-run__abort" {...controlGate}>
								Abort run
							</button>
						</div>
					</div>
				</div>

				<div className="live-run__layout-row">
					<span className="live-run__layout-label">LAYOUT</span>
					<div className="live-run__layout-switcher">
						<button
							type="button"
							className={layout === "queues" ? "is-active" : ""}
							onClick={() => setLayout("queues")}
						>
							Priority queues
						</button>
						<button type="button" className={layout === "board" ? "is-active" : ""} onClick={() => setLayout("board")}>
							State board
						</button>
					</div>
					<span className="live-run__spacer" />
					{connectionState !== "open" && (
						<span className="live-run__connection-note mono">stream {connectionState}…</span>
					)}
				</div>

				{header.blocked && (
					<div className="live-run__blocked-banner">
						<span className="live-run__blocked-dot" />
						<span>
							Queue halted — {header.queues.find((q) => q.blocked)?.blocked_reason ?? "credential failure"}
						</span>
						<button type="button" {...inertControlProps("Admin")}>
							Change credential &amp; resume
						</button>
					</div>
				)}
			</div>

			<div className="live-run__body">
				{layout === "queues" ? <QueueLayout header={header} jobs={jobs} /> : <BoardLayout jobs={jobs} />}
			</div>
		</div>
	);
}

function Counter({ label, value, color }: { label: string; value: number; color: string }) {
	return (
		<div className="live-run__counter">
			<div className="mono live-run__counter-value" style={{ color }}>
				{value}
			</div>
			<div className="live-run__counter-label">{label}</div>
		</div>
	);
}
