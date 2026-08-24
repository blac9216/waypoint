import { describe, expect, it } from "vitest";
import type { WaypointEvent } from "../../lib/events";
import { applyEvent, formatElapsed, progressPercentForState, type RunSnapshot } from "./liverun";

function baseSnapshot(): RunSnapshot {
	return {
		lastSeq: 0,
		header: {
			id: "run-0808-0100Z",
			site: "Alpha Enclave",
			target_count: 2,
			initiated_by: "j.moreno",
			credential_name: "svc-stig-scan",
			state: "running",
			paused: false,
			pass: 0,
			fail: 0,
			na: 0,
			percent: 0,
			completed_count: 0,
			elapsed_seconds: 0,
			blocked: false,
			queues: [
				{
					key: "esxi",
					priority: 4,
					name: "ESXI HOSTS",
					benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
					blocked: false,
					blocked_reason: null,
				},
			],
		},
		jobs: [
			{
				job_id: "j-1",
				target: "esxi-01.example.internal",
				queue: "esxi",
				priority: 4,
				benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
				state: "queued",
				progress_percent: 0,
				pass: null,
				fail: null,
				na: null,
				note: "",
			},
			{
				job_id: "j-2",
				target: "esxi-02.example.internal",
				queue: "esxi",
				priority: 4,
				benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
				state: "queued",
				progress_percent: 0,
				pass: null,
				fail: null,
				na: null,
				note: "",
			},
		],
	};
}

function evt<T>(overrides: Partial<WaypointEvent<T>>): WaypointEvent<T> {
	return {
		seq: 1,
		ts: "2026-08-08T01:00:00Z",
		type: "job.state",
		data: {} as T,
		...overrides,
	};
}

describe("progressPercentForState", () => {
	it("maps the five in-flight stages to stage * 25%", () => {
		expect(progressPercentForState("queued")).toBe(0);
		expect(progressPercentForState("running")).toBe(25);
		expect(progressPercentForState("attesting")).toBe(50);
		expect(progressPercentForState("converting")).toBe(75);
		expect(progressPercentForState("uploaded")).toBe(100);
	});

	it("treats SRG's HDF-only 'done' terminal state as 100%", () => {
		expect(progressPercentForState("done")).toBe(100);
	});

	it("has no stage index for failed/blocked and reports 0", () => {
		expect(progressPercentForState("failed")).toBe(0);
		expect(progressPercentForState("blocked")).toBe(0);
	});
});

describe("applyEvent", () => {
	it("advances a job's state and progress on job.state", () => {
		const next = applyEvent(
			baseSnapshot(),
			evt({ seq: 10, type: "job.state", job_id: "j-1", data: { target: "esxi-01.example.internal", from: "queued", to: "running" } }),
		);
		expect(next.jobs[0].state).toBe("running");
		expect(next.jobs[0].progress_percent).toBe(25);
		expect(next.jobs[1].state).toBe("queued");
		expect(next.lastSeq).toBe(10);
	});

	it("does not mutate the input snapshot", () => {
		const input = baseSnapshot();
		const frozenJobs = JSON.stringify(input.jobs);
		applyEvent(input, evt({ seq: 1, job_id: "j-1", data: { to: "running" } }));
		expect(JSON.stringify(input.jobs)).toBe(frozenJobs);
	});

	it("ignores job.state for a job_id not present in the snapshot (stale/unseeded event)", () => {
		const next = applyEvent(baseSnapshot(), evt({ seq: 5, job_id: "j-unknown", data: { to: "running" } }));
		expect(next.jobs).toEqual(baseSnapshot().jobs);
		expect(next.lastSeq).toBe(5);
	});

	it("ignores events scoped to a different run_id", () => {
		const next = applyEvent(
			baseSnapshot(),
			evt({ seq: 5, run_id: "some-other-run", job_id: "j-1", data: { to: "running" } }),
		);
		expect(next.jobs[0].state).toBe("queued");
		expect(next.lastSeq).toBe(0);
	});

	it("updates the note from job.log", () => {
		const next = applyEvent(
			baseSnapshot(),
			evt({ seq: 3, type: "job.log", job_id: "j-1", data: { line: "connecting…" } }),
		);
		expect(next.jobs[0].note).toBe("connecting…");
	});

	it("folds run.progress into header counters", () => {
		const next = applyEvent(
			baseSnapshot(),
			evt({ seq: 7, type: "run.progress", data: { pass: 3, fail: 1, na: 2, percent: 40, completed_count: 1, elapsed_seconds: 90 } }),
		);
		expect(next.header).toMatchObject({ pass: 3, fail: 1, na: 2, percent: 40, completed_count: 1, elapsed_seconds: 90 });
	});

	it("flips the run to blocked on queue.state (real payload has no per-queue key — issue #494) and rolls it up to header.blocked", () => {
		const next = applyEvent(baseSnapshot(), evt({ seq: 9, type: "queue.state", data: { blocked: true, reason: "credential failure" } }));
		expect(next.header.blocked).toBe(true);
		expect(next.header.queues[0]).toMatchObject({ blocked: true, blocked_reason: "credential failure" });
	});

	it("is idempotent-safe under out-of-order-looking replays: applying events in commit (seq) order converges", () => {
		// docs/api-contract.md: seq is assigned in commit order, so replaying
		// WHERE seq > last in that order is exact. This test proves applying
		// a sequence of events via applyEvent produces the terminal board a
		// human reading the events in order would expect.
		const events: WaypointEvent[] = [
			evt({ seq: 1, type: "job.state", job_id: "j-1", data: { to: "running" } }),
			evt({ seq: 2, type: "job.state", job_id: "j-1", data: { to: "attesting" } }),
			evt({ seq: 3, type: "job.state", job_id: "j-1", data: { to: "converting" } }),
			evt({ seq: 4, type: "job.state", job_id: "j-1", data: { to: "uploaded" } }),
			evt({ seq: 5, type: "run.progress", data: { pass: 150, fail: 4, na: 12, percent: 50, completed_count: 1 } }),
		];
		let snapshot = baseSnapshot();
		for (const event of events) {
			snapshot = applyEvent(snapshot, event);
		}
		expect(snapshot.jobs[0].state).toBe("uploaded");
		expect(snapshot.jobs[0].progress_percent).toBe(100);
		expect(snapshot.header.pass).toBe(150);
		expect(snapshot.lastSeq).toBe(5);
	});

	it("no-ops on event types this screen does not render (download.progress, system.notice)", () => {
		const withDownload = applyEvent(
			baseSnapshot(),
			evt({ seq: 2, type: "download.progress", job_id: "j-1", data: { progress_percent: 50 } }),
		);
		expect(withDownload.jobs).toEqual(baseSnapshot().jobs);
		expect(withDownload.lastSeq).toBe(2);
	});
});

describe("formatElapsed", () => {
	it("formats seconds as Nm NNs", () => {
		expect(formatElapsed(0)).toBe("0m 00s");
		expect(formatElapsed(65)).toBe("1m 05s");
		expect(formatElapsed(252)).toBe("4m 12s");
	});

	it("clamps negative input to 0", () => {
		expect(formatElapsed(-5)).toBe("0m 00s");
	});
});
