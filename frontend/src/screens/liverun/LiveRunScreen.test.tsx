import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import type { RunListItem } from "../results/results";
import { LiveRunScreen } from "./LiveRunScreen";

/**
 * Issue #494: fixtures below are the REAL wire shapes — `RunResponse`/
 * `JobResponse` (backend/Waypoint.Api/Contracts/RunContracts.cs), exactly as
 * `RunsController` serializes them, not the fictional `RunHeader`/`RunJob`
 * view model `liverun.ts` maps them into. Mocking the view model directly (as
 * this suite did before #494) is exactly the drift that let
 * `LiveRunScreen`'s "e.queues is not iterable" crash ship unnoticed: every
 * unit test passed against a contract no real backend ever sends.
 */
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

interface JobWireFixture {
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

const RUN_WIRE: RunWireFixture = {
	id: "run-0808-0100Z",
	run_type: "scan",
	state: "running",
	paused: false,
	blocked: false,
	blocked_reason: null,
	scope: '{"site_id":"11111111-1111-1111-1111-111111111111"}',
	credential_id: "22222222-2222-2222-2222-222222222222",
	initiated_by: "j.moreno",
	created_at: "2026-08-08T01:00:00Z",
	started_at: "2026-08-08T01:00:00Z",
	completed_at: null,
	job_count: 2,
	job_count_queued: 2,
	job_count_running: 0,
	job_count_completed: 0,
	job_count_failed: 0,
	job_count_blocked: 0,
};

const JOB_WIRE: JobWireFixture[] = [
	{
		id: "j-1",
		run_id: "run-0808-0100Z",
		job_type: "scan",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "queued",
		stage: null,
		priority: 4,
		attempt_count: 0,
		created_at: "2026-08-08T01:00:00Z",
		started_at: null,
		finished_at: null,
	},
	{
		id: "j-2",
		run_id: "run-0808-0100Z",
		job_type: "scan",
		target_id: "target-2",
		target_name: "esxi-02.example.internal",
		state: "queued",
		stage: null,
		priority: 4,
		attempt_count: 0,
		created_at: "2026-08-08T01:00:00Z",
		started_at: null,
		finished_at: null,
	},
];

/** Frames that would be emitted for run-0808-0100Z, in commit (seq) order. */
const FRAMES = [
	{ seq: 1, job_id: "j-1", to: "running" },
	{ seq: 2, job_id: "j-1", to: "attesting" },
	{ seq: 3, job_id: "j-2", to: "running" },
	{ seq: 4, job_id: "j-1", to: "converting" },
	{ seq: 5, job_id: "j-1", to: "uploaded" },
];

function frameText(f: (typeof FRAMES)[number]): string {
	const envelope = {
		seq: f.seq,
		ts: "2026-08-08T01:00:00Z",
		type: "job.state",
		run_id: "run-0808-0100Z",
		job_id: f.job_id,
		data: { to: f.to },
	};
	return `id: ${f.seq}\ndata: ${JSON.stringify(envelope)}\n\n`;
}

/** What one SSE connection attempt should deliver. `frames` are written as a
 * single chunk; `close: true` makes the reader report a clean stream end after
 * that chunk, which drives `connectEventStream` to reconnect and send
 * `Last-Event-ID` (mirroring events.test.ts's `endAfterChunk`). `close: false`
 * (default) keeps a healthy long-lived stream open (settles only on abort). */
interface SseAttempt {
	frames: (typeof FRAMES)[number][];
	close?: boolean;
}

/**
 * Mocks `fetch` for the REST seed + SSE stream. `sseAttempt(lastEventId)` is
 * called once per SSE connection attempt and decides, from the client's
 * `Last-Event-ID` header, what that connection replays and whether it then
 * closes (forcing a Last-Event-ID reconnect). Live-only tests keep one stream
 * open; the reload/replay test delivers a prefix, closes, and replays the tail
 * keyed off the header — both converge on the same board via the same
 * `applyEvent` reducer (LiveRunScreen reads through useLiveRun).
 */
function installFetchMock(sseAttempt: (lastEventId: string | null) => SseAttempt) {
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const accept = new Headers(init?.headers).get("Accept");

		if (url === "/api/v1/runs/run-0808-0100Z" && accept !== "text/event-stream") {
			return new Response(JSON.stringify(RUN_WIRE), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
			return new Response(JSON.stringify(JOB_WIRE), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url === "/api/v1/runs/run-0808-0100Z/events") {
			const lastEventId = new Headers(init?.headers).get("Last-Event-ID");
			const attempt = sseAttempt(lastEventId);
			const encoder = new TextEncoder();
			const chunk = encoder.encode(attempt.frames.map(frameText).join(""));
			let sent = false;
			const signal = init?.signal;
			const body = {
				getReader() {
					return {
						read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
							if (!sent) {
								sent = true;
								return Promise.resolve({ value: chunk, done: false });
							}
							if (attempt.close) {
								// Server closed the stream cleanly → connectEventStream
								// reconnects and re-requests with Last-Event-ID.
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
			return { ok: true, status: 200, body } as unknown as Response;
		}
		if (url === "/api/v1/auth/me") {
			return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithAuth(runId: string, role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({
			token: "tok",
			username: "j.moreno",
			role,
			expiresAt: new Date(Date.now() + 3600_000).toISOString(),
		}),
	);
	return render(
		<AuthProvider>
			<RouterProvider>
				<LiveRunScreen runId={runId} />
			</RouterProvider>
		</AuthProvider>,
	);
}

describe("LiveRunScreen (issue #283)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		sessionStorage.clear();
	});

	it("renders the seeded board immediately from GET /runs/{id} and /runs/{id}/jobs", async () => {
		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z");

		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());
		expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("esxi-02.example.internal")).toBeInTheDocument();
	});

	/** Issue #707 (epic #706): the console links back to the global Jobs
	 * workspace (renamed from "Live Jobs" by #708) and out to Compliance Scan
	 * Results for the same run — the bidirectional linkage the epic requires. */
	it("links back to Jobs and out to Compliance Scan Results for this run", async () => {
		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z");

		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());

		const backLink = screen.getByRole("link", { name: "← Jobs" });
		expect(backLink).toHaveAttribute("href", "/live-jobs");

		const resultsLink = screen.getByRole("link", { name: "View in Compliance Scan Results →" });
		expect(resultsLink).toHaveAttribute("href", "/results?run=run-0808-0100Z");
	});

	it("walks a target's state purely from live SSE events (no polling)", async () => {
		installFetchMock((lastEventId) => ({ frames: lastEventId ? [] : FRAMES }));
		renderWithAuth("run-0808-0100Z");

		await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());

		// j-1 walks queued -> running -> attesting -> converting -> uploaded;
		// j-2 only reaches running. Both come from the event stream alone.
		await waitFor(() => {
			const row = screen.getByText("esxi-01.example.internal").closest("tr");
			expect(row).toHaveTextContent("uploaded");
		});
		const row2 = screen.getByText("esxi-02.example.internal").closest("tr");
		expect(row2).toHaveTextContent("running");
	});

	it("Last-Event-ID replay after a reload reproduces the same board the live stream would have (AC2)", async () => {
		// The header's elapsed-time readout is computed from Date.now() at the
		// moment each mount's REST seed is mapped (liverun.ts's
		// elapsedSecondsFrom). Pin the clock so the first mount and the
		// "reload" second mount land on the identical elapsed second — real
		// timers stay in effect (only Date.now is stubbed) so the SSE
		// reconnect's backoff `setTimeout` below still elapses for real, which
		// the 3000ms waitFor timeout below depends on.
		const nowSpy = vi.spyOn(Date, "now").mockReturnValue(Date.parse(RUN_WIRE.started_at as string) + 90_000);

		// First mount = the fully-live board: one long-lived stream delivers all
		// five frames from the start and never reconnects. This is the reference
		// board we require the replay path to reconstruct.
		installFetchMock((lastEventId) => ({ frames: lastEventId ? [] : FRAMES }));
		const first = renderWithAuth("run-0808-0100Z");
		await waitFor(() => {
			const row = screen.getByText("esxi-01.example.internal").closest("tr");
			expect(row).toHaveTextContent("uploaded");
		});
		const liveBoardHtml = first.container.innerHTML;
		first.unmount();

		// "Reload": a fresh mount whose REST seed is caught up only through
		// seq 2, so the board must be reconstructed by REPLAYING the tail. The
		// first SSE connection delivers the seq 1-2 prefix then CLOSES the
		// stream — this drives connectEventStream to reconnect and re-request
		// with `Last-Event-ID: 2` (its last-seen id). The reconnect then replays
		// exactly the WHERE seq > last_event_id tail (seq 3-5). No frame is ever
		// delivered without going through a Last-Event-ID reconnect, so this
		// fails if the reconnect/replay path breaks — it is not a fresh live run.
		const seenLastEventIds: (string | null)[] = [];
		installFetchMock((lastEventId) => {
			seenLastEventIds.push(lastEventId);
			if (lastEventId === null) {
				// Initial connect: deliver the prefix, then close to force a
				// Last-Event-ID reconnect.
				return { frames: FRAMES.slice(0, 2), close: true };
			}
			// Reconnect: server replays strictly the tail after Last-Event-ID.
			const last = Number(lastEventId);
			return { frames: FRAMES.filter((f) => f.seq > last) };
		});
		const second = renderWithAuth("run-0808-0100Z");
		await waitFor(
			() => {
				const row = screen.getByText("esxi-01.example.internal").closest("tr");
				expect(row).toHaveTextContent("uploaded");
			},
			// connectEventStream reconnects after ~minBackoff (1s default) once the
			// first stream closes cleanly; allow for that before the tail replays.
			{ timeout: 3000 },
		);
		// The board was rebuilt from seed + a genuine Last-Event-ID reconnect...
		expect(seenLastEventIds).toContain("2");
		// ...and folds through the same reducer to the identical fully-live board.
		expect(second.container.innerHTML).toBe(liveBoardHtml);
		second.unmount();
		nowSpy.mockRestore();
	});

	it("enables Pause/Abort for Operator+ and gates them with the role reason for Viewer/Cyber (#285)", async () => {
		installFetchMock(() => ({ frames: [] }));
		const admin = renderWithAuth("run-0808-0100Z", "Admin");
		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());
		expect(screen.getByRole("button", { name: "Pause queue" })).not.toBeDisabled();
		expect(screen.getByRole("button", { name: "Abort run" })).not.toBeDisabled();
		admin.unmount();

		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z", "Viewer");
		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());
		const pause = screen.getByRole("button", { name: "Pause queue" });
		const abort = screen.getByRole("button", { name: "Abort run" });
		expect(pause).toBeDisabled();
		expect(abort).toBeDisabled();
		// The title explains the ROLE reason now that the controls are real,
		// not the old "#285 not built yet" placeholder reason.
		expect(pause).toHaveAttribute("title", expect.stringMatching(/Requires|role/i));
		expect(pause.getAttribute("title")).not.toMatch(/#285/);
	});

	it("Pause calls POST /runs/{id}/pause and Abort confirms before calling POST .../abort", async () => {
		const calls: string[] = [];
		installFetchMock(() => ({ frames: [] }));
		const baseFetch = globalThis.fetch;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/runs/run-0808-0100Z/pause" && init?.method === "POST") {
				calls.push("pause");
				return new Response(JSON.stringify({ id: "run-0808-0100Z", state: "running" }), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/abort" && init?.method === "POST") {
				calls.push("abort");
				return new Response(JSON.stringify({ id: "run-0808-0100Z", state: "aborted" }), { status: 200 });
			}
			return baseFetch(input, init);
		}) as unknown as typeof fetch;

		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
		renderWithAuth("run-0808-0100Z", "Admin");
		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());

		screen.getByRole("button", { name: "Pause queue" }).click();
		await waitFor(() => expect(calls).toContain("pause"));

		screen.getByRole("button", { name: "Abort run" }).click();
		expect(confirmSpy).toHaveBeenCalled();
		await waitFor(() => expect(calls).toContain("abort"));
		confirmSpy.mockRestore();
	});

	/** Shared blocked-run fixture: seeds a halted queue (README screen 1's
	 * "HALTED — credential failure" thread) with an SSE stream that never
	 * delivers (this suite only exercises the REST-seeded halt render). */
	function installBlockedFetchMock(extra?: (url: string, init?: RequestInit) => Response | undefined) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const extraResponse = extra?.(url, init);
			if (extraResponse) {
				return extraResponse;
			}
			if (url === "/api/v1/runs/run-0808-0100Z" && new Headers(init?.headers).get("Accept") !== "text/event-stream") {
				const blockedWire: RunWireFixture = { ...RUN_WIRE, blocked: true, blocked_reason: "credential failure" };
				return new Response(JSON.stringify(blockedWire), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
				return new Response(JSON.stringify(JOB_WIRE), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/events") {
				return {
					ok: true,
					status: 200,
					body: {
						getReader: () => ({
							read: () => new Promise(() => {}),
							releaseLock() {},
						}),
					},
				} as unknown as Response;
			}
			throw new Error(`Unhandled fetch: ${url}`);
		}) as unknown as typeof fetch;
	}

	it("shows the blocked banner and a disabled resume control for a non-Admin role", async () => {
		installBlockedFetchMock();

		// Viewer, not Admin — README "Roles & Permissions": credential-swap-resume
		// is Admin only, and the disabled control must still render (visible but
		// disabled), not disappear. No credential picker for a role that can
		// never use it.
		renderWithAuth("run-0808-0100Z", "Viewer");
		await waitFor(() => expect(screen.getByText(/Queue halted/)).toBeInTheDocument());
		const resumeButton = screen.getByRole("button", { name: /Change credential/ });
		expect(resumeButton).toBeDisabled();
		expect(resumeButton).toHaveAttribute("title", expect.stringMatching(/Admin/));
		expect(screen.queryByLabelText("Replacement credential")).not.toBeInTheDocument();
	});

	it("Admin swap-resume: picking a credential and confirming calls POST /runs/{id}/resume-blocked with its id", async () => {
		const resumeCalls: unknown[] = [];
		installBlockedFetchMock((url, init) => {
			if (url === "/api/v1/credentials" && init?.method === "GET") {
				return new Response(
					JSON.stringify([
						{
							id: "cred-1",
							name: "svc-stig-scan-2",
							credential_type: "ssh",
							owner: "shared",
							health: "unknown",
							sudo_enabled: false,
							has_secret: true,
							used_by_job_count: 0,
							created_at: "2026-08-01T00:00:00Z",
							updated_at: "2026-08-01T00:00:00Z",
						},
					]),
					{ status: 200 },
				);
			}
			if (url === "/api/v1/runs/run-0808-0100Z/resume-blocked" && init?.method === "POST") {
				resumeCalls.push(init.body ? JSON.parse(init.body as string) : null);
				return new Response(JSON.stringify({ id: "run-0808-0100Z", state: "running" }), { status: 200 });
			}
			return undefined;
		});

		renderWithAuth("run-0808-0100Z", "Admin");
		await waitFor(() => expect(screen.getByText(/Queue halted/)).toBeInTheDocument());

		const select = await screen.findByLabelText("Replacement credential");
		await waitFor(() => expect(screen.getByText("svc-stig-scan-2")).toBeInTheDocument());

		const resumeButton = screen.getByRole("button", { name: /Change credential/ });
		// Nothing selected yet: resume stays disabled even for Admin.
		expect(resumeButton).toBeDisabled();

		(select as HTMLSelectElement).value = "cred-1";
		select.dispatchEvent(new Event("change", { bubbles: true }));

		await waitFor(() => expect(resumeButton).not.toBeDisabled());
		resumeButton.click();

		await waitFor(() => expect(resumeCalls).toEqual([{ credential_id: "cred-1" }]));
	});

	it("per-job cancel: confirming calls DELETE /jobs/{id} for that job only", async () => {
		const deleteCalls: string[] = [];
		installFetchMock(() => ({ frames: [] }));
		const baseFetch = globalThis.fetch;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/jobs/j-1" && init?.method === "DELETE") {
				deleteCalls.push(url);
				return new Response(null, { status: 204 });
			}
			return baseFetch(input, init);
		}) as unknown as typeof fetch;

		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
		renderWithAuth("run-0808-0100Z", "Admin");
		await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());

		screen.getByRole("button", { name: "Cancel esxi-01.example.internal" }).click();
		await waitFor(() => expect(deleteCalls).toEqual(["/api/v1/jobs/j-1"]));
		// The sibling job's cancel control is untouched — no DELETE for j-2.
		expect(deleteCalls).not.toContain("/api/v1/jobs/j-2");
		confirmSpy.mockRestore();
	});

	it("per-job cancel is gated for Viewer with the role reason, same convention as run controls", async () => {
		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z", "Viewer");
		await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());

		const cancelButton = screen.getByRole("button", { name: "Cancel esxi-01.example.internal" });
		expect(cancelButton).toBeDisabled();
		expect(cancelButton).toHaveAttribute("title", expect.stringMatching(/Requires|role/i));
	});

	it("renders a job's failure note from job.log SSE (README failure story: convert/attest failure notes)", async () => {
		const FAILURE_NOTE = "hdf→ckl failed — control V-259142 has no matching rule id in V1R2";
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const accept = new Headers(init?.headers).get("Accept");
			if (url === "/api/v1/runs/run-0808-0100Z" && accept !== "text/event-stream") {
				return new Response(JSON.stringify(RUN_WIRE), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
				return new Response(JSON.stringify(JOB_WIRE), { status: 200 });
			}
			if (url === "/api/v1/auth/me") {
				return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/events") {
				const encoder = new TextEncoder();
				const stateEnvelope = {
					seq: 1,
					ts: "2026-08-08T01:00:00Z",
					type: "job.state",
					run_id: "run-0808-0100Z",
					job_id: "j-1",
					data: { to: "failed" },
				};
				const logEnvelope = {
					seq: 2,
					ts: "2026-08-08T01:00:01Z",
					type: "job.log",
					run_id: "run-0808-0100Z",
					job_id: "j-1",
					data: { line: FAILURE_NOTE },
				};
				const chunk = encoder.encode(
					`id: 1\ndata: ${JSON.stringify(stateEnvelope)}\n\nid: 2\ndata: ${JSON.stringify(logEnvelope)}\n\n`,
				);
				let sent = false;
				const signal = init?.signal;
				return {
					ok: true,
					status: 200,
					body: {
						getReader: () => ({
							read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
								if (!sent) {
									sent = true;
									return Promise.resolve({ value: chunk, done: false });
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
						}),
					},
				} as unknown as Response;
			}
			throw new Error(`Unhandled fetch: ${url}`);
		}) as unknown as typeof fetch;

		renderWithAuth("run-0808-0100Z", "Admin");
		await waitFor(() => {
			const row = screen.getByText("esxi-01.example.internal").closest("tr");
			expect(row).toHaveTextContent(FAILURE_NOTE);
		});
	});

	describe("40-target scale render (#289 — AC-1 was only proven at 2 targets)", () => {
		/** README screen 1's five priority queues: "P1 NSX MANAGERS", "P2 VCSA
		 * COMPONENTS", "P3 VCENTER APPLIANCES", "P4 ESXI HOSTS", "P5 GUEST / SSH
		 * TARGETS" — 8 invented targets per queue, 40 total, mirroring the
		 * prototype's "~40 targets in priority queues" spec instead of the
		 * 2-target fixture the rest of this suite uses for readability.
		 *
		 * Issue #494: the real `JobResponse` carries only `priority` (a bare
		 * short), not a named queue/benchmark — `liverun.ts`'s `mapRunJob`
		 * synthesizes the `p{priority}` queue grouping the board renders, so
		 * this fixture only needs a `key`/`priority` pair per group; `name`/
		 * `benchmark` are no longer wire fields.
		 */
		const SCALE_QUEUES: { key: string; priority: number }[] = [
			{ key: "nsx", priority: 1 },
			{ key: "vcsa", priority: 2 },
			{ key: "vcenter", priority: 3 },
			{ key: "esxi", priority: 4 },
			{ key: "guest", priority: 5 },
		];

		const TARGETS_PER_QUEUE = 8;
		const SCALE_JOB_COUNT = SCALE_QUEUES.length * TARGETS_PER_QUEUE; // 40

		function buildScaleJobs(): JobWireFixture[] {
			const jobs: JobWireFixture[] = [];
			for (const queue of SCALE_QUEUES) {
				for (let i = 0; i < TARGETS_PER_QUEUE; i++) {
					const n = String(i).padStart(2, "0");
					jobs.push({
						id: `${queue.key}-job-${n}`,
						run_id: "run-0808-0200Z",
						job_type: "scan",
						target_id: `${queue.key}-target-${n}`,
						target_name: `${queue.key}-${n}.example.internal`,
						state: "queued",
						stage: null,
						priority: queue.priority,
						attempt_count: 0,
						created_at: "2026-08-08T02:00:00Z",
						started_at: null,
						finished_at: null,
					});
				}
			}
			return jobs;
		}

		const SCALE_JOBS = buildScaleJobs();
		const SCALE_HEADER: RunWireFixture = {
			id: "run-0808-0200Z",
			run_type: "scan",
			state: "running",
			paused: false,
			blocked: false,
			blocked_reason: null,
			scope: '{"site_id":"11111111-1111-1111-1111-111111111111"}',
			credential_id: "22222222-2222-2222-2222-222222222222",
			initiated_by: "j.moreno",
			created_at: "2026-08-08T02:00:00Z",
			started_at: "2026-08-08T02:00:00Z",
			completed_at: null,
			job_count: SCALE_JOB_COUNT,
			job_count_queued: SCALE_JOB_COUNT,
			job_count_running: 0,
			job_count_completed: 0,
			job_count_failed: 0,
			job_count_blocked: 0,
		};

		/** A representative subset of transitions, not all 40 jobs walking the
		 * full state machine — per #289's scope note, driving a batch of SSE
		 * frames through the same reducer is sufficient to pin the live-render
		 * claim without making the test slow. Two full walks to a terminal
		 * state (one uploaded, one failed), several mid-flight advances spread
		 * across queues, and the rest left at their seeded "queued" state. */
		function buildScaleFrames() {
			const frames: { seq: number; job_id: string; to: string }[] = [];
			let seq = 1;

			// nsx-00 walks all the way to uploaded.
			for (const to of ["running", "attesting", "converting", "uploaded"]) {
				frames.push({ seq: seq++, job_id: "nsx-job-00", to });
			}
			// vcsa-03 fails at convert (mirrors the README's convert-failure story).
			for (const to of ["running", "attesting", "converting", "failed"]) {
				frames.push({ seq: seq++, job_id: "vcsa-job-03", to });
			}
			// One in-flight advance per remaining queue so all five queues show
			// live movement, not just the two fully-walked jobs.
			frames.push({ seq: seq++, job_id: "vcenter-job-01", to: "running" });
			frames.push({ seq: seq++, job_id: "esxi-job-02", to: "running" });
			frames.push({ seq: seq++, job_id: "esxi-job-05", to: "attesting" });
			frames.push({ seq: seq++, job_id: "guest-job-07", to: "running" });

			return frames;
		}

		function scaleFrameText(f: { seq: number; job_id: string; to: string }): string {
			const envelope = {
				seq: f.seq,
				ts: "2026-08-08T02:00:00Z",
				type: "job.state",
				run_id: SCALE_HEADER.id,
				job_id: f.job_id,
				data: { to: f.to },
			};
			return `id: ${f.seq}\ndata: ${JSON.stringify(envelope)}\n\n`;
		}

		function progressFrameText(seq: number) {
			const envelope = {
				seq,
				ts: "2026-08-08T02:00:01Z",
				type: "run.progress",
				run_id: SCALE_HEADER.id,
				data: { pass: 412, fail: 3, na: 8, percent: 12, completed_count: 1, elapsed_seconds: 96 },
			};
			return `id: ${seq}\ndata: ${JSON.stringify(envelope)}\n\n`;
		}

		function installScaleFetchMock(frames: { seq: number; job_id: string; to: string }[], extraFrameText?: string) {
			globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
				const url = typeof input === "string" ? input : input.toString();
				const accept = new Headers(init?.headers).get("Accept");
				if (url === `/api/v1/runs/${SCALE_HEADER.id}` && accept !== "text/event-stream") {
					return new Response(JSON.stringify(SCALE_HEADER), { status: 200, headers: { "Content-Type": "application/json" } });
				}
				if (url === `/api/v1/runs/${SCALE_HEADER.id}/jobs`) {
					return new Response(JSON.stringify(SCALE_JOBS), { status: 200, headers: { "Content-Type": "application/json" } });
				}
				if (url === `/api/v1/runs/${SCALE_HEADER.id}/events`) {
					const encoder = new TextEncoder();
					const body = frames.map(scaleFrameText).join("") + (extraFrameText ?? "");
					const chunk = encoder.encode(body);
					let sent = false;
					const signal = init?.signal;
					return {
						ok: true,
						status: 200,
						body: {
							getReader: () => ({
								read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
									if (!sent) {
										sent = true;
										return Promise.resolve({ value: chunk, done: false });
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
							}),
						},
					} as unknown as Response;
				}
				if (url === "/api/v1/auth/me") {
					return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
				}
				throw new Error(`Unhandled fetch in test: ${url}`);
			}) as unknown as typeof fetch;
		}

		it("renders all 40 target rows live from the SSE-driven reducer, with counters reflecting the 40 jobs (AC-1)", async () => {
			const frames = buildScaleFrames();
			const lastSeq = frames[frames.length - 1].seq + 1;
			installScaleFetchMock(frames, progressFrameText(lastSeq));
			renderWithAuth(SCALE_HEADER.id);

			await waitFor(() => expect(screen.getByText(SCALE_HEADER.id)).toBeInTheDocument());

			// All 40 target rows are present — the crux of AC-1's "40-target run
			// renders live" claim, not extrapolated from 2.
			await waitFor(() => {
				for (const job of SCALE_JOBS) {
					expect(screen.getByText(job.target_name!)).toBeInTheDocument();
				}
			});
			expect(screen.getAllByRole("row").length).toBeGreaterThanOrEqual(SCALE_JOB_COUNT);

			// The run-level header reflects the full target count (not just the
			// jobs that got an SSE frame in this test).
			expect(screen.getByText(new RegExp(`${SCALE_JOB_COUNT} targets`))).toBeInTheDocument();

			// Per-target transitions from the SSE batch landed on the right rows...
			await waitFor(() => {
				const row = screen.getByText("nsx-00.example.internal").closest("tr");
				expect(row).toHaveTextContent("uploaded");
			});
			expect(screen.getByText("vcsa-03.example.internal").closest("tr")).toHaveTextContent("failed");
			expect(screen.getByText("vcenter-01.example.internal").closest("tr")).toHaveTextContent("running");
			expect(screen.getByText("esxi-02.example.internal").closest("tr")).toHaveTextContent("running");
			expect(screen.getByText("esxi-05.example.internal").closest("tr")).toHaveTextContent("attesting");
			expect(screen.getByText("guest-07.example.internal").closest("tr")).toHaveTextContent("running");

			// ...and every job this batch never touched is still at its seeded
			// "queued" state — the reducer didn't smear a transition across rows.
			expect(screen.getByText("nsx-01.example.internal").closest("tr")).toHaveTextContent("queued");
			expect(screen.getByText("esxi-00.example.internal").closest("tr")).toHaveTextContent("queued");
			expect(screen.getByText("guest-00.example.internal").closest("tr")).toHaveTextContent("queued");

			// run.progress counters folded from SSE, not extrapolated/derived locally.
			await waitFor(() => {
				expect(screen.getByText("412")).toBeInTheDocument(); // PASS
				expect(screen.getByText("3")).toBeInTheDocument(); // FAIL
				expect(screen.getByText("8")).toBeInTheDocument(); // N/A
			});

			// State board layout also carries the full 40-job set (buildBoardColumns
			// operates over the same jobs array the queue layout renders).
			screen.getByRole("button", { name: "State board" }).click();
			await waitFor(() => expect(document.querySelector(".live-run__board")).toBeInTheDocument());
			for (const job of SCALE_JOBS) {
				expect(screen.getByText(job.target_name!)).toBeInTheDocument();
			}
		});

		it("a queue halting at 40-target scale still renders the HALTED status and blocked banner (AC-1 queue halts)", async () => {
			// Issue #494: the real `queue.state`/`RunResponse.blocked` signal is
			// run-level (a credential halt on the shared job queue), not scoped
			// to one named priority queue the way the original fictional
			// contract modeled it — a halted run therefore reads as every
			// synthesized queue halting together, mirroring the README's
			// "P5 guest queue halts after three consecutive svc-stig-vm SSH auth
			// failures" story at the full 40-target scale, but honestly reflecting
			// that the halt is whole-run.
			const blockedWire: RunWireFixture = { ...SCALE_HEADER, blocked: true, blocked_reason: "credential failure" };
			globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
				const url = typeof input === "string" ? input : input.toString();
				const accept = new Headers(init?.headers).get("Accept");
				if (url === `/api/v1/runs/${SCALE_HEADER.id}` && accept !== "text/event-stream") {
					return new Response(JSON.stringify(blockedWire), { status: 200 });
				}
				if (url === `/api/v1/runs/${SCALE_HEADER.id}/jobs`) {
					return new Response(JSON.stringify(SCALE_JOBS), { status: 200 });
				}
				if (url === `/api/v1/runs/${SCALE_HEADER.id}/events`) {
					return {
						ok: true,
						status: 200,
						body: { getReader: () => ({ read: () => new Promise(() => {}), releaseLock() {} }) },
					} as unknown as Response;
				}
				if (url === "/api/v1/auth/me") {
					return new Response(JSON.stringify({ username: "j.moreno", role: "Viewer" }), { status: 200 });
				}
				throw new Error(`Unhandled fetch: ${url}`);
			}) as unknown as typeof fetch;

			renderWithAuth(SCALE_HEADER.id, "Viewer");
			await waitFor(() => expect(screen.getByText(/Queue halted/)).toBeInTheDocument());
			// Every synthesized queue reflects the run-level halt (all 5 queues,
			// not just one — see the run-level `queue.state` note above), while
			// all 40 target rows still render alongside it.
			expect(screen.getAllByText(/HALTED — credential failure/).length).toBe(SCALE_QUEUES.length);
			for (const job of SCALE_JOBS) {
				expect(screen.getByText(job.target_name!)).toBeInTheDocument();
			}
		});
	});

	describe("log-first layout (#287)", () => {
		it("the layout switcher offers a third Log-first option alongside Priority queues and State board", async () => {
			installFetchMock(() => ({ frames: [] }));
			renderWithAuth("run-0808-0100Z");
			await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());

			expect(screen.getByRole("button", { name: "Priority queues" })).toBeInTheDocument();
			expect(screen.getByRole("button", { name: "State board" })).toBeInTheDocument();
			expect(screen.getByRole("button", { name: "Log-first" })).toBeInTheDocument();
		});

		it("selecting Log-first renders the narrow target list and a full-height log pane, and switching between all three modes works", async () => {
			installFetchMock(() => ({ frames: [] }));
			renderWithAuth("run-0808-0100Z");
			await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());

			// Default is Priority queues: grouped table rows, no dedicated log pane.
			expect(document.querySelector(".live-run__queues")).toBeInTheDocument();
			expect(document.querySelector(".live-run__log-first")).not.toBeInTheDocument();

			screen.getByRole("button", { name: "State board" }).click();
			await waitFor(() => expect(document.querySelector(".live-run__board")).toBeInTheDocument());
			expect(document.querySelector(".live-run__log-first")).not.toBeInTheDocument();

			screen.getByRole("button", { name: "Log-first" }).click();
			await waitFor(() => expect(document.querySelector(".live-run__log-first")).toBeInTheDocument());
			expect(document.querySelector(".live-run__log-first-targets")).toBeInTheDocument();
			expect(document.querySelector(".live-run__log-first-log")).toBeInTheDocument();
			// Same per-target data the other two layouts render.
			expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument();
			expect(screen.getByText("esxi-02.example.internal")).toBeInTheDocument();
			// No log lines have arrived yet.
			expect(screen.getByText("No log lines yet.")).toBeInTheDocument();

			screen.getByRole("button", { name: "Priority queues" }).click();
			await waitFor(() => expect(document.querySelector(".live-run__queues")).toBeInTheDocument());
			expect(document.querySelector(".live-run__log-first")).not.toBeInTheDocument();
		});

		it("the log pane renders live job.log lines while in log-first mode", async () => {
			const LOG_LINE = "InSpec control scan starting — 412 controls";
			globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
				const url = typeof input === "string" ? input : input.toString();
				const accept = new Headers(init?.headers).get("Accept");
				if (url === "/api/v1/runs/run-0808-0100Z" && accept !== "text/event-stream") {
					return new Response(JSON.stringify(RUN_WIRE), { status: 200 });
				}
				if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
					return new Response(JSON.stringify(JOB_WIRE), { status: 200 });
				}
				if (url === "/api/v1/auth/me") {
					return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
				}
				if (url === "/api/v1/runs/run-0808-0100Z/events") {
					const encoder = new TextEncoder();
					const logEnvelope = {
						seq: 1,
						ts: "2026-08-08T01:00:00Z",
						type: "job.log",
						run_id: "run-0808-0100Z",
						job_id: "j-1",
						data: { target: "esxi-01.example.internal", line: LOG_LINE },
					};
					const chunk = encoder.encode(`id: 1\ndata: ${JSON.stringify(logEnvelope)}\n\n`);
					let sent = false;
					const signal = init?.signal;
					return {
						ok: true,
						status: 200,
						body: {
							getReader: () => ({
								read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
									if (!sent) {
										sent = true;
										return Promise.resolve({ value: chunk, done: false });
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
							}),
						},
					} as unknown as Response;
				}
				throw new Error(`Unhandled fetch: ${url}`);
			}) as unknown as typeof fetch;

			renderWithAuth("run-0808-0100Z", "Admin");
			await waitFor(() => expect(screen.getByText("esxi-01.example.internal")).toBeInTheDocument());

			screen.getByRole("button", { name: "Log-first" }).click();
			await waitFor(() => expect(document.querySelector(".live-run__log-first-log")).toBeInTheDocument());
			await waitFor(() => expect(screen.getByText(LOG_LINE)).toBeInTheDocument());
			// The target list still renders alongside the log pane in the same mode
			// (the target name now appears twice: the list row and the log line's tag).
			expect(screen.getAllByText("esxi-01.example.internal").length).toBeGreaterThanOrEqual(2);
		});
	});

	/**
	 * Issue #711 (regression against #714): making `/live-run` a permanent
	 * top-level nav entry means the screen is reachable with no `?run=` at
	 * all. `LiveRunScreen({ runId: undefined })` must present a useful
	 * default — an active-run picker backed by the same `GET /runs` list
	 * `results/useRunList.ts` already fetches — never the old bare "No run
	 * selected." dead end.
	 */
	describe("no-runId default state (#711)", () => {
		const ACTIVE_RUN: RunListItem = {
			id: "run-0826-0900Z",
			run_type: "scan",
			state: "running",
			scope: '{"site_id":"33333333-3333-3333-3333-333333333333"}',
			initiated_by: "j.moreno",
			created_at: "2026-08-26T09:00:00Z",
			started_at: "2026-08-26T09:00:00Z",
			completed_at: null,
			job_count: 4,
			job_count_queued: 4,
			job_count_running: 0,
			job_count_completed: 0,
			job_count_failed: 0,
			job_count_blocked: 0,
		};

		const COMPLETED_RUN: RunListItem = {
			...ACTIVE_RUN,
			id: "run-0825-0100Z",
			state: "completed",
			completed_at: "2026-08-25T02:00:00Z",
		};

		const DOWNLOAD_RUN: RunListItem = {
			...ACTIVE_RUN,
			id: "run-download-01",
			run_type: "download",
			state: "running",
		};

		function installRunListFetchMock(runList: RunListItem[]) {
			globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
				const url = typeof input === "string" ? input : input.toString();
				if (url.startsWith("/api/v1/runs?")) {
					return new Response(JSON.stringify(runList), { status: 200 });
				}
				if (url === "/api/v1/auth/me") {
					return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
				}
				throw new Error(`Unhandled fetch in test: ${url}`);
			}) as unknown as typeof fetch;
		}

		function renderNoRunId(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
			sessionStorage.setItem(
				"waypoint.session",
				JSON.stringify({
					token: "tok",
					username: "j.moreno",
					role,
					expiresAt: new Date(Date.now() + 3600_000).toISOString(),
				}),
			);
			return render(
				<AuthProvider>
					<RouterProvider>
						<LiveRunScreen runId={undefined} />
					</RouterProvider>
				</AuthProvider>,
			);
		}

		it("shows an empty-state explanation with a Start a Scan link when no compliance run is active", async () => {
			installRunListFetchMock([COMPLETED_RUN, DOWNLOAD_RUN]);
			renderNoRunId();

			await waitFor(() => expect(screen.getByText(/No compliance run is currently active/)).toBeInTheDocument());
			expect(screen.queryByText("No run selected.")).not.toBeInTheDocument();

			const startLink = screen.getByRole("link", { name: "Start a Scan" });
			expect(startLink).toHaveAttribute("href", "/scan/new");
			const jobsLink = screen.getByRole("link", { name: "Jobs" });
			expect(jobsLink).toHaveAttribute("href", "/live-jobs");
		});

		it("lists active compliance runs (excluding completed and non-compliance run types) as selectable rows", async () => {
			installRunListFetchMock([ACTIVE_RUN, COMPLETED_RUN, DOWNLOAD_RUN]);
			renderNoRunId();

			await waitFor(() => expect(screen.getByText(ACTIVE_RUN.id)).toBeInTheDocument());
			expect(screen.queryByText(COMPLETED_RUN.id)).not.toBeInTheDocument();
			expect(screen.queryByText(DOWNLOAD_RUN.id)).not.toBeInTheDocument();
			expect(screen.queryByText("No run selected.")).not.toBeInTheDocument();
			expect(screen.queryByText(/No compliance run is currently active/)).not.toBeInTheDocument();
		});

		it("selecting a listed run navigates to /live-run?run=<id>", async () => {
			installRunListFetchMock([ACTIVE_RUN]);
			renderNoRunId();

			const row = await screen.findByRole("link", { name: new RegExp(ACTIVE_RUN.id) });
			expect(row).toHaveAttribute("href", `/live-run?run=${ACTIVE_RUN.id}`);

			row.click();
			await waitFor(() => expect(window.location.pathname + window.location.search).toBe(`/live-run?run=${ACTIVE_RUN.id}`));
			window.history.pushState(null, "", "/live-run");
		});

		it("shows a load error instead of the bare dead end when GET /runs fails", async () => {
			globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
				const url = typeof input === "string" ? input : input.toString();
				if (url.startsWith("/api/v1/runs?")) {
					return new Response(JSON.stringify({ error: { code: "internal_error", message: "Could not load compliance runs." } }), {
						status: 500,
					});
				}
				if (url === "/api/v1/auth/me") {
					return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
				}
				throw new Error(`Unhandled fetch in test: ${url}`);
			}) as unknown as typeof fetch;
			renderNoRunId();

			await waitFor(() => expect(screen.getByText("Could not load compliance runs.")).toBeInTheDocument());
			expect(screen.queryByText("No run selected.")).not.toBeInTheDocument();
		});
	});

	/** with-`?run=` behavior is unchanged by #711 — same board render as
	 * every other test in this suite above; this is just an explicit
	 * regression pin that supplying a runId never renders the new picker. */
	it("with a runId supplied, renders the run board directly and never the picker (#711 regression pin)", async () => {
		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z");

		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());
		expect(screen.queryByText(/No compliance run is currently active/)).not.toBeInTheDocument();
		expect(document.querySelector(".live-run__picker")).not.toBeInTheDocument();
	});
});
