import { describe, expect, it } from "vitest";
import type { WaypointEvent } from "../../lib/events";
import {
	applyEvent,
	isActiveGroup,
	isTerminalJobState,
	isTerminalRunState,
	mapRunJobItem,
	mapRunListItem,
	waitReasonForJob,
	type LiveJobsSnapshot,
	type LiveRunGroup,
} from "./livejobs";

function job(overrides: Partial<LiveRunGroup["jobs"][number]> = {}): LiveRunGroup["jobs"][number] {
	return {
		job_id: "j-1",
		run_id: "run-1",
		job_type: "scan",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "queued",
		stage: null,
		attempt_count: 0,
		created_at: "2026-08-24T00:00:00Z",
		started_at: null,
		finished_at: null,
		lastLogLine: null,
		logLines: [],
		...overrides,
	};
}

function group(overrides: Partial<LiveRunGroup> = {}): LiveRunGroup {
	return {
		run_id: "run-1",
		run_type: "scan",
		state: "running",
		paused: false,
		blocked: false,
		blocked_reason: null,
		scope: "{}",
		initiated_by: "j.moreno",
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:00Z",
		completed_at: null,
		job_count: 1,
		job_count_completed: 0,
		job_count_failed: 0,
		jobs: [job()],
		...overrides,
	};
}

function snapshot(groups: LiveRunGroup[], lastSeq = 0): LiveJobsSnapshot {
	return { runs: groups, lastSeq };
}

describe("isTerminalRunState / isTerminalJobState", () => {
	it.each(["completed", "completed_with_failures", "aborted"])("treats run state %s as terminal", (state) => {
		expect(isTerminalRunState(state)).toBe(true);
	});

	it.each(["pending", "running"])("treats run state %s as non-terminal", (state) => {
		expect(isTerminalRunState(state)).toBe(false);
	});

	it.each(["uploaded", "done", "failed", "auth-failed", "cancelled"])("treats job state %s as terminal", (state) => {
		expect(isTerminalJobState(state)).toBe(true);
	});

	it.each(["queued", "running", "blocked"])("treats job state %s as non-terminal", (state) => {
		expect(isTerminalJobState(state)).toBe(false);
	});
});

describe("mapRunJobItem / mapRunListItem", () => {
	it("maps the real JobResponse wire shape into a LiveJobRow", () => {
		const row = mapRunJobItem("run-9", {
			id: "j-9",
			job_type: "download",
			target_id: null,
			target_name: null,
			state: "running",
			stage: "fetch",
			attempt_count: 1,
			created_at: "2026-08-24T00:00:00Z",
			started_at: "2026-08-24T00:01:00Z",
			finished_at: null,
		});
		expect(row).toEqual({
			job_id: "j-9",
			run_id: "run-9",
			job_type: "download",
			target_id: null,
			target_name: null,
			state: "running",
			stage: "fetch",
			attempt_count: 1,
			created_at: "2026-08-24T00:00:00Z",
			started_at: "2026-08-24T00:01:00Z",
			finished_at: null,
			lastLogLine: null,
			logLines: [],
		});
	});

	it("maps the real RunResponse wire shape into a LiveRunGroup, carrying the mapped jobs", () => {
		const jobs = [mapRunJobItem("run-9", { id: "j-9", job_type: "download", target_id: null, target_name: null, state: "running", stage: null, attempt_count: 0, created_at: "t", started_at: null, finished_at: null })];
		const g = mapRunListItem(
			{
				id: "run-9",
				run_type: "download",
				state: "running",
				scope: '{"site_id":"s-1"}',
				initiated_by: "j.moreno",
				created_at: "2026-08-24T00:00:00Z",
				started_at: "2026-08-24T00:00:00Z",
				completed_at: null,
				job_count: 1,
				job_count_queued: 0,
				job_count_running: 1,
				job_count_completed: 0,
				job_count_failed: 0,
				job_count_blocked: 0,
			},
			jobs,
		);
		expect(g.run_id).toBe("run-9");
		expect(g.jobs).toBe(jobs);
		expect(g.blocked).toBe(false);
	});
});

describe("isActiveGroup", () => {
	it("is active when the run itself is non-terminal", () => {
		expect(isActiveGroup(group({ state: "running" }))).toBe(true);
	});

	it("is active when the run is terminal but a job is still non-terminal", () => {
		expect(isActiveGroup(group({ state: "completed", jobs: [job({ state: "running" })] }))).toBe(true);
	});

	it("is inactive when the run and every job are terminal", () => {
		expect(isActiveGroup(group({ state: "completed", jobs: [job({ state: "done" })] }))).toBe(false);
	});

	it("is inactive for a terminal run with no jobs at all", () => {
		expect(isActiveGroup(group({ state: "aborted", jobs: [] }))).toBe(false);
	});
});

describe("waitReasonForJob", () => {
	it("reports the run's blocked_reason for a blocked job", () => {
		const g = group({ blocked: true, blocked_reason: "credential expired" });
		expect(waitReasonForJob(job({ state: "blocked" }), g)).toBe("credential expired");
	});

	it("falls back to a generic label for a blocked job with no reason", () => {
		const g = group({ blocked: true, blocked_reason: null });
		expect(waitReasonForJob(job({ state: "blocked" }), g)).toBe("blocked");
	});

	it("reports 'queued' for a queued job on an unblocked run", () => {
		expect(waitReasonForJob(job({ state: "queued" }), group({ blocked: false }))).toBe("queued");
	});

	it("reports the run's blocked_reason for a queued job on a blocked run", () => {
		const g = group({ blocked: true, blocked_reason: "capacity exhausted" });
		expect(waitReasonForJob(job({ state: "queued" }), g)).toBe("capacity exhausted");
	});

	it("returns null for a running job", () => {
		expect(waitReasonForJob(job({ state: "running" }), group())).toBeNull();
	});
});

describe("applyEvent", () => {
	it("updates a job's state across groups by job_id, regardless of which group carries it", () => {
		const g1 = group({ run_id: "run-1", jobs: [job({ job_id: "j-1", run_id: "run-1", state: "queued" })] });
		const g2 = group({ run_id: "run-2", jobs: [job({ job_id: "j-2", run_id: "run-2", state: "queued" })] });
		const event: WaypointEvent = { seq: 5, ts: "t", type: "job.state", run_id: "run-2", job_id: "j-2", data: { to: "running" } };

		const next = applyEvent(snapshot([g1, g2]), event);

		expect(next.runs[0].jobs[0].state).toBe("queued");
		expect(next.runs[1].jobs[0].state).toBe("running");
		expect(next.lastSeq).toBe(5);
	});

	it("drops a job.state event for a job_id not present in any group (seed is authoritative)", () => {
		const g1 = group();
		const next = applyEvent(snapshot([g1]), {
			seq: 3,
			ts: "t",
			type: "job.state",
			run_id: "run-1",
			job_id: "unknown-job",
			data: { to: "running" },
		});
		expect(next.runs[0].jobs[0].state).toBe("queued");
		expect(next.lastSeq).toBe(3);
	});

	it("ignores a job.state event with no 'to' field beyond bumping lastSeq", () => {
		const g1 = group();
		const next = applyEvent(snapshot([g1]), { seq: 2, ts: "t", type: "job.state", run_id: "run-1", job_id: "j-1", data: {} });
		expect(next.runs[0].jobs[0].state).toBe("queued");
		expect(next.lastSeq).toBe(2);
	});

	it("appends job.log lines to the job's bounded log buffer and sets lastLogLine", () => {
		const g1 = group();
		const next = applyEvent(snapshot([g1]), {
			seq: 4,
			ts: "t",
			type: "job.log",
			run_id: "run-1",
			job_id: "j-1",
			data: { line: "hello" },
		});
		expect(next.runs[0].jobs[0].lastLogLine).toBe("hello");
		expect(next.runs[0].jobs[0].logLines).toEqual(["hello"]);
	});

	it("caps the per-job log buffer at 200 lines", () => {
		let g1 = group({ jobs: [job({ logLines: Array.from({ length: 200 }, (_, i) => `line-${i}`) })] });
		let snap = snapshot([g1]);
		snap = applyEvent(snap, { seq: 1, ts: "t", type: "job.log", run_id: "run-1", job_id: "j-1", data: { line: "line-200" } });
		expect(snap.runs[0].jobs[0].logLines).toHaveLength(200);
		expect(snap.runs[0].jobs[0].logLines[0]).toBe("line-1");
		expect(snap.runs[0].jobs[0].logLines.at(-1)).toBe("line-200");
	});

	it("updates run.progress fields on the matching run group", () => {
		const g1 = group({ run_id: "run-1", state: "running", job_count_completed: 0 });
		const next = applyEvent(snapshot([g1]), {
			seq: 7,
			ts: "t",
			type: "run.progress",
			run_id: "run-1",
			data: { state: "completed", completed_count: 3 },
		});
		expect(next.runs[0].state).toBe("completed");
		expect(next.runs[0].job_count_completed).toBe(3);
	});

	it("updates queue.state (run-level blocked flag) on the matching run group", () => {
		const g1 = group({ run_id: "run-1", blocked: false });
		const next = applyEvent(snapshot([g1]), {
			seq: 8,
			ts: "t",
			type: "queue.state",
			run_id: "run-1",
			data: { blocked: true, reason: "credential failure" },
		});
		expect(next.runs[0].blocked).toBe(true);
		expect(next.runs[0].blocked_reason).toBe("credential failure");
	});

	it("clears blocked_reason when queue.state reports unblocked", () => {
		const g1 = group({ run_id: "run-1", blocked: true, blocked_reason: "was blocked" });
		const next = applyEvent(snapshot([g1]), {
			seq: 9,
			ts: "t",
			type: "queue.state",
			run_id: "run-1",
			data: { blocked: false },
		});
		expect(next.runs[0].blocked).toBe(false);
		expect(next.runs[0].blocked_reason).toBeNull();
	});

	it("is a no-op (besides lastSeq) for an unrecognized event type", () => {
		const g1 = group();
		const next = applyEvent(snapshot([g1]), { seq: 11, ts: "t", type: "download.progress", data: {} });
		expect(next.runs).toEqual([g1]);
		expect(next.lastSeq).toBe(11);
	});

	it("keeps lastSeq monotonic even when an event arrives out of order", () => {
		const next = applyEvent(snapshot([group()], 50), { seq: 10, ts: "t", type: "system.notice", data: {} });
		expect(next.lastSeq).toBe(50);
	});
});
