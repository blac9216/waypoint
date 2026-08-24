/**
 * Live Run data layer (issue #283, first slice of #26) — docs/api-contract.md
 * "Runs & jobs" (`GET /runs/{id}`, `GET /runs/{id}/jobs`) and "Event streams
 * (SSE)" (`job.state`, `job.log`, `run.progress`, `queue.state`).
 *
 * State is built two ways that must converge to the same board:
 *   1. REST seed (`fetchRunSnapshot`, backed by `fetchRunJobs`) — a snapshot for first paint.
 *   2. SSE events folded by `applyEvent`, a pure reducer, so the exact same
 *      function drives live updates AND Last-Event-ID replay after a reload
 *      (docs/api-contract.md: "commit order" seq guarantee makes replay
 *      exact) — see useLiveRun.ts and LiveRunScreen.test.tsx.
 *
 * Issue #494: `RunHeader`/`RunJob`/`QueueStatus` below are a client-only VIEW
 * MODEL, not a wire shape — `RunsController` (backend/Waypoint.Api/Controllers/RunsController.cs)
 * sends `RunResponse` from `GET /runs/{id}` and `JobResponse[]` from
 * `GET /runs/{id}/jobs` (see `RunWire`/`JobWire` below), which this module
 * maps into the richer board shape the UI was originally built against. The
 * real backend has NO per-queue/per-benchmark concept at all (`queue.state`
 * is a run-level credential-halt signal, not a named priority queue), and
 * job rows carry no pass/fail/na counts or benchmark label (those live only
 * on `/runs/{id}/artifacts`, results.ts's concern, not this screen's) — so
 * `mapRunHeader`/`mapRunJob` synthesize the closest honest projection: jobs
 * are grouped into one queue per `JobResponse.priority`, and `pass`/`fail`/
 * `na`/`benchmark` are left null/empty rather than fabricated. Where
 * docs/api-contract.md previously described the fictional contract, it has
 * been corrected to match this real shape.
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

/** Target state machine (api-contract.md "State machines" — Scan job), plus
 * `cancelled` (issue #494): the real terminal state `JobStates.Cancelled`
 * (backend/Waypoint.Core/Jobs/JobStates.cs) that per-job cancel and
 * run-abort both leave a job in — omitted from the original fictional
 * contract this module was built against, which is how a cancelled job's
 * `job.state` SSE event crashed `asJobState`'s allowlist. */
export type JobState =
	| "queued"
	| "running"
	| "attesting"
	| "converting"
	| "uploaded"
	| "done"
	| "failed"
	| "auth-failed"
	| "blocked"
	| "cancelled";

export const TERMINAL_JOB_STATES: ReadonlySet<JobState> = new Set(["uploaded", "done", "failed", "auth-failed", "cancelled"]);

/** `stage x 25%` per the prototype README ("Target state machine"). */
const STAGE_ORDER: JobState[] = ["queued", "running", "attesting", "converting", "uploaded"];

export function progressPercentForState(state: JobState): number {
	if (state === "uploaded" || state === "done") {
		return 100;
	}
	const idx = STAGE_ORDER.indexOf(state);
	return idx >= 0 ? idx * 25 : 0;
}

/** View-model row the board layouts render — see the module doc comment for
 * why this is richer than any single wire response. */
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

/** View-model header the screen renders — see the module doc comment. */
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

/**
 * The REAL `GET /runs/{id}` wire shape — `RunResponse`
 * (backend/Waypoint.Api/Contracts/RunContracts.cs). No `site`, no
 * `credential_name`, no `queues`, no aggregate `pass`/`fail`/`na` (those live
 * only on `/runs/{id}/artifacts`, a different screen's concern) — just job
 * counts by state and the queue-level `blocked`/`paused` flags.
 */
interface RunWire {
	id: string;
	run_type: string;
	state: string;
	paused: boolean;
	blocked: boolean;
	blocked_reason: string | null;
	scope: string;
	credential_id: string | null;
	initiated_by: string | null;
	created_at: string;
	started_at: string | null;
	completed_at: string | null;
	job_count: number;
	job_count_queued: number;
	job_count_running: number;
	job_count_completed: number;
	job_count_failed: number;
	job_count_blocked: number;
	/** Issue #663: non-secret attribution snapshot — see `mapRunHeader`'s doc
	 * comment. Optional/absent-safe: older wire fixtures/tests may omit it. */
	credential_name?: string | null;
}

const KNOWN_RUN_HEADER_STATES: ReadonlySet<string> = new Set(["pending", "running", "completed", "completed_with_failures", "aborted"]);

function asRunHeaderState(value: string): RunHeader["state"] {
	return KNOWN_RUN_HEADER_STATES.has(value) ? (value as RunHeader["state"]) : "pending";
}

function elapsedSecondsFrom(startedAt: string | null, completedAt: string | null): number {
	if (!startedAt) {
		return 0;
	}
	const start = Date.parse(startedAt);
	if (Number.isNaN(start)) {
		return 0;
	}
	const end = completedAt ? Date.parse(completedAt) : Date.now();
	return Math.max(0, Math.round((end - start) / 1000));
}

/**
 * Maps the real `RunResponse` (`RunWire`) to the board's `RunHeader` view
 * model. There is no server-side "named priority queue" concept — a run's
 * `blocked`/`blocked_reason` is a single queue-level flag, not per-queue
 * (see `mapRunJob`'s doc comment for the per-job queue grouping this feeds).
 * `site` is not carried on `RunResponse` at all (`scope` is an opaque JSON
 * blob keyed by `site_id`) and is set to `run_type` so the header has
 * something honest to show rather than a fabricated label. `credential_name`
 * (issue #663) IS a real wire field — a non-secret attribution snapshot
 * taken when the credential was a terminal-only reference at deletion time
 * — and is preferred over the bare `credential_id` when present, falling
 * back to `credential_id` (or empty) for runs created before #663 landed.
 */
export function mapRunHeader(wire: RunWire, jobs: RunJob[]): RunHeader {
	const queueMap = new Map<string, QueueStatus>();
	for (const job of jobs) {
		if (!queueMap.has(job.queue)) {
			queueMap.set(job.queue, {
				key: job.queue,
				priority: job.priority,
				name: `PRIORITY ${job.priority}`,
				benchmark: "",
				blocked: wire.blocked,
				blocked_reason: wire.blocked ? wire.blocked_reason : null,
			});
		}
	}
	const queues = [...queueMap.values()].sort((a, b) => a.priority - b.priority);

	const completedCount = wire.job_count_completed;
	const percent = wire.job_count > 0 ? Math.round((completedCount / wire.job_count) * 100) : 0;

	return {
		id: wire.id,
		site: wire.run_type,
		target_count: wire.job_count,
		initiated_by: wire.initiated_by ?? "",
		credential_name: wire.credential_name ?? wire.credential_id ?? "",
		state: asRunHeaderState(wire.state),
		paused: wire.paused,
		// Not carried by RunResponse (only /runs/{id}/artifacts has counts) —
		// left at 0 rather than fabricated; run.progress SSE fills these in live.
		pass: 0,
		fail: 0,
		na: 0,
		percent,
		completed_count: completedCount,
		elapsed_seconds: elapsedSecondsFrom(wire.started_at, wire.completed_at),
		blocked: wire.blocked,
		queues,
	};
}

/**
 * The REAL `GET /runs/{id}/jobs` wire row — `JobResponse`
 * (backend/Waypoint.Api/Contracts/RunContracts.cs). No `benchmark`, no
 * `pass`/`fail`/`na`, no free-text `note` — those are results/artifact
 * concerns, not job-queue concerns, on the shipped backend.
 */
interface JobWire {
	id: string;
	run_id: string | null;
	job_type: string;
	target_id: string | null;
	target_name: string | null;
	state: string;
	stage: string | null;
	priority: number;
	attempt_count: number;
	created_at: string;
	started_at: string | null;
	finished_at: string | null;
}

const KNOWN_JOB_WIRE_STATES: ReadonlySet<string> = new Set([
	"queued",
	"running",
	"attesting",
	"converting",
	"uploaded",
	"done",
	"failed",
	"auth-failed",
	"blocked",
	"cancelled",
]);

function asJobWireState(value: string): JobState {
	return KNOWN_JOB_WIRE_STATES.has(value) ? (value as JobState) : "queued";
}

/**
 * Maps one real `JobResponse` (`JobWire`) to the board's `RunJob` view
 * model. `queue` groups jobs by `priority` (the one queue-like signal
 * `JobResponse` actually carries) rather than a named priority-queue id the
 * backend has no concept of; `benchmark` and `pass`/`fail`/`na` have no wire
 * source on this endpoint and are left empty/null (never fabricated) —
 * `/runs/{id}/artifacts` is where real CAT counts live, a different
 * screen's (`results.ts`) concern. `note` starts empty and is filled in
 * live by `job.log` SSE via `applyEvent`, same as before.
 */
export function mapRunJob(wire: JobWire): RunJob {
	const state = asJobWireState(wire.state);
	return {
		job_id: wire.id,
		target: wire.target_name ?? wire.target_id ?? "unknown",
		queue: `p${wire.priority}`,
		priority: wire.priority,
		benchmark: "",
		state,
		progress_percent: progressPercentForState(state),
		pass: null,
		fail: null,
		na: null,
		note: "",
	};
}

/**
 * Fetches and maps both REST endpoints together — `mapRunHeader` needs the
 * mapped jobs to derive its synthetic `queues` (see its doc comment), so the
 * two requests are combined here rather than composed by the caller (which
 * would otherwise double-fetch `/runs/{id}/jobs`, once for `fetchRunJobs` and
 * once inside a naive `fetchRun`).
 */
export async function fetchRunSnapshot(runId: string): Promise<{ header: RunHeader; jobs: RunJob[] }> {
	const [wire, jobs] = await Promise.all([apiGet<RunWire>(`/runs/${runId}`), fetchRunJobs(runId)]);
	return { header: mapRunHeader(wire, jobs), jobs };
}

export async function fetchRunJobs(runId: string): Promise<RunJob[]> {
	const wireJobs = await apiGet<JobWire[]>(`/runs/${runId}/jobs`);
	return wireJobs.map(mapRunJob);
}

/** The real `RunActionResponse` wire shape (`run_id`, not `id`). */
export interface RunActionResult {
	run_id: string;
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
	/** Issue #406: present when this event is the run reaching a contract terminal
	 * state (`completed`/`completed_with_failures`) — the job-engine's run-completion
	 * write rides the existing `run.progress` channel rather than a new event type
	 * (docs/api-contract.md's SSE types are a closed six-value set). Absent on every
	 * other `run.progress` emission (plain counter progress, abort), so it must never
	 * overwrite `header.state` with `undefined`. */
	state?: RunHeader["state"];
}

const KNOWN_RUN_STATES: ReadonlySet<string> = new Set(["pending", "running", "completed", "completed_with_failures", "aborted"]);

function asRunState(value: string | undefined): RunHeader["state"] | undefined {
	return value !== undefined && KNOWN_RUN_STATES.has(value) ? (value as RunHeader["state"]) : undefined;
}

/**
 * `queue.state` event payload — the REAL shape `JobQueueRepository` emits
 * (`EmitBornBlockedAsync`/`EmitCredentialSwappedAsync`,
 * backend/Waypoint.Infrastructure/Jobs/JobQueueRepository.cs): a run-level
 * credential-halt signal, `{ blocked, reason, credential_ids }` (born-blocked)
 * or `{ blocked: false, swapped: true, old_credential_id, new_credential_id }`
 * (swap-resume) — there is no per-queue `key` on the wire (issue #494: the
 * original fictional contract invented one). Since the board's `queues` are
 * a client-side grouping by job priority, not a server-known id, this event
 * is applied to every synthesized queue rather than keyed to one.
 */
interface QueueStateData {
	blocked?: boolean;
	reason?: string | null;
}

/** `job.log` event payload — only the note-worthy fields this screen renders. */
interface JobLogData {
	target?: string;
	line?: string;
	message?: string;
}

function asJobState(value: string | undefined): JobState | undefined {
	return value !== undefined && KNOWN_JOB_WIRE_STATES.has(value) ? (value as JobState) : undefined;
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
				state: asRunState(data.state) ?? snapshot.header.state,
			},
		};
	}

	if (event.type === "queue.state") {
		const data = event.data as QueueStateData;
		if (data.blocked === undefined) {
			return { ...snapshot, lastSeq };
		}
		const blockedReason = data.blocked ? (data.reason ?? null) : null;
		const queues = snapshot.header.queues.map((q) => ({ ...q, blocked: data.blocked as boolean, blocked_reason: blockedReason }));
		return { ...snapshot, lastSeq, header: { ...snapshot.header, queues, blocked: data.blocked } };
	}

	return { ...snapshot, lastSeq };
}

export function formatElapsed(totalSeconds: number): string {
	const clamped = Math.max(0, Math.floor(totalSeconds));
	const minutes = Math.floor(clamped / 60);
	const seconds = clamped % 60;
	return `${minutes}m ${String(seconds).padStart(2, "0")}s`;
}
