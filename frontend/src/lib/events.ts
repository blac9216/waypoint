/**
 * SSE client for the job engine's event streams (docs/api-contract.md
 * "Event streams (SSE)"): `/api/v1/events` (global — job log drawer + nav
 * badges) and `/api/v1/runs/{id}/events` (per-run).
 *
 * Built on `fetch` + a streaming body reader rather than the native
 * `EventSource`, for one concrete reason: EventSource cannot set an
 * `Authorization` header, and this API is bearer-token authenticated (see
 * lib/api.ts). Everything else follows the same wire protocol EventSource
 * uses (an `id:`/`event:`/`data:` frame grammar, `Last-Event-ID` replay on
 * reconnect) so it's a drop-in replacement, not a divergent protocol.
 *
 * Envelope (contract, verbatim):
 *   { seq, ts, type, run_id?, job_id?, data }
 * Types: job.state | job.log | run.progress | queue.state |
 *        download.progress | system.notice
 * "seq is monotonic per stream; clients reconnect with Last-Event-ID and the
 * server replays from Postgres." — every animated/counted thing in the UI
 * must come from one of these six types, never a poll.
 */

export type WaypointEventType =
	| "job.state"
	| "job.log"
	| "run.progress"
	| "queue.state"
	| "download.progress"
	| "system.notice";

export interface WaypointEvent<T = unknown> {
	seq: number;
	ts: string;
	type: WaypointEventType;
	run_id?: string;
	job_id?: string;
	data: T;
}

export type ConnectionState = "connecting" | "open" | "reconnecting" | "closed";

export interface EventStreamHandlers {
	onEvent: (event: WaypointEvent) => void;
	onStateChange?: (state: ConnectionState) => void;
}

export interface EventStreamOptions extends EventStreamHandlers {
	/** Bearer token; the stream will not connect without one. */
	getToken: () => string | null;
	/** Reconnect backoff bounds, in ms. */
	minBackoffMs?: number;
	maxBackoffMs?: number;
}

interface SseFrame {
	id?: string;
	event?: string;
	data: string;
}

/**
 * A frame ends at a blank line, and the SSE grammar (WHATWG HTML,
 * "Interpreting an event stream") accepts `\r\n`, `\n` or `\r` as the line
 * terminator — so the blank line is any of `\r\n\r\n`, `\n\n` or `\r\r`.
 * Splitting on `"\n\n"` alone (the original implementation) meant a
 * CRLF-emitting server produced a buffer with no recognizable boundary: the
 * stream connected, stayed "open", and delivered zero events forever with
 * no error and no reconnect. Issue #70.
 *
 * Deliberately non-global so `exec` is stateless and always returns the
 * leftmost boundary. A buffer ending in a partial terminator (e.g. a lone
 * trailing `\r`) simply doesn't match yet and is carried into the next
 * chunk, which is what makes this safe across arbitrary chunk splits.
 */
const FRAME_BOUNDARY = /\r\n\r\n|\n\n|\r\r/;

/** Line terminators within a single frame — same grammar, one line at a time. */
const LINE_BOUNDARY = /\r\n|\n|\r/;

/**
 * Parses one already-delimited frame body into its `id`/`event`/`data`
 * fields. Exported for unit testing; `connectEventStream` is the only
 * production caller.
 */
export function parseSseFrame(raw: string): SseFrame {
	const frame: SseFrame = { data: "" };
	const dataLines: string[] = [];
	for (const line of raw.split(LINE_BOUNDARY)) {
		if (line.startsWith("id:")) {
			frame.id = line.slice(3).trim();
		} else if (line.startsWith("event:")) {
			frame.event = line.slice(6).trim();
		} else if (line.startsWith("data:")) {
			dataLines.push(line.slice(5).trimStart());
		}
		// Anything else (`:` comment/heartbeat lines, `retry:`, unknown fields)
		// is ignored per the spec.
	}
	frame.data = dataLines.join("\n");
	return frame;
}

/**
 * Opens a self-reconnecting SSE subscription. Returns a `close()` function;
 * calling it stops reconnection and aborts any in-flight request.
 */
export function connectEventStream(url: string, options: EventStreamOptions): () => void {
	const minBackoff = options.minBackoffMs ?? 1000;
	const maxBackoff = options.maxBackoffMs ?? 15000;

	let closed = false;
	let controller: AbortController | null = null;
	let lastEventId: string | undefined;
	let attempt = 0;
	let retryTimer: ReturnType<typeof setTimeout> | undefined;

	const setState = (state: ConnectionState) => options.onStateChange?.(state);

	async function connectOnce(): Promise<void> {
		const token = options.getToken();
		if (!token) {
			// Not authenticated yet; back off and try again rather than erroring —
			// the drawer mounts before login resolves on a hard refresh.
			throw new Error("no_token");
		}

		controller = new AbortController();
		const headers: Record<string, string> = { Accept: "text/event-stream", Authorization: `Bearer ${token}` };
		if (lastEventId) {
			headers["Last-Event-ID"] = lastEventId;
		}

		setState(attempt === 0 ? "connecting" : "reconnecting");
		const response = await fetch(url, { headers, signal: controller.signal });
		if (!response.ok || !response.body) {
			throw new Error(`event stream request failed: ${response.status}`);
		}

		setState("open");
		attempt = 0;

		const reader = response.body.getReader();
		const decoder = new TextDecoder();
		let buffer = "";
		for (;;) {
			const { value, done } = await reader.read();
			if (done) {
				break;
			}
			buffer += decoder.decode(value, { stream: true });
			let boundary: RegExpExecArray | null;
			// Drain every complete frame currently in the buffer. LF, CRLF and
			// CR delimiters are all accepted (issue #70) — the backend (#3)
			// hasn't picked one, and getting this wrong fails silently.
			while ((boundary = FRAME_BOUNDARY.exec(buffer)) !== null) {
				const raw = buffer.slice(0, boundary.index);
				buffer = buffer.slice(boundary.index + boundary[0].length);
				const frame = parseSseFrame(raw);
				if (frame.id) {
					lastEventId = frame.id;
				}
				if (!frame.data) {
					continue;
				}
				try {
					const parsed = JSON.parse(frame.data) as WaypointEvent;
					if (frame.id === undefined && typeof parsed.seq === "number") {
						lastEventId = String(parsed.seq);
					}
					options.onEvent(parsed);
				} catch {
					// Malformed frame from a misbehaving proxy/backend — drop it,
					// don't tear down an otherwise-healthy stream over one line.
				}
			}
		}
	}

	async function loop(): Promise<void> {
		while (!closed) {
			try {
				await connectOnce();
				if (closed) {
					return;
				}
				// Server closed the stream cleanly; reconnect immediately once.
				attempt = 0;
			} catch (err) {
				if (closed || (err instanceof DOMException && err.name === "AbortError")) {
					return;
				}
				attempt += 1;
			}
			if (closed) {
				return;
			}
			const backoff = Math.min(maxBackoff, minBackoff * 2 ** Math.max(0, attempt - 1));
			await new Promise<void>((resolve) => {
				retryTimer = setTimeout(resolve, backoff);
			});
		}
	}

	void loop();

	return function close() {
		closed = true;
		setState("closed");
		if (retryTimer) {
			clearTimeout(retryTimer);
		}
		controller?.abort();
	};
}
