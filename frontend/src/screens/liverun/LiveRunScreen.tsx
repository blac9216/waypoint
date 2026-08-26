/**
 * Live Run — docs/ui/prototype/README.md screen 1, the hero screen (issue
 * #283 read side + #285 write side). Renders a run's per-target board driven
 * entirely by SSE (useLiveRun.ts) — header counters, the layout switcher, all
 * three of the prototype's layout modes (priority queues default, state
 * board, log-first — #287), and — as of #285 — the run controls: Pause queue
 * / Abort run (run-scoped), per-job cancel, and the blocked banner's Admin
 * credential-swap-resume.
 *
 * This screen is the orchestrator only: event/subscription state lives in
 * useLiveRun.ts (board snapshot) and useRunLog.ts (log-first line history);
 * job-control state (pause/abort/cancel, role gating) lives in
 * useRunControls.ts; layout rendering lives in LiveRunLayouts.tsx; the
 * blocked-credential recovery banner lives in BlockedBanner.tsx. Every
 * control's server-side effect is reflected back through SSE (job.state /
 * run.progress / queue.state), never a local optimistic patch — consistent
 * with the "no polling" rule this screen was built to satisfy.
 *
 * Restored by issue #707 (epic #706) after issue #693 deleted this cluster,
 * on the premise that #591's generic Live Jobs renderer covered scan
 * monitoring (#704/#705: it didn't — no controls, no layouts, no board).
 * This is the console again, not a projection: it links back to the global
 * `/live-jobs` workspace and out to `/results?run=` for the compliance
 * Results screen, mirroring the linkage the deleted screen never had a
 * reason to need (Live Jobs didn't exist yet when this shipped originally).
 *
 * No-`runId` default state (issue #711, a regression against #714): making
 * this route a permanent top-level nav entry means it is now reachable with
 * no `?run=` at all, not just via a stale/removed deep link. `LiveRunPicker`
 * below covers that case with a small run-centric selector — reusing the
 * same `GET /runs` list `results/useRunList.ts` already fetches, filtered to
 * active (non-terminal) compliance runs via `useActiveRuns.ts` — rather than
 * the previous bare "No run selected." dead end.
 */
import { useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { Link } from "../../lib/router";
import { formatRunDuration, formatTimestamp, scopeSiteId } from "../results/results";
import { BlockedBanner } from "./BlockedBanner";
import { BoardLayout, Counter, LogFirstLayout, QueueLayout } from "./LiveRunLayouts";
import { formatElapsed, progressPercentForState } from "./liverun";
import { useActiveRuns } from "./useActiveRuns";
import { useLiveRun } from "./useLiveRun";
import { useRunControls } from "./useRunControls";
import { useRunIdFromQuery } from "./useRunIdFromQuery";
import { useRunLog } from "./useRunLog";
import "./LiveRunScreen.css";

type LayoutMode = "queues" | "board" | "log";

/** Route-connected entry point — reads `?run=` from the URL. The bare
 * `LiveRunScreen` below takes `runId` as an explicit prop so tests can drive
 * it directly without touching `window.location`. */
export function LiveRunRoute() {
	const runId = useRunIdFromQuery();
	return <LiveRunScreen runId={runId} />;
}

/** No-`runId` default state: lists active (non-terminal) compliance runs a
 * user can jump into, an empty-state explanation with a pointer to Start a
 * Scan when none exist, and a load-error fallback — never the bare "No run
 * selected." dead end. Selecting a row navigates to `/live-run?run=<id>`,
 * the same URL a deep link would use, so the rest of the screen's behavior
 * (and this component's own remount) is unaffected. */
function LiveRunPicker() {
	const { runs, loading, error } = useActiveRuns();

	return (
		<div className="live-run-screen">
			<div className="live-run__picker">
				<div className="live-run__picker-header">
					<h2 className="live-run__picker-title">Live Run</h2>
					<p className="live-run__picker-subtitle">Select an active compliance run to view its live console.</p>
				</div>

				{loading && <div className="live-run__empty">Loading active runs…</div>}

				{!loading && error && <div className="live-run__empty live-run__empty--error">{error}</div>}

				{!loading && !error && runs.length === 0 && (
					<div className="live-run__picker-empty">
						<p>No compliance run is currently active.</p>
						<p>
							<Link to="/scan/new">Start a Scan</Link> to begin one, or check <Link to="/live-jobs">Jobs</Link> for
							appliance-wide activity.
						</p>
					</div>
				)}

				{!loading && !error && runs.length > 0 && (
					<ul className="live-run__picker-list">
						{runs.map((run) => (
							<li key={run.id}>
								<Link to={`/live-run?run=${encodeURIComponent(run.id)}`} className="live-run__picker-row">
									<span className="mono live-run__picker-row-id">{run.id}</span>
									<span className="live-run__picker-row-meta">
										{scopeSiteId(run.scope) ?? "—"} · {run.job_count} targets · {run.run_type} · {run.state} ·{" "}
										{formatRunDuration(run.started_at, run.completed_at)} · started{" "}
										{run.started_at ? formatTimestamp(run.started_at) : "—"}
									</span>
								</Link>
							</li>
						))}
					</ul>
				)}
			</div>
		</div>
	);
}

export function LiveRunScreen({ runId }: { runId?: string }) {
	const { user } = useAuth();
	const { snapshot, loading, loadError, connectionState } = useLiveRun(runId);
	const [layout, setLayout] = useState<LayoutMode>("queues");
	const logLines = useRunLog(runId, layout === "log");
	const { runControlGate, jobControls, pausing, aborting, actionError, setActionError, handlePauseToggle, handleAbort } =
		useRunControls(runId, user?.role);

	if (!runId) {
		return <LiveRunPicker />;
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
						<div className="live-run__breadcrumb">
							<Link to="/live-jobs">← Jobs</Link>
						</div>
						<div className="live-run__title-row">
							<span className="live-run__pulse-dot" />
							<span className="mono live-run__run-id">{header.id}</span>
							<span className="live-run__mode-pill">SCAN · READ-ONLY</span>
							<span className="live-run__desc">
								{header.site} · {header.target_count} targets · initiated by {header.initiated_by} with{" "}
								<span className="mono">{header.credential_name}</span>
							</span>
							<Link to={`/results?run=${encodeURIComponent(header.id)}`} className="live-run__results-link">
								View in Compliance Scan Results →
							</Link>
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
						<button type="button" className={layout === "log" ? "is-active" : ""} onClick={() => setLayout("log")}>
							Log-first
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
				{layout === "queues" && <QueueLayout header={header} jobs={jobs} jobControls={jobControls} />}
				{layout === "board" && <BoardLayout jobs={jobs} />}
				{layout === "log" && <LogFirstLayout jobs={jobs} lines={logLines} />}
			</div>
		</div>
	);
}
