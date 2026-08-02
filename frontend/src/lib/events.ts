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
			let boundary: number;
			// Drain every complete frame currently in the buffer.
			while ((boundary = buffer.indexOf("\n\n")) !== -1) {
				const raw = buffer.slice(0, boundary);
				buffer = buffer.slice(boundary + 2);
				const frame: SseFrame = { data: "" };
				const dataLines: string[] = [];
				for (const line of raw.split("\n")) {
					if (line.startsWith("id:")) {
						frame.id = line.slice(3).trim();
					} else if (line.startsWith("event:")) {
						frame.event = line.slice(6).trim();
					} else if (line.startsWith("data:")) {
						dataLines.push(line.slice(5).trimStart());
					}
				}
				frame.data = dataLines.join("\n");
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
