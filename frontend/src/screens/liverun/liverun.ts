/**
 * Live Run data layer (issue #283, first slice of #26) — docs/api-contract.md
 * "Runs & jobs" (`GET /runs/{id}`, `GET /runs/{id}/jobs`) and "Event streams
 * (SSE)" (`job.state`, `job.log`, `run.progress`, `queue.state`).
 *
 * State is built two ways that must converge to the same board:
 *   1. REST seed (`fetchRun` + `fetchRunJobs`) — a snapshot for first paint.
 *   2. SSE events folded by `applyEvent`, a pure reducer, so the exact same
 *      function drives live updates AND Last-Event-ID replay after a reload
 *      (docs/api-contract.md: "commit order" seq guarantee makes replay
 *      exact) — see useLiveRun.ts and LiveRunScreen.test.tsx.
 *
 * Run controls (#285): `pauseRun`/`resumeRun`/`abortRun` map to
 * `/runs/{id}/pause|resume|abort` (Operator+, own runs; Admin any —
 * api-contract.md "Runs & jobs"); `cancelJob` maps to the per-job cancel
 * primitive #277 added to the job engine, exposed here at `DELETE
 * /jobs/{id}` alongside the existing `/jobs/{id}/artifacts/{kind}` path
 * prefix; `resumeBlockedRun` maps to `/runs/{id}/resume-blocked` (Admin
 * only — ADR-0008 halt behavior), body `{ credential_id }`. None of these
 * calls mutate local state directly: the server-side effect is reflected
 * back through `job.state`/`run.progress`/`queue.state` SSE events per the
 * "no polling" rule, same as every other animated value on this screen.
 */
import { apiDelete, apiGet, apiPost } from "../../lib/api";
import type { WaypointEvent } from "../../lib/events";

/** Target state machine (api-contract.md "State machines" — Scan job). */
export type JobState =
	| "queued"
	| "running"
	| "attesting"
	| "converting"
	| "uploaded"
	| "done"
	| "failed"
	| "auth-failed"
	| "blocked";

export const TERMINAL_JOB_STATES: ReadonlySet<JobState> = new Set(["uploaded", "done", "failed", "auth-failed"]);

/** `stage x 25%` per the prototype README ("Target state machine"). */
const STAGE_ORDER: JobState[] = ["queued", "running", "attesting", "converting", "uploaded"];

export function progressPercentForState(state: JobState): number {
	if (state === "uploaded" || state === "done") {
		return 100;
	}
	const idx = STAGE_ORDER.indexOf(state);
	return idx >= 0 ? idx * 25 : 0;
}

export interface RunJob {
	job_id: string;
	target: string;
	queue: string;
	priority: number;
	benchmark: string;
	state: JobState;
	progress_percent: number;
	pass: number | null;
	fail: number | null;
	na: number | null;
	note: string;
}

export interface QueueStatus {
	key: string;
	priority: number;
	name: string;
	benchmark: string;
	blocked: boolean;
	blocked_reason: string | null;
}

export interface RunHeader {
	id: string;
	site: string;
	target_count: number;
	initiated_by: string;
	credential_name: string;
	state: "pending" | "running" | "completed" | "completed_with_failures" | "aborted";
	/** Queue-level flag (api-contract.md "Run" state machine: "paused and
	 * blocked are queue-level flags, not run states") — distinct from `state`,
	 * which never itself becomes "paused". Drives the header Pause/Resume
	 * toggle. */
	paused: boolean;
	pass: number;
	fail: number;
	na: number;
	percent: number;
	completed_count: number;
	elapsed_seconds: number;
	blocked: boolean;
	queues: QueueStatus[];
}

export interface RunSnapshot {
	header: RunHeader;
	jobs: RunJob[];
	/** Highest `seq` reflected in this snapshot — the reconnect `Last-Event-ID`. */
	lastSeq: number;
}

export function fetchRun(runId: string): Promise<RunHeader> {
	return apiGet<RunHeader>(`/runs/${runId}`);
}

export function fetchRunJobs(runId: string): Promise<RunJob[]> {
	return apiGet<RunJob[]>(`/runs/${runId}/jobs`);
}

export interface RunActionResult {
	id: string;
	state: string;
}

export function pauseRun(runId: string): Promise<RunActionResult> {
	return apiPost<RunActionResult>(`/runs/${runId}/pause`);
}

export function resumeRun(runId: string): Promise<RunActionResult> {
	return apiPost<RunActionResult>(`/runs/${runId}/resume`);
}

export function abortRun(runId: string): Promise<RunActionResult> {
	return apiPost<RunActionResult>(`/runs/${runId}/abort`);
}

/** Per-job cancel (#277's cooperative `cancel_requested` primitive). */
export function cancelJob(jobId: string): Promise<void> {
	return apiDelete<void>(`/jobs/${jobId}`);
}

/** Admin credential-swap-resume for a halted run (ADR-0008 / #146). */
export function resumeBlockedRun(runId: string, credentialId: string): Promise<RunActionResult> {
	return apiPost<RunActionResult>(`/runs/${runId}/resume-blocked`, { credential_id: credentialId });
}

/** `job.state` event payload (api-contract.md event envelope example). */
interface JobStateData {
	target?: string;
	from?: string;
	to?: string;
}

/** `run.progress` event payload. */
interface RunProgressData {
	pass?: number;
	fail?: number;
	na?: number;
	percent?: number;
	completed_count?: number;
	elapsed_seconds?: number;
}

/** `queue.state` event payload. */
interface QueueStateData {
	key?: string;
	blocked?: boolean;
	reason?: string | null;
}

/** `job.log` event payload — only the note-worthy fields this screen renders. */
interface JobLogData {
	target?: string;
	line?: string;
	message?: string;
}

const KNOWN_JOB_STATES: ReadonlySet<string> = new Set([
	"queued",
	"running",
	"attesting",
	"converting",
	"uploaded",
	"done",
	"failed",
	"auth-failed",
	"blocked",
]);

function asJobState(value: string | undefined): JobState | undefined {
	return value !== undefined && KNOWN_JOB_STATES.has(value) ? (value as JobState) : undefined;
}

/**
 * Folds one SSE event into a `RunSnapshot`, returning a new snapshot (never
 * mutates the input) so it is safe to call from a React state updater. This
 * is the single source of truth for "what does an event do to the board" —
 * both the live subscription and Last-Event-ID replay call it, which is what
 * makes replay-after-reload produce the identical board (issue #283 AC2).
 *
 * Events outside this run (`run_id` set and different) or of a type this
 * screen doesn't render (`download.progress`, `system.notice`) are no-ops.
 * `job.state` for a `job_id` not yet in `jobs` is dropped rather than
 * synthesizing a row — unlike the download queue (#11), the Live Run board's
 * row set is authoritatively seeded from `GET /runs/{id}/jobs` before the
 * stream attaches, so an unknown job id here means the event predates the
 * seed and is stale, not missing data.
 */
export function applyEvent(snapshot: RunSnapshot, event: WaypointEvent): RunSnapshot {
	if (event.run_id && event.run_id !== snapshot.header.id) {
		return snapshot;
	}
	const lastSeq = Math.max(snapshot.lastSeq, event.seq);

	if (event.type === "job.state") {
		const data = event.data as JobStateData;
		const to = asJobState(data.to);
		if (!event.job_id || !to) {
			return { ...snapshot, lastSeq };
		}
		const idx = snapshot.jobs.findIndex((j) => j.job_id === event.job_id);
		if (idx === -1) {
			return { ...snapshot, lastSeq };
		}
		const jobs = [...snapshot.jobs];
		jobs[idx] = { ...jobs[idx], state: to, progress_percent: progressPercentForState(to) };
		return { ...snapshot, jobs, lastSeq };
	}

	if (event.type === "job.log") {
		const data = event.data as JobLogData;
		if (!event.job_id) {
			return { ...snapshot, lastSeq };
		}
		const idx = snapshot.jobs.findIndex((j) => j.job_id === event.job_id);
		if (idx === -1) {
			return { ...snapshot, lastSeq };
		}
		const note = data.line ?? data.message;
		if (!note) {
			return { ...snapshot, lastSeq };
		}
		const jobs = [...snapshot.jobs];
		jobs[idx] = { ...jobs[idx], note };
		return { ...snapshot, jobs, lastSeq };
	}

	if (event.type === "run.progress") {
		const data = event.data as RunProgressData;
		return {
			...snapshot,
			lastSeq,
			header: {
				...snapshot.header,
				pass: data.pass ?? snapshot.header.pass,
				fail: data.fail ?? snapshot.header.fail,
				na: data.na ?? snapshot.header.na,
				percent: data.percent ?? snapshot.header.percent,
				completed_count: data.completed_count ?? snapshot.header.completed_count,
				elapsed_seconds: data.elapsed_seconds ?? snapshot.header.elapsed_seconds,
			},
		};
	}

	if (event.type === "queue.state") {
		const data = event.data as QueueStateData;
		if (!data.key) {
			return { ...snapshot, lastSeq };
		}
		const queues = snapshot.header.queues.map((q) =>
			q.key === data.key ? { ...q, blocked: data.blocked ?? q.blocked, blocked_reason: data.reason ?? null } : q,
		);
		const blocked = queues.some((q) => q.blocked);
		return { ...snapshot, lastSeq, header: { ...snapshot.header, queues, blocked } };
	}

	return { ...snapshot, lastSeq };
}

export function formatElapsed(totalSeconds: number): string {
	const clamped = Math.max(0, Math.floor(totalSeconds));
	const minutes = Math.floor(clamped / 60);
	const seconds = clamped % 60;
	return `${minutes}m ${String(seconds).padStart(2, "0")}s`;
}
