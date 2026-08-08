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
	paused: false,
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
				return new Response(JSON.stringify(HEADER), { status: 200 });
			}
			if (url === "/api/v1/runs/run-0808-0100Z/jobs") {
				return new Response(JSON.stringify(JOBS), { status: 200 });
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
});
