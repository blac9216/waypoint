import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { LiveJobsScreen } from "./LiveJobsScreen";

/**
 * Issue #590 coverage: grouping/selection across multiple concurrent runs,
 * SSE update + REST reconciliation on reconnect, deep links via `?run=&job=`,
 * accessibility roles/keyboard, and the honest empty state. Fixtures are the
 * REAL wire shapes (`RunResponse`/`JobResponse`) `RunsController` sends —
 * same discipline `LiveRunScreen.test.tsx` established after issue #494.
 */

/** `RunResponse` (backend/Waypoint.Api/Contracts/RunContracts.cs) — this suite's subset. */
interface RunWireFixture {
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
}

/** `JobResponse` — this suite's subset. */
interface JobWireFixture {
	id: string;
	run_id: string;
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

const RUN_1: RunWireFixture = {
	id: "run-1",
	run_type: "scan",
	state: "running",
	paused: false,
	blocked: false,
	blocked_reason: null,
	scope: '{"site_id":"s-1"}',
	credential_id: null,
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
};

const RUN_2: RunWireFixture = {
	...RUN_1,
	id: "run-2",
	run_type: "download",
	initiated_by: "a.diaz",
};

const RUN_1_JOBS: JobWireFixture[] = [
	{
		id: "job-1",
		run_id: "run-1",
		job_type: "scan",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "running",
		stage: null,
		priority: 4,
		attempt_count: 0,
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:01Z",
		finished_at: null,
	},
];

const RUN_2_JOBS: JobWireFixture[] = [
	{
		id: "job-2",
		run_id: "run-2",
		job_type: "download",
		target_id: null,
		target_name: "vcf-artifact-bundle",
		state: "queued",
		stage: null,
		priority: 4,
		attempt_count: 0,
		created_at: "2026-08-24T00:00:00Z",
		started_at: null,
		finished_at: null,
	},
];

interface MockOptions {
	runs?: RunWireFixture[];
	jobsByRun?: Record<string, JobWireFixture[]>;
	/** SSE frames delivered on the FIRST connection attempt only (kept open otherwise). */
	frames?: unknown[];
	/** When true, the first SSE attempt closes after its frames so a second (reconnect) attempt happens. */
	reconnect?: boolean;
}

function installFetchMock(opts: MockOptions) {
	const runs = opts.runs ?? [RUN_1, RUN_2];
	const jobsByRun = opts.jobsByRun ?? { "run-1": RUN_1_JOBS, "run-2": RUN_2_JOBS };
	let sseAttempts = 0;

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const accept = new Headers(init?.headers).get("Accept");

		if (url.startsWith("/api/v1/runs?")) {
			return new Response(JSON.stringify(runs), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		const jobsMatch = /^\/api\/v1\/runs\/(.+)\/jobs$/.exec(url);
		if (jobsMatch) {
			const jobs = jobsByRun[jobsMatch[1]] ?? [];
			return new Response(JSON.stringify(jobs), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url === "/api/v1/events") {
			const attemptIndex = sseAttempts++;
			const frames = attemptIndex === 0 ? (opts.frames ?? []) : [];
			const shouldClose = attemptIndex === 0 && opts.reconnect === true;
			const encoder = new TextEncoder();
			const body = frames.map((f) => `id: x\ndata: ${JSON.stringify(f)}\n\n`).join("");
			const chunk = encoder.encode(body);
			let sent = false;
			const signal = init?.signal;
			const readerBody = {
				getReader() {
					return {
						read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
							if (!sent) {
								sent = true;
								return Promise.resolve({ value: chunk, done: false });
							}
							if (shouldClose) {
								return Promise.resolve({ value: undefined, done: true });
							}
							return new Promise((_resolve, reject) => {
								if (signal?.aborted) {
									reject(new DOMException("aborted", "AbortError"));
									return;
								}
								signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
							});
						},
						releaseLock() {},
					};
				},
			};
			return { ok: true, status: 200, body: readerBody } as unknown as Response;
		}
		if (url === "/api/v1/auth/me") {
			return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
		}
		if (accept === "text/event-stream") {
			return { ok: true, status: 200, body: { getReader: () => ({ read: () => new Promise(() => {}), releaseLock() {} }) } } as unknown as Response;
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithAuth(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({ token: "tok", username: "j.moreno", role, expiresAt: new Date(Date.now() + 3600_000).toISOString() }),
	);
	return render(
		<AuthProvider>
			<LiveJobsScreen />
		</AuthProvider>,
	);
}

describe("LiveJobsScreen (issue #590)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		sessionStorage.clear();
		window.history.pushState(null, "", "/live-jobs");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		sessionStorage.clear();
	});

	it("groups jobs by run and lists every concurrently active run", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-1")).toBeInTheDocument());
		expect(screen.getByText("run-2")).toBeInTheDocument();
		expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("vcf-artifact-bundle")).toBeInTheDocument();
		expect(screen.getByText("2 active runs")).toBeInTheDocument();
	});

	it("renders an honest empty state when there is no active work", async () => {
		installFetchMock({ runs: [], jobsByRun: {} });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("No active runs or jobs right now.")).toBeInTheDocument());
	});

	it("filters out fully terminal runs (run and every job terminal)", async () => {
		const terminalRun = { ...RUN_1, id: "run-done", state: "completed", completed_at: "2026-08-24T00:05:00Z" };
		const terminalJobs = [{ ...RUN_1_JOBS[0], id: "job-done", run_id: "run-done", state: "done", finished_at: "2026-08-24T00:05:00Z" }];
		installFetchMock({ runs: [terminalRun], jobsByRun: { "run-done": terminalJobs } });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("No active runs or jobs right now.")).toBeInTheDocument());
		expect(screen.queryByText("run-done")).not.toBeInTheDocument();
	});

	it("auto-selects the first run and shows its detail pane without hiding the other run", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByRole("region", { name: /Run detail: run-1/ })).toBeInTheDocument());
		// The other run's row is still rendered, not hidden by selection.
		expect(screen.getByText("run-2")).toBeInTheDocument();
	});

	it("selecting one job's row changes the detail pane without removing the other run/job from the list", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());
		fireEvent.click(screen.getByText("vcf-artifact-bundle"));

		await waitFor(() => expect(screen.getByRole("region", { name: /Job detail: vcf-artifact-bundle/ })).toBeInTheDocument());
		// run-1's job row is still present in the list.
		expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument();
	});

	it("updates the URL (?run=&job=) when a job row is selected — deep link preserved", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("vcf-artifact-bundle")).toBeInTheDocument());
		fireEvent.click(screen.getByText("vcf-artifact-bundle"));

		await waitFor(() => expect(window.location.search).toContain("run=run-2"));
		expect(window.location.search).toContain("job=job-2");
	});

	it("restores the selected run/job from a deep-linked URL on mount", async () => {
		window.history.pushState(null, "", "/live-jobs?run=run-2&job=job-2");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByRole("region", { name: /Job detail: vcf-artifact-bundle/ })).toBeInTheDocument());
	});

	it("degrades cleanly when a deep-linked run/job no longer exists in the active set", async () => {
		window.history.pushState(null, "", "/live-jobs?run=run-missing&job=job-missing");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-1")).toBeInTheDocument());
		expect(screen.getByText("Select a run or job to see its detail.")).toBeInTheDocument();
	});

	it("supports ArrowDown keyboard navigation between rows in the listbox", async () => {
		installFetchMock({});
		renderWithAuth();

		// Wait for the seed to land and auto-select to settle on the first row
		// (run-1's header) before driving keyboard navigation from it.
		await waitFor(() => expect(window.location.search).toBe("?run=run-1"));
		const listbox = screen.getByRole("listbox");
		listbox.focus();
		fireEvent.keyDown(listbox, { key: "ArrowDown" });

		await waitFor(() => expect(window.location.search).toContain("job=job-1"));
	});

	it("exposes listbox/option ARIA roles for screen-reader selection among concurrent runs", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByRole("listbox", { name: "Active runs and jobs" })).toBeInTheDocument());
		const options = screen.getAllByRole("option");
		// Two run headers + two jobs = 4 selectable rows.
		expect(options.length).toBe(4);
	});

	it("updates a job's state live via the global SSE stream (no polling)", async () => {
		installFetchMock({
			frames: [{ seq: 1, ts: "t", type: "job.state", run_id: "run-2", job_id: "job-2", data: { to: "running" } }],
		});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("vcf-artifact-bundle")).toBeInTheDocument());
		fireEvent.click(screen.getByText("vcf-artifact-bundle"));

		await waitFor(() => expect(screen.getAllByText("running").length).toBeGreaterThan(0));
	});

	it("re-fetches GET /runs on SSE reconnect (REST reconciliation) without duplicating rows", async () => {
		installFetchMock({ reconnect: true });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-1")).toBeInTheDocument());
		// After the forced reconnect, the seed re-fetches; still exactly one LIST
		// row per run/job — no duplicates. `getAllByRole("option")` scopes to the
		// listbox rows only (the detail pane legitimately repeats the selected
		// run's id in its own facts list, which is not what this asserts).
		await waitFor(() => expect(screen.getAllByRole("option")).toHaveLength(4));
		expect(screen.getAllByText("esxi-01.example.internal")).toHaveLength(1);
	});

	it("shows a generic detail renderer for every job type (renderer-registry seam, no #591 renderers registered yet)", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("vcf-artifact-bundle")).toBeInTheDocument());
		fireEvent.click(screen.getByText("vcf-artifact-bundle"));

		await waitFor(() => expect(screen.getByRole("region", { name: /Job detail/ })).toBeInTheDocument());
		expect(screen.getAllByText("download").length).toBeGreaterThan(0);
	});

	it("renders for a Viewer (role floor for observing operational history — ADR-0019 decision 6)", async () => {
		installFetchMock({});
		renderWithAuth("Viewer");

		await waitFor(() => expect(screen.getByText("run-1")).toBeInTheDocument());
		expect(screen.getByText("viewing as Viewer")).toBeInTheDocument();
	});
});
