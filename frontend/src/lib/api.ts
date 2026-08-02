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

/**
 * Issue one `/api/v1/...` request. `path` is relative to API_BASE
 * (e.g. `/dashboard`, `/runs/${id}/pause`).
 */
export async function apiFetch<T = unknown>(path: string, options: ApiRequestOptions = {}): Promise<T> {
	const { body, unauthenticated, headers, ...rest } = options;
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

	let response: Response;
	try {
		response = await fetch(`${API_BASE}${path}`, {
			...rest,
			headers: finalHeaders,
			body: body !== undefined ? JSON.stringify(body) : undefined,
		});
	} catch (networkError) {
		throw new ApiError(0, "network_error", "Could not reach the Waypoint API.", networkError);
	}

	if (!response.ok) {
		const error = await parseErrorBody(response);
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
	return (await response.json().catch(() => undefined)) as T;
}

export function apiGet<T>(path: string, options?: ApiRequestOptions): Promise<T> {
	return apiFetch<T>(path, { ...options, method: "GET" });
}

export function apiPost<T>(path: string, body?: unknown, options?: ApiRequestOptions): Promise<T> {
	return apiFetch<T>(path, { ...options, method: "POST", body });
}
