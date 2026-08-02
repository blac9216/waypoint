import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { connectEventStream, parseSseFrame, type WaypointEvent } from "./events";

interface MockCall {
	/** Body delivered as a single chunk. Ignored when `chunks` is set. */
	text?: string;
	/** Body delivered as several reader chunks, to exercise frame boundaries
	 * that straddle a chunk split (e.g. a `\r\n\r\n` cut down the middle). */
	chunks?: string[];
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
		const encoder = new TextEncoder();
		const pending = (thisCall.chunks ?? [thisCall.text ?? ""]).map((chunk) => encoder.encode(chunk));
		let sentChunks = 0;
		const signal = init?.signal;

		const body = {
			getReader() {
				return {
					read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
						if (sentChunks < pending.length) {
							const value = pending[sentChunks];
							sentChunks += 1;
							return Promise.resolve({ value, done: false });
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

	/**
	 * Issue #70. The parser used to split frames on `"\n\n"` only, so a
	 * CRLF-emitting server (several ASP.NET Core SSE helpers do this, and
	 * issue #3 hasn't picked a line ending yet) produced a buffer with no
	 * recognizable boundary — the stream connected, stayed open, and
	 * delivered nothing, with no error to explain it. These tests fail if
	 * that regression is reintroduced.
	 */
	describe("frame delimiters (issue #70)", () => {
		const envelope: WaypointEvent = {
			seq: 7,
			ts: "2026-08-02T14:07:31Z",
			type: "job.log",
			job_id: "j-3021",
			data: { level: "warn", line: "crlf frame" },
		};

		it("parses CRLF-delimited frames and advances Last-Event-ID", async () => {
			// Every terminator is \r\n, including the blank line that ends the frame.
			const sse = `id: 7\r\nevent: message\r\ndata: ${JSON.stringify(envelope)}\r\n\r\n`;
			const headersSeen: Headers[] = [];
			globalThis.fetch = makeSseFetch([{ text: sse, endAfterChunk: true }, { text: "" }], headersSeen);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(1));
			expect(received[0]).toEqual(envelope);
			// The id must have been recorded, or replay-on-reconnect silently
			// restarts the stream from the beginning.
			await vi.waitFor(() => expect(headersSeen.length).toBeGreaterThanOrEqual(2));
			expect(headersSeen[1].get("Last-Event-ID")).toBe("7");
		});

		it("parses back-to-back CRLF frames arriving in one chunk", async () => {
			const a = { ...envelope, seq: 1 };
			const b = { ...envelope, seq: 2 };
			const sse =
				`id: 1\r\ndata: ${JSON.stringify(a)}\r\n\r\n` + `id: 2\r\ndata: ${JSON.stringify(b)}\r\n\r\n`;
			globalThis.fetch = makeSseFetch([{ text: sse }], []);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(2));
			expect(received.map((e) => e.seq)).toEqual([1, 2]);
		});

		it("parses a CRLF frame whose boundary is split across two reader chunks", async () => {
			const frame = `id: 7\r\ndata: ${JSON.stringify(envelope)}\r\n\r\n`;
			// Cut the stream in the middle of the terminating \r\n\r\n so the
			// first chunk ends on a lone \r — the case a naive
			// normalize-then-scan would turn into a phantom boundary.
			const cut = frame.length - 3;
			globalThis.fetch = makeSseFetch([{ chunks: [frame.slice(0, cut), frame.slice(cut)] }], []);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(1));
			expect(received[0]).toEqual(envelope);
		});

		it("parses the mixed \\r\\n\\n and \\n\\r\\n blank-line forms the grammar also allows", async () => {
			const a = { ...envelope, seq: 21 };
			const b = { ...envelope, seq: 22 };
			// A blank line is any two consecutive terminators, and they need not
			// be the same terminator twice.
			const sse = `id: 21\r\ndata: ${JSON.stringify(a)}\r\n\n` + `id: 22\ndata: ${JSON.stringify(b)}\n\r\n`;
			globalThis.fetch = makeSseFetch([{ text: sse }], []);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(2));
			expect(received.map((e) => e.seq)).toEqual([21, 22]);
		});

		it("does not treat a single \\r\\n inside a frame as a boundary", async () => {
			// Guards against the `(?:\r\n|\r|\n){2}` backtracking trap, which
			// would split this single frame in half and lose its data line.
			const multi = { ...envelope, seq: 31 };
			const sse = `id: 31\r\nevent: job.log\r\ndata: ${JSON.stringify(multi)}\r\n\r\n`;
			globalThis.fetch = makeSseFetch([{ text: sse }], []);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(1));
			expect(received[0]).toEqual(multi);
		});

		it("still parses the LF-delimited form, and the bare-CR form", async () => {
			const lf = { ...envelope, seq: 11 };
			const cr = { ...envelope, seq: 12 };
			const sse = `id: 11\ndata: ${JSON.stringify(lf)}\n\n` + `id: 12\rdata: ${JSON.stringify(cr)}\r\r`;
			globalThis.fetch = makeSseFetch([{ text: sse }], []);

			const received: WaypointEvent[] = [];
			activeClose = connectEventStream("/api/v1/events", {
				getToken: () => "tok",
				onEvent: (e) => received.push(e),
				minBackoffMs: 5,
				maxBackoffMs: 10,
			});

			await vi.waitFor(() => expect(received).toHaveLength(2));
			expect(received.map((e) => e.seq)).toEqual([11, 12]);
		});
	});

	describe("parseSseFrame", () => {
		it("reads id/event/data fields regardless of line terminator", () => {
			expect(parseSseFrame("id: 5\r\nevent: job.log\r\ndata: {}")).toEqual({
				id: "5",
				event: "job.log",
				data: "{}",
			});
			expect(parseSseFrame("id: 5\revent: job.log\rdata: {}")).toEqual({
				id: "5",
				event: "job.log",
				data: "{}",
			});
		});

		it("joins multi-line data with \\n and ignores comment/heartbeat lines", () => {
			expect(parseSseFrame(": heartbeat\r\ndata: line one\r\ndata: line two").data).toBe("line one\nline two");
		});
	});
});
