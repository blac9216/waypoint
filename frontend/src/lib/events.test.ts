import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connectEventStream, type WaypointEvent } from "./events";

interface MockCall {
	text: string;
	/** If true, the mock reports a clean stream end right after the chunk —
	 * simulating the server closing the connection, which should make
	 * connectEventStream reconnect. If false (default), the connection
	 * "stays open" (the next read() only settles on abort), matching a
	 * healthy long-lived SSE connection that should NOT trigger a reconnect. */
	endAfterChunk?: boolean;
}

/** Builds a fetch mock where call N behaves per `callsByIndex[N]` (see
 * MockCall). Calls beyond the array's length stay open with no data. */
function makeSseFetch(callsByIndex: MockCall[], headersSeen: Headers[]): typeof fetch {
	let callIndex = 0;
	return vi.fn(async (_url: string, init?: RequestInit) => {
		const thisCall = callsByIndex[callIndex] ?? { text: "" };
		callIndex += 1;
		headersSeen.push(new Headers(init?.headers));
		const bytes = new TextEncoder().encode(thisCall.text);
		let sentFirstChunk = false;
		const signal = init?.signal;

		const body = {
			getReader() {
				return {
					read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
						if (!sentFirstChunk) {
							sentFirstChunk = true;
							return Promise.resolve({ value: bytes, done: false });
						}
						if (thisCall.endAfterChunk) {
							return Promise.resolve({ value: undefined, done: true });
						}
						// Simulate a still-open connection: only settle on abort.
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
	}) as unknown as typeof fetch;
}

describe("connectEventStream", () => {
	let originalFetch: typeof fetch;
	let activeClose: (() => void) | null;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		activeClose = null;
	});

	afterEach(() => {
		// Guarantee no connection outlives its test, pass or fail — an
		// un-closed stream would otherwise keep calling whatever mock the
		// *next* test installs.
		activeClose?.();
		globalThis.fetch = originalFetch;
	});

	it("parses seq/ts/type/data frames and dispatches them via onEvent", async () => {
		const envelope: WaypointEvent = {
			seq: 1,
			ts: "2026-08-02T14:07:31Z",
			type: "job.log",
			job_id: "j-3021",
			data: { level: "info", line: "hello" },
		};
		const sse = `id: 1\ndata: ${JSON.stringify(envelope)}\n\n`;
		const headersSeen: Headers[] = [];
		globalThis.fetch = makeSseFetch([{ text: sse }], headersSeen);

		const received: WaypointEvent[] = [];
		const states: string[] = [];
		activeClose = connectEventStream("/api/v1/events", {
			getToken: () => "tok",
			onEvent: (e) => received.push(e),
			onStateChange: (s) => states.push(s),
			minBackoffMs: 5,
			maxBackoffMs: 10,
		});

		await vi.waitFor(() => expect(received).toHaveLength(1));
		expect(received[0]).toEqual(envelope);
		expect(states).toContain("open");
		expect(headersSeen[0].get("Authorization")).toBe("Bearer tok");
		expect(globalThis.fetch).toHaveBeenCalledTimes(1);
	});

	it("does not connect without a token (avoids hammering the API before login resolves)", async () => {
		const fetchSpy = vi.fn();
		globalThis.fetch = fetchSpy as unknown as typeof fetch;

		activeClose = connectEventStream("/api/v1/events", {
			getToken: () => null,
			onEvent: () => {},
			minBackoffMs: 5,
			maxBackoffMs: 10,
		});

		await new Promise((r) => setTimeout(r, 20));
		expect(fetchSpy).not.toHaveBeenCalled();
	});

	it("reconnects with Last-Event-ID after the first stream ends, replaying from the last seen id", async () => {
		const envelope: WaypointEvent = {
			seq: 42,
			ts: "2026-08-02T14:07:31Z",
			type: "system.notice",
			data: { level: "ok", message: "hi" },
		};
		const first = `id: 42\ndata: ${JSON.stringify(envelope)}\n\n`;
		const headersSeen: Headers[] = [];
		// First call delivers the frame then cleanly ends (server closed the
		// connection) to trigger a reconnect; the second call's stream just
		// stays open — the assertion only cares about the headers it used.
		globalThis.fetch = makeSseFetch([{ text: first, endAfterChunk: true }, { text: "" }], headersSeen);

		activeClose = connectEventStream("/api/v1/events", {
			getToken: () => "tok",
			onEvent: () => {},
			minBackoffMs: 5,
			maxBackoffMs: 10,
		});

		await vi.waitFor(() => expect(headersSeen.length).toBeGreaterThanOrEqual(2));
		expect(headersSeen[0].get("Last-Event-ID")).toBeNull();
		expect(headersSeen[1].get("Last-Event-ID")).toBe("42");
	});
});
