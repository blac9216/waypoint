import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from "react";
import { useAuth } from "../../lib/auth";
import { API_BASE } from "../../lib/api";
import { connectEventStream, type WaypointEvent } from "../../lib/events";
import { DrawerChevronIcon } from "./icons";
import "./JobLogDrawer.css";

const MIN_HEIGHT_PX = 96;
const MAX_HEIGHT_VH = 0.4;
const DEFAULT_HEIGHT_PX = 196;
/** Soft cap on buffered log lines — an implementation memory bound, distinct
 * from the Live Run screen's own spec'd 260-line cap (README "Live run
 * simulation"), which belongs to that screen's own log pane. */
const MAX_BUFFERED_LINES = 1000;

interface LogLine {
	seq: number;
	ts: string;
	level: "INFO" | "OK" | "WARN" | "ERROR";
	message: string;
}

interface ActiveJob {
	jobId: string;
	target: string;
	state: string;
}

const LEVEL_FROM_EVENT: Record<string, LogLine["level"]> = {
	info: "INFO",
	ok: "OK",
	warn: "WARN",
	error: "ERROR",
};

const TERMINAL_STATES = new Set(["uploaded", "done", "failed", "auth-failed"]);

/** Clamp a candidate drawer height to [96px, 40% of the current viewport
 * height] — README "Layout Rules Learned the Hard Way" #6 / "Global job log
 * drawer": "Drag-resize clamps to [96px, 40% of window height]. Without
 * those caps the drawer starves the screen above it." Exported so the clamp
 * logic itself is unit-testable without mounting the drawer. */
export function clampDrawerHeight(candidatePx: number, viewportHeightPx: number): number {
	const max = Math.round(viewportHeightPx * MAX_HEIGHT_VH);
	return Math.max(MIN_HEIGHT_PX, Math.min(max, candidatePx));
}

export function JobLogDrawer() {
	const { token, status } = useAuth();
	const [open, setOpen] = useState(false);
	const [logH, setLogH] = useState(DEFAULT_HEIGHT_PX);
	const [follow, setFollow] = useState(true);
	const [logs, setLogs] = useState<LogLine[]>([]);
	const [activeJobs, setActiveJobs] = useState<Map<string, ActiveJob>>(new Map());

	const logRef = useRef<HTMLDivElement>(null);
	const resizeState = useRef<{ startY: number; startH: number } | null>(null);

	// --- Global SSE subscription (docs/api-contract.md "Event streams (SSE)") ---
	useEffect(() => {
		if (status !== "signed-in" || !token) {
			return;
		}
		const close = connectEventStream(`${API_BASE}/events`, {
			getToken: () => token,
			onEvent: (event: WaypointEvent) => {
				if (event.type === "job.log") {
					const data = event.data as { level?: string; line?: string; message?: string };
					const level = LEVEL_FROM_EVENT[(data.level ?? "info").toLowerCase()] ?? "INFO";
					setLogs((prev) => {
						const next = [...prev, { seq: event.seq, ts: event.ts, level, message: data.line ?? data.message ?? "" }];
						return next.length > MAX_BUFFERED_LINES ? next.slice(next.length - MAX_BUFFERED_LINES) : next;
					});
				} else if (event.type === "job.state" && event.job_id) {
					const data = event.data as { target?: string; to?: string };
					const to = data.to ?? "";
					setActiveJobs((prev) => {
						const next = new Map(prev);
						if (TERMINAL_STATES.has(to)) {
							next.delete(event.job_id!);
						} else {
							next.set(event.job_id!, { jobId: event.job_id!, target: data.target ?? event.job_id!, state: to });
						}
						return next;
					});
				} else if (event.type === "system.notice") {
					const data = event.data as { level?: string; message?: string };
					const level = LEVEL_FROM_EVENT[(data.level ?? "info").toLowerCase()] ?? "INFO";
					setLogs((prev) => {
						const next = [...prev, { seq: event.seq, ts: event.ts, level, message: data.message ?? "" }];
						return next.length > MAX_BUFFERED_LINES ? next.slice(next.length - MAX_BUFFERED_LINES) : next;
					});
				}
			},
		});
		return close;
	}, [status, token]);

	// --- Follow-tail: "set scrollTop = scrollHeight; never scrollIntoView" ---
	useEffect(() => {
		if (!follow || !open) {
			return;
		}
		const el = logRef.current;
		if (el) {
			el.scrollTop = el.scrollHeight;
		}
	}, [logs, follow, open]);

	// --- Drag-resize (README "Drawer resize") ---
	const onHandleMouseDown = useCallback(
		(event: ReactMouseEvent) => {
			event.preventDefault();
			resizeState.current = { startY: event.clientY, startH: logH };

			const onMouseMove = (moveEvent: MouseEvent) => {
				if (!resizeState.current) {
					return;
				}
				const delta = resizeState.current.startY - moveEvent.clientY;
				const candidate = resizeState.current.startH + delta;
				setLogH(clampDrawerHeight(candidate, window.innerHeight));
			};
			const onMouseUp = () => {
				resizeState.current = null;
				window.removeEventListener("mousemove", onMouseMove);
				window.removeEventListener("mouseup", onMouseUp);
			};
			window.addEventListener("mousemove", onMouseMove);
			window.addEventListener("mouseup", onMouseUp);
		},
		[logH],
	);

	// Re-clamp on viewport resize so a shrunk window can't leave the drawer
	// taller than 40vh (README rule #6).
	useEffect(() => {
		const onResize = () => setLogH((h) => clampDrawerHeight(h, window.innerHeight));
		window.addEventListener("resize", onResize);
		return () => window.removeEventListener("resize", onResize);
	}, []);

	const jobsSummary = useMemo(() => Array.from(activeJobs.values()).map((j) => j.target).join(", "), [activeJobs]);

	const downloadLog = useCallback(() => {
		const text = logs.map((l) => `${l.ts} ${l.level.padEnd(5)} ${l.message}`).join("\n");
		const blob = new Blob([text], { type: "text/plain" });
		const url = URL.createObjectURL(blob);
		const a = document.createElement("a");
		a.href = url;
		a.download = `waypoint-job-log-${new Date().toISOString().replace(/[:.]/g, "-")}.txt`;
		a.click();
		URL.revokeObjectURL(url);
	}, [logs]);

	if (status !== "signed-in") {
		return null;
	}

	return (
		<div className="job-log-drawer">
			{open && (
				<div
					className="job-log-drawer__handle"
					onMouseDown={onHandleMouseDown}
					title="Drag to resize the log pane"
				>
					<div className="job-log-drawer__grip" />
				</div>
			)}

			{/*
			  A real <button> can't contain the nested Follow-tail/Download
			  buttons (invalid HTML, breaks their accessibility tree), so this
			  outer div is a plain click-anywhere-on-the-bar pointer affordance,
			  not an accessibility-tree control (no role="button" — that would
			  fold the nested buttons' names into this container's computed
			  name and announce them twice; see issue #71). Keyboard/SR users
			  get one dedicated toggle instead: the real <button> below, which
			  carries aria-expanded so the open/collapsed state is exposed.
			*/}
			<div className="job-log-drawer__bar" onClick={() => setOpen((o) => !o)}>
				<button
					type="button"
					className="job-log-drawer__toggle"
					aria-expanded={open}
					aria-controls="job-log-drawer-body"
					onClick={(event) => {
						// The wrapping div's onClick already toggles; stop
						// propagation so this doesn't double-toggle.
						event.stopPropagation();
						setOpen((o) => !o);
					}}
				>
					<DrawerChevronIcon style={{ transform: `rotate(${open ? 0 : 180}deg)` }} />
					<span className="job-log-drawer__title">JOB LOG</span>
				</button>
				<span className="job-log-drawer__active">
					<span className="job-log-drawer__dot" />
					<span className="mono">{activeJobs.size}</span>
				</span>
				<span className="job-log-drawer__summary mono">{jobsSummary}</span>
				<span className="job-log-drawer__count mono">{logs.length} lines</span>
				{open && (
					<button
						type="button"
						className={`job-log-drawer__follow ${follow ? "is-on" : ""}`}
						onClick={(event) => {
							event.stopPropagation();
							setFollow((f) => !f);
						}}
					>
						Follow tail
					</button>
				)}
				{open && (
					<button
						type="button"
						className="job-log-drawer__download"
						onClick={(event) => {
							event.stopPropagation();
							downloadLog();
						}}
					>
						Download
					</button>
				)}
			</div>

			{open && (
				<div
					id="job-log-drawer-body"
					className="job-log-drawer__body"
					style={{ height: logH }}
					ref={logRef}
					role="log"
					aria-live="polite"
				>
					{logs.length === 0 && <div className="job-log-drawer__empty">No log lines yet.</div>}
					{logs.map((line) => (
						<div className="job-log-drawer__line" key={line.seq}>
							<span className="job-log-drawer__ts">{line.ts}</span>
							<span className={`job-log-drawer__level job-log-drawer__level--${line.level}`}>{line.level}</span>
							<span className="job-log-drawer__msg">{line.message}</span>
						</div>
					))}
				</div>
			)}
		</div>
	);
}
