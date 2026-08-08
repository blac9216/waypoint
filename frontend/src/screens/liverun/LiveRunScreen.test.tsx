import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { LiveRunScreen } from "./LiveRunScreen";
import type { RunHeader, RunJob } from "./liverun";

const HEADER: RunHeader = {
	id: "run-0808-0100Z",
	site: "Alpha Enclave",
	target_count: 2,
	initiated_by: "j.moreno",
	credential_name: "svc-stig-scan",
	state: "running",
	pass: 0,
	fail: 0,
	na: 0,
	percent: 0,
	completed_count: 0,
	elapsed_seconds: 30,
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
};

const JOBS: RunJob[] = [
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
			return new Response(JSON.stringify(HEADER), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
			return new Response(JSON.stringify(JOBS), { status: 200, headers: { "Content-Type": "application/json" } });
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
			<LiveRunScreen runId={runId} />
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
	});

	it("renders Pause/Abort disabled with the not-yet-built (#285) reason even for an Admin", async () => {
		// #285 hasn't wired run controls; the block is role-independent, so a
		// privileged Admin must NOT see enabled Pause/Abort buttons that
		// silently do nothing — they stay visible-but-disabled with the #285
		// reason, not the insufficient-role reason.
		installFetchMock(() => ({ frames: [] }));
		renderWithAuth("run-0808-0100Z", "Admin");

		await waitFor(() => expect(screen.getByText("run-0808-0100Z")).toBeInTheDocument());

		const pause = screen.getByRole("button", { name: "Pause queue" });
		const abort = screen.getByRole("button", { name: "Abort run" });
		expect(pause).toBeDisabled();
		expect(abort).toBeDisabled();
		// The title explains it's not built yet (#285), not a role denial.
		expect(pause).toHaveAttribute("title", expect.stringContaining("#285"));
		expect(abort).toHaveAttribute("title", expect.stringContaining("#285"));
		expect(pause.getAttribute("title")).not.toMatch(/Requires|role/i);
	});

	it("shows the blocked banner and a disabled resume control when a queue halts", async () => {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/runs/run-0808-0100Z" && new Headers(init?.headers).get("Accept") !== "text/event-stream") {
				const blockedHeader: RunHeader = {
					...HEADER,
					blocked: true,
					queues: [{ ...HEADER.queues[0], blocked: true, blocked_reason: "credential failure" }],
				};
				return new Response(JSON.stringify(blockedHeader), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
				return new Response(JSON.stringify(JOBS), { status: 200 });
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

		// Viewer, not Admin — README "Roles & Permissions": credential-swap-resume
		// is Admin only, and the disabled control must still render (visible but
		// disabled), not disappear.
		renderWithAuth("run-0808-0100Z", "Viewer");
		await waitFor(() => expect(screen.getByText(/Queue halted/)).toBeInTheDocument());
		const resumeButton = screen.getByRole("button", { name: /Change credential/ });
		expect(resumeButton).toBeDisabled();
	});
});
