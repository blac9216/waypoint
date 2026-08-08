/**
 * Live Run — docs/ui/prototype/README.md screen 1, the hero screen (issue
 * #283 read side + #285 write side). Renders a run's per-target board driven
 * entirely by SSE (useLiveRun.ts) — header counters, the layout switcher, two
 * of the prototype's three layout modes (priority queues default, state
 * board), and — as of #285 — the run controls: Pause queue / Abort run
 * (run-scoped), per-job cancel, and the blocked banner's Admin
 * credential-swap-resume. The third layout (log-first) is proposed as a
 * follow-up in the PR body to keep slices review-sized; see #283.
 *
 * Controls follow the README "Roles & Permissions" visible-but-disabled
 * convention: an insufficient role still sees the button, disabled, with a
 * `title` naming the required role (`roleGateProps`) — never silently
 * hidden. Confirmation before a destructive action (`window.confirm`, same
 * pattern as CredentialsTab/SiteTargetsPanel deletes) guards Abort and
 * per-job Cancel; Pause/Resume act immediately since they're reversible.
 * Every control's server-side effect is reflected back through SSE
 * (job.state / run.progress / queue.state), never a local optimistic patch —
 * consistent with the "no polling" rule this screen was built to satisfy.
 */
import { useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { roleGateProps, type Role } from "../../lib/roles";
import { useAuth } from "../../lib/auth";
import { fetchCredentials, type Credential } from "../configuration/credentials";
import {
	abortRun,
	cancelJob,
	formatElapsed,
	pauseRun,
	progressPercentForState,
	resumeBlockedRun,
	resumeRun,
	type RunJob,
	type RunSnapshot,
} from "./liverun";
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

interface JobControlProps {
	cancelGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	onCancel: (job: RunJob) => void;
}

function QueueLayout({
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
	const [actionError, setActionError] = useState<string | null>(null);
	const [pausing, setPausing] = useState(false);
	const [aborting, setAborting] = useState(false);
	const [cancellingJobId, setCancellingJobId] = useState<string | null>(null);

	// api-contract.md "Runs & jobs": pause/resume/abort are Operator+ (own
	// runs), Admin any; the run-ownership check itself is server-side (this is
	// presentation only, per roles.ts). Per-job cancel rides the same floor —
	// it is a finer-grained abort, not a separate capability.
	const runControlGate = user ? roleGateProps(user.role, "Operator") : { disabled: true, style: { opacity: 0.42 } };

	async function handlePauseToggle(paused: boolean) {
		if (!runId) {
			return;
		}
		setActionError(null);
		setPausing(true);
		try {
			await (paused ? resumeRun(runId) : pauseRun(runId));
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : paused ? "Could not resume the run." : "Could not pause the run.");
		} finally {
			setPausing(false);
		}
	}

	async function handleAbort() {
		if (!runId) {
			return;
		}
		if (!window.confirm("Abort this run? In-flight targets stop cooperatively; queued targets are cancelled. This cannot be undone.")) {
			return;
		}
		setActionError(null);
		setAborting(true);
		try {
			await abortRun(runId);
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not abort the run.");
		} finally {
			setAborting(false);
		}
	}

	async function handleCancelJob(job: RunJob) {
		if (!window.confirm(`Cancel "${job.target}"? This cannot be undone.`)) {
			return;
		}
		setActionError(null);
		setCancellingJobId(job.job_id);
		try {
			await cancelJob(job.job_id);
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not cancel the job.");
		} finally {
			setCancellingJobId(null);
		}
	}

	const jobControls: JobControlProps = {
		cancelGate: runControlGate.disabled
			? runControlGate
			: cancellingJobId
				? { disabled: true, style: { opacity: 0.42 }, title: "Cancelling…" }
				: { disabled: false },
		onCancel: (job) => {
			void handleCancelJob(job);
		},
	};

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
							<button type="button" {...runControlGate} onClick={() => handlePauseToggle(header.paused)}>
								{pausing ? (header.paused ? "Resuming…" : "Pausing…") : header.paused ? "Resume queue" : "Pause queue"}
							</button>
							<button type="button" className="live-run__abort" {...runControlGate} onClick={handleAbort}>
								{aborting ? "Aborting…" : "Abort run"}
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

				{actionError && <div className="live-run__action-error">{actionError}</div>}

				{header.blocked && runId && (
					<BlockedBanner
						runId={runId}
						reason={header.queues.find((q) => q.blocked)?.blocked_reason ?? "credential failure"}
						role={user?.role}
						onError={setActionError}
					/>
				)}
			</div>

			<div className="live-run__body">
				{layout === "queues" ? (
					<QueueLayout header={header} jobs={jobs} jobControls={jobControls} />
				) : (
					<BoardLayout jobs={jobs} />
				)}
			</div>
		</div>
	);
}

/**
 * Blocked banner (README screen 1: "explains the halt and offers 'Change
 * credential & resume' (Admin only)"). The #146 unblock flow: Admin picks a
 * replacement credential from the stored (service/shared) list and calls
 * `POST /runs/{id}/resume-blocked`. Non-Admin roles still see the button —
 * visible-but-disabled with the role reason — never hidden. The credential
 * picker itself only renders for Admin, since a disabled `<select>` with no
 * chance of ever being used would just be UI noise for every other role.
 */
function BlockedBanner({
	runId,
	reason,
	role,
	onError,
}: {
	runId: string;
	reason: string;
	role: Role | undefined;
	onError: (message: string | null) => void;
}) {
	const isAdmin = role === "Admin";
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [selectedId, setSelectedId] = useState("");
	const [resuming, setResuming] = useState(false);
	const [loadedCredentials, setLoadedCredentials] = useState(false);

	useEffect(() => {
		if (!isAdmin || loadedCredentials) {
			return;
		}
		setLoadedCredentials(true);
		fetchCredentials()
			.then((list) => setCredentials(list))
			.catch(() => {
				// Non-fatal: the picker just stays empty and resume stays disabled
				// until a credential id is chosen; the banner itself must still render.
			});
	}, [isAdmin, loadedCredentials]);

	async function handleResume() {
		if (!selectedId) {
			return;
		}
		onError(null);
		setResuming(true);
		try {
			await resumeBlockedRun(runId, selectedId);
		} catch (err) {
			onError(err instanceof ApiError ? err.message : "Could not resume the run.");
		} finally {
			setResuming(false);
		}
	}

	const resumeGate = isAdmin
		? { disabled: !selectedId || resuming }
		: roleGateProps(role ?? "Viewer", "Admin", "Requires Admin — credential swap & resume is not available to your role");

	return (
		<div className="live-run__blocked-banner">
			<span className="live-run__blocked-dot" />
			<span>Queue halted — {reason}</span>
			{isAdmin && (
				<select
					className="live-run__credential-select"
					value={selectedId}
					onChange={(e) => setSelectedId(e.target.value)}
					aria-label="Replacement credential"
				>
					<option value="">Select replacement credential…</option>
					{credentials.map((c) => (
						<option key={c.id} value={c.id}>
							{c.name}
						</option>
					))}
				</select>
			)}
			<button type="button" {...resumeGate} onClick={handleResume}>
				{resuming ? "Resuming…" : "Change credential & resume"}
			</button>
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
