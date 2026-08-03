/**
 * Thin REST client for `/api/v1` (docs/api-contract.md "Conventions").
 *
 * - JSON bodies, snake_case fields on the wire (kept snake_case in the TS
 *   types too, deliberately — translating to camelCase would just be a
 *   second name for every field with no behavioral benefit here).
 * - Errors arrive as `{ error: { code, message, detail? } }`; ApiError
 *   normalizes that (and the mode-unavailable / network-failure cases) into
 *   one shape callers can render directly.
 * - Bearer token auth: the resolved token is attached as `Authorization`.
 * - Long-running operations return 202 with a run_id/job_id; this client
 *   just returns the parsed body and leaves progress to the SSE layer
 *   (lib/events.ts) per the contract's explicit "not polling" rule.
 */

export class ApiError extends Error {
	code: string;
	detail?: unknown;
	status: number;

	constructor(status: number, code: string, message: string, detail?: unknown) {
		super(message);
		this.name = "ApiError";
		this.status = status;
		this.code = code;
		this.detail = detail;
	}
}

/** `409 mode_unavailable` — the endpoint exists but the instance's deployment
 * mode (connected/air-gapped) can't serve it right now. Distinct from 404. */
export function isModeUnavailable(err: unknown): boolean {
	return err instanceof ApiError && err.status === 409 && err.code === "mode_unavailable";
}

export const API_BASE = "/api/v1";

export type TokenGetter = () => string | null;

let getToken: TokenGetter = () => null;
let onUnauthorized: () => void = () => {};

/** Wired up once by AuthProvider so this module never imports React state
 * directly (keeps the client usable from non-component code, e.g. the SSE
 * reconnect loop). */
export function setTokenGetter(fn: TokenGetter): void {
	getToken = fn;
}

/** Called on any 401 from any authenticated request — AuthProvider uses this
 * to clear a stale/expired session in one place instead of every call site
 * checking `err.status === 401` for itself. */
export function setUnauthorizedHandler(fn: () => void): void {
	onUnauthorized = fn;
}

export interface ApiRequestOptions extends Omit<RequestInit, "body"> {
	body?: unknown;
	/** Skip attaching the bearer token (only the login call needs this). */
	unauthenticated?: boolean;
	/**
	 * Abandon the request after this many milliseconds and reject with
	 * `ApiError(0, "timeout")`. Opt-in per call, not a global default: `fetch`
	 * has no timeout of its own, so a request whose server accepts the
	 * connection and then never answers hangs forever, and any state machine
	 * waiting on it never settles. That is not hypothetical — it is issue
	 * #88-review-finding-2: `GET /api/v1/system` hanging left the whole app on
	 * a permanently blank page, because `ready` only flips when the fetch
	 * resolves one way or the other.
	 *
	 * Deliberately not applied to every request. Streaming/long-poll paths
	 * (`/events`) must be able to stay open indefinitely, and a caller with no
	 * fail-safe to fall through to is better off waiting than being handed an
	 * error it can only rethrow. Set it where a bounded wait has somewhere to
	 * go — currently the startup `/system` + `/stigman` fetches in
	 * `SystemProvider`, whose failure path already exists and is already
	 * exercised (an API error folds mode to `disconnected` and renders the
	 * chrome).
	 *
	 * Bounds the WHOLE exchange, not just the connect. An earlier version
	 * cleared the timer in a `finally` attached to the `fetch` call, which
	 * resolves when response *headers* arrive — leaving `response.json()`
	 * unbounded, so a 200 whose body never completes reproduced the blank page
	 * exactly (PR #88 round-2 review, finding 1; measured at 25 s in
	 * Chromium). The timer now spans the body read too.
	 *
	 * Composes with a caller-supplied `signal` rather than being ignored by
	 * it: whichever fires first aborts the request. A caller abort still
	 * surfaces as `ApiError(0, "network_error")` exactly as before; only a
	 * timeout produces `ApiError(0, "timeout")`.
	 */
	timeoutMs?: number;
}

async function parseErrorBody(response: Response): Promise<ApiError> {
	let code = "unknown_error";
	let message = `Request failed with status ${response.status}`;
	let detail: unknown;
	try {
		const body = await response.json();
		if (body && typeof body === "object" && "error" in body) {
			const envelope = (body as { error?: { code?: string; message?: string; detail?: unknown } }).error;
			code = envelope?.code ?? code;
			message = envelope?.message ?? message;
			detail = envelope?.detail;
		}
	} catch {
		// Non-JSON error body (e.g. an nginx-level 502) — fall back to the status text.
		message = response.statusText || message;
	}
	return new ApiError(response.status, code, message, detail);
}

function timeoutError(timeoutMs: number | undefined, cause?: unknown): ApiError {
	return new ApiError(0, "timeout", `The Waypoint API did not respond within ${timeoutMs} ms.`, cause);
}

/**
 * Issue one `/api/v1/...` request. `path` is relative to API_BASE
 * (e.g. `/dashboard`, `/runs/${id}/pause`).
 */
export async function apiFetch<T = unknown>(path: string, options: ApiRequestOptions = {}): Promise<T> {
	const { body, unauthenticated, headers, timeoutMs, signal, ...rest } = options;
	const finalHeaders = new Headers(headers);
	finalHeaders.set("Accept", "application/json");
	if (body !== undefined) {
		finalHeaders.set("Content-Type", "application/json");
	}
	if (!unauthenticated) {
		const token = getToken();
		if (token) {
			finalHeaders.set("Authorization", `Bearer ${token}`);
		}
	}

	// Hand-rolled with AbortController rather than `AbortSignal.timeout()`:
	// the timer has to be cleared once the exchange is over (an uncleared
	// timeout keeps a handle alive for its full duration, which in tests means
	// a suite that will not exit), and `AbortSignal.timeout` gives no handle
	// to clear.
	//
	// `timedOut` — not `controller.signal.aborted` — is what distinguishes a
	// timeout from a caller abort, because the controller is now aborted by
	// either one. `AbortSignal.any` would express the composition more
	// directly but is newer than this project's floor; forwarding by listener
	// is equivalent and has no compatibility question.
	const controller = timeoutMs !== undefined ? new AbortController() : undefined;
	let timedOut = false;
	const timer = controller
		? setTimeout(() => {
				timedOut = true;
				controller.abort();
			}, timeoutMs)
		: undefined;
	let forwardAbort: (() => void) | undefined;
	if (controller && signal) {
		if (signal.aborted) {
			controller.abort();
		} else {
			forwardAbort = () => controller.abort();
			signal.addEventListener("abort", forwardAbort, { once: true });
		}
	}

	/** The deadline covers the body read too, so every `await` below is inside
	 * this try and every exit path goes through the one `finally`. */
	try {
		const response = await fetch(`${API_BASE}${path}`, {
			...rest,
			signal: controller ? controller.signal : signal,
			headers: finalHeaders,
			body: body !== undefined ? JSON.stringify(body) : undefined,
		});

		if (!response.ok) {
			// `parseErrorBody` awaits `response.json()`, which is unbounded
			// without the live timer above — the round-2 finding applies to the
			// error path exactly as it does to the success path.
			const error = await parseErrorBody(response);
			if (timedOut) {
				throw timeoutError(timeoutMs);
			}
			if (response.status === 401 && !unauthenticated) {
				onUnauthorized();
			}
			throw error;
		}

		if (response.status === 204) {
			return undefined as T;
		}

		// NOTE: no `X-Total-Count` handling here on purpose. No list endpoint
		// exists in docs/api-contract.md yet, so there is nothing to shape the
		// pagination result against; the half-written branch that used to live
		// here returned the same value from both arms and was removed (PR #65
		// review, finding #4). Add it with a real endpoint, not before.
		try {
			return (await response.json()) as T;
		} catch (bodyError) {
			// A body that is absent or not JSON is not an error (unchanged
			// behaviour) — but a body abandoned mid-read because the deadline
			// expired is, and must not be laundered into `undefined`.
			if (timedOut) {
				throw timeoutError(timeoutMs, bodyError);
			}
			return undefined as T;
		}
	} catch (err) {
		if (err instanceof ApiError) {
			throw err;
		}
		if (timedOut) {
			throw timeoutError(timeoutMs, err);
		}
		throw new ApiError(0, "network_error", "Could not reach the Waypoint API.", err);
	} finally {
		clearTimeout(timer);
		if (forwardAbort && signal) {
			signal.removeEventListener("abort", forwardAbort);
		}
	}
}

export function apiGet<T>(path: string, options?: ApiRequestOptions): Promise<T> {
	return apiFetch<T>(path, { ...options, method: "GET" });
}

export function apiPost<T>(path: string, body?: unknown, options?: ApiRequestOptions): Promise<T> {
	return apiFetch<T>(path, { ...options, method: "POST", body });
}
