/**
 * Live Jobs data layer (issue #590, ADR-0019, epic #588) — the global
 * concurrent operational projection over every active Run/Job, replacing
 * the scan-only board the former `screens/liverun/liverun.ts` built for a
 * single run (removed by issue #693 once #591's type renderers covered it).
 *
 * ADR-0019 decision 1: "A top-level workspace lists every active Run and
 * Job, groups jobs by run, and lets the operator select among concurrent
 * work. Selection observes execution; it does not schedule or serialize
 * it." This module is the pure state (seed + reducer) that screen renders;
 * `useLiveJobs.ts` owns the REST seed + SSE subscription wiring.
 *
 * Seed: `GET /runs` (`RunResponse[]`, already paginated newest-first) plus,
 * for every non-terminal run, `GET /runs/{id}/jobs` (`JobResponse[]`) — the
 * same wire shapes `../results/results.ts` already maps, reused here
 * rather than re-typed a second time. There is no
 * server-side "active runs" filter yet (ADR-0019's "planned" section: "the
 * global `/runs?cursor=...` list read remains planned, unimplemented"), so
 * "active" is a client-side filter over `RunListItem.state` — additive-only,
 * no backend change.
 *
 * Live updates: the SAME global SSE feed the job-log drawer already
 * consumes (`GET /api/v1/events`, `lib/events.ts`), folded by `applyEvent`
 * below — a pure reducer so the identical function drives live events and
 * Last-Event-ID replay after reconnect, the same convergence property
 * the former single-run `liverun.ts` `applyEvent` documented.
 */
import type { RunJobItem, RunListItem } from "../results/results";
import type { WaypointEvent } from "../../lib/events";

/** Run states with no further transition — mirrors `results.ts`'s
 * `isTerminalRunState` (kept as its own copy per that module's own
 * precedent: no SSE/state-machine coupling between screens is worth an
 * incidental cross-screen import for one predicate). */
export function isTerminalRunState(state: string): boolean {
	return state === "completed" || state === "completed_with_failures" || state === "aborted";
}

/** Job states with no further transition — the same closed set the former
 * `liverun.ts` `TERMINAL_JOB_STATES` used (docs/api-contract.md job states). */
const TERMINAL_JOB_STATES: ReadonlySet<string> = new Set(["uploaded", "done", "failed", "auth-failed", "cancelled"]);

export function isTerminalJobState(state: string): boolean {
	return TERMINAL_JOB_STATES.has(state);
}

/** One job row within a grouped run, view-model shape derived from
 * `RunJobItem` (`JobResponse`) plus whatever the SSE feed has updated live. */
export interface LiveJobRow {
	job_id: string;
	run_id: string;
	job_type: string;
	target_id: string | null;
	target_name: string | null;
	state: string;
	stage: string | null;
	attempt_count: number;
	created_at: string;
	started_at: string | null;
	finished_at: string | null;
	/** Most recent `job.log` line seen for this job, if any — the same
	 * "latest note" treatment the former `liverun.ts` `RunJob.note` used. */
	lastLogLine: string | null;
	/** Bounded live-tail buffer of recent `job.log` lines for this job
	 * (newest last), capped at `MAX_JOB_LOG_LINES`. Feeds the generic detail
	 * renderer's active-job log view; a terminal job's detail switches to the
	 * persisted history API instead (`useJobHistory.ts`), so this buffer only
	 * needs to cover "what's happening right now", not full recall. */
	logLines: string[];
}

/** Soft cap on the per-job live-tail buffer — an implementation memory
 * bound, mirroring `JobLogDrawer.tsx`'s `MAX_BUFFERED_LINES` precedent but
 * scoped per-job rather than per-drawer since this workspace can have many
 * jobs live at once. */
const MAX_JOB_LOG_LINES = 200;

/** One run group the workspace lists — `RunListItem` (`RunResponse`) plus
 * its jobs and a client-derived capacity/wait-reason projection. */
export interface LiveRunGroup {
	run_id: string;
	run_type: string;
	state: string;
	paused: boolean;
	blocked: boolean;
	blocked_reason: string | null;
	scope: string;
	initiated_by: string | null;
	created_at: string;
	started_at: string | null;
	completed_at: string | null;
	job_count: number;
	job_count_completed: number;
	job_count_failed: number;
	jobs: LiveJobRow[];
}

export interface LiveJobsSnapshot {
	runs: LiveRunGroup[];
	/** Highest `seq` reflected in this snapshot — the reconnect `Last-Event-ID`. */
	lastSeq: number;
}

export function mapRunJobItem(runId: string, wire: RunJobItem): LiveJobRow {
	return {
		job_id: wire.id,
		run_id: runId,
		job_type: wire.job_type,
		target_id: wire.target_id,
		target_name: wire.target_name,
		state: wire.state,
		stage: wire.stage,
		attempt_count: wire.attempt_count,
		created_at: wire.created_at,
		started_at: wire.started_at,
		finished_at: wire.finished_at,
		lastLogLine: null,
		logLines: [],
	};
}

export function mapRunListItem(wire: RunListItem, jobs: LiveJobRow[]): LiveRunGroup {
	return {
		run_id: wire.id,
		run_type: wire.run_type,
		state: wire.state,
		paused: false,
		blocked: false,
		blocked_reason: null,
		scope: wire.scope,
		initiated_by: wire.initiated_by,
		created_at: wire.created_at,
		started_at: wire.started_at,
		completed_at: wire.completed_at,
		job_count: wire.job_count,
		job_count_completed: wire.job_count_completed,
		job_count_failed: wire.job_count_failed,
		jobs,
	};
}

/** A run/group is "active" for this workspace's default view when the run
 * itself has not reached a terminal state, OR it has at least one
 * non-terminal job (a run can flip terminal slightly before its last job
 * event lands — keeping it visible until every job resolves avoids a job
 * disappearing mid-flight). */
export function isActiveGroup(group: LiveRunGroup): boolean {
	if (!isTerminalRunState(group.state)) {
		return true;
	}
	return group.jobs.some((j) => !isTerminalJobState(j.state));
}

/** `job.state` event payload (api-contract.md event envelope). */
interface JobStateData {
	target?: string;
	from?: string;
	to?: string;
}

/** `job.log` event payload. */
interface JobLogData {
	line?: string;
	message?: string;
}

/** `run.progress` event payload (subset this workspace renders). */
interface RunProgressData {
	state?: string;
	completed_count?: number;
}

/**
 * Folds one global SSE event into a `LiveJobsSnapshot`. Mirrors
 * the former single-run `liverun.ts` `applyEvent` shape (pure, returns a new
 * snapshot) but is multi-run: every event carries its own `run_id`, so
 * routing is "find the group with this run_id" rather than "is this event
 * for my one run". A `job.state`/`job.log` event for a job not present in
 * any group's `jobs` (predates the REST seed, or belongs to a run this page
 * has not fetched jobs for yet) is dropped, same "seed is authoritative"
 * posture as the single-run screen — never synthesizes a row from a bare
 * event.
 */
export function applyEvent(snapshot: LiveJobsSnapshot, event: WaypointEvent): LiveJobsSnapshot {
	const lastSeq = Math.max(snapshot.lastSeq, event.seq);

	if (event.type === "job.state" && event.job_id) {
		const data = event.data as JobStateData;
		const to = data.to;
		if (!to) {
			return { ...snapshot, lastSeq };
		}
		let changed = false;
		const runs = snapshot.runs.map((group) => {
			const idx = group.jobs.findIndex((j) => j.job_id === event.job_id);
			if (idx === -1) {
				return group;
			}
			changed = true;
			const jobs = [...group.jobs];
			jobs[idx] = { ...jobs[idx], state: to };
			return { ...group, jobs };
		});
		return changed ? { runs, lastSeq } : { ...snapshot, lastSeq };
	}

	if (event.type === "job.log" && event.job_id) {
		const data = event.data as JobLogData;
		const note = data.line ?? data.message;
		if (!note) {
			return { ...snapshot, lastSeq };
		}
		let changed = false;
		const runs = snapshot.runs.map((group) => {
			const idx = group.jobs.findIndex((j) => j.job_id === event.job_id);
			if (idx === -1) {
				return group;
			}
			changed = true;
			const jobs = [...group.jobs];
			const nextLines = [...jobs[idx].logLines, note];
			jobs[idx] = {
				...jobs[idx],
				lastLogLine: note,
				logLines: nextLines.length > MAX_JOB_LOG_LINES ? nextLines.slice(nextLines.length - MAX_JOB_LOG_LINES) : nextLines,
			};
			return { ...group, jobs };
		});
		return changed ? { runs, lastSeq } : { ...snapshot, lastSeq };
	}

	if (event.type === "run.progress" && event.run_id) {
		const data = event.data as RunProgressData;
		const idx = snapshot.runs.findIndex((g) => g.run_id === event.run_id);
		if (idx === -1) {
			return { ...snapshot, lastSeq };
		}
		const runs = [...snapshot.runs];
		runs[idx] = {
			...runs[idx],
			state: data.state ?? runs[idx].state,
			job_count_completed: data.completed_count ?? runs[idx].job_count_completed,
		};
		return { runs, lastSeq };
	}

	if (event.type === "queue.state" && event.run_id) {
		const data = event.data as { blocked?: boolean; reason?: string | null };
		if (data.blocked === undefined) {
			return { ...snapshot, lastSeq };
		}
		const idx = snapshot.runs.findIndex((g) => g.run_id === event.run_id);
		if (idx === -1) {
			return { ...snapshot, lastSeq };
		}
		const runs = [...snapshot.runs];
		runs[idx] = { ...runs[idx], blocked: data.blocked, blocked_reason: data.blocked ? (data.reason ?? null) : null };
		return { runs, lastSeq };
	}

	return { ...snapshot, lastSeq };
}

/** Human-readable capacity/wait-reason string for one job (issue #590 AC
 * "capacity/wait reason"). Derives from data the workspace already has —
 * `blocked_reason` on the owning run (credential halt, ADR-0008) and the
 * job's own `state`/`stage` — rather than a dedicated per-job field, which
 * the job engine does not expose today (issue #601, still open, tracks
 * surfacing `GET /system`'s `capacity_pool` starvation reasons; see this
 * module's doc comment in `useLiveJobs.ts` for why that is a distinct,
 * system-wide signal this per-job helper does not attempt to fold in). */
export function waitReasonForJob(job: LiveJobRow, group: LiveRunGroup): string | null {
	if (job.state === "blocked") {
		return group.blocked_reason ?? "blocked";
	}
	if (job.state === "queued") {
		return group.blocked ? (group.blocked_reason ?? "run blocked") : "queued";
	}
	return null;
}
