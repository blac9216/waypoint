import { useEffect, useMemo, useState, type ReactNode } from "react";
import { apiGet } from "./api";
import { useAuth } from "./auth-context";
import type { ModeState } from "./routes";
import { SystemContext, type SystemInfo, type StigmanStatus } from "./system-context";

/**
 * How long the startup `/system` + `/stigman` fetches may take before they
 * are abandoned and `ready` is allowed to settle anyway.
 *
 * This exists because `fetch` has no timeout. Without one, a backend that
 * accepts the connection and then never answers leaves `ready` false, `mode`
 * `"unknown"`, and `evaluateRouteAccess` `"pending"` **forever** — and
 * `AppShell` renders `null` for a `connectedOnly` route while pending, so a
 * `/catalog` deep link is a permanently blank page with no chrome, no
 * spinner and no error (PR #88 round-1 review, finding 2; measured in
 * Chromium at 1 s, 3.5 s and 9.5 s with `bodyTextLen: 0`). A hang was the
 * only unbounded case: a 500, a 404 or a dropped connection all reject, so
 * `ready` flips and mode folds to `disconnected` already.
 *
 * The bound covers the RESPONSE BODY, not only the connect. Round 2 of the
 * same review found the first version cleared its timer when `fetch`
 * resolved — i.e. when headers arrived — so a backend that flushed a 200 and
 * then wedged mid-body reproduced the identical blank page at 25 s. `apiFetch`
 * now holds the deadline across `response.json()`; see its `timeoutMs` doc.
 *
 * 8 seconds, chosen between two hard bounds rather than by feel:
 *
 * - **Floor.** It must not fire on a backend that is merely slow, because
 *   firing means "disconnected", which hides connected-only features on an
 *   appliance that has them. `deploy/docker-compose.yml` gives the backend a
 *   5 s healthcheck timeout and a 20 s start period, so once nginx is
 *   serving at all (it is gated on `service_healthy`), a request taking more
 *   than 8 s is not a slow appliance — it is a stuck one.
 * - **Ceiling.** It must fire *before* the proxy does, or the frontend never
 *   gets to choose the outcome. nginx's `/api/` location uses the default
 *   60 s `proxy_read_timeout` (`deploy/nginx/conf.d/default.conf` only
 *   overrides it for the SSE location, to 3600 s), so anything under ~60 s
 *   bounds the wait in the browser rather than deferring to the proxy — and
 *   the frontend must self-bound anyway, since it is also served from
 *   `vite dev` and from any operator's reverse proxy with its own settings.
 *
 * Within that window, shorter is better: the pending blank is on-screen for
 * the whole of it. 8 s keeps the worst case comfortably inside the ~10 s at
 * which an unexplained blank page reads as "broken" rather than "loading".
 */
export const SYSTEM_FETCH_TIMEOUT_MS = 8000;

export function SystemProvider({ children }: { children: ReactNode }) {
	const { status } = useAuth();
	const [system, setSystem] = useState<SystemInfo | null>(null);
	const [stigman, setStigman] = useState<StigmanStatus | null>(null);
	const [ready, setReady] = useState(false);

	useEffect(() => {
		if (status !== "signed-in") {
			return;
		}
		let cancelled = false;

		async function load() {
			// `/system` and `/stigman` are independent per the contract; a
			// STIG Manager outage should never block the mode/version chrome.
			//
			// Both are bounded by SYSTEM_FETCH_TIMEOUT_MS. `ready` flips when
			// this settles, and nothing else can flip it — so an unbounded
			// request here is an unbounded "unknown" mode and, for a
			// connectedOnly route, an unbounded blank page. A timeout rejects
			// exactly like a 500 does, which folds mode to "disconnected" and
			// renders the chrome: the fail-safe path already existed and was
			// already tested; it just was not reachable from a hang.
			const [systemResult, stigmanResult] = await Promise.allSettled([
				apiGet<SystemInfo>("/system", { timeoutMs: SYSTEM_FETCH_TIMEOUT_MS }),
				apiGet<StigmanStatus>("/stigman", { timeoutMs: SYSTEM_FETCH_TIMEOUT_MS }),
			]);
			if (cancelled) {
				return;
			}
			// Deployment mode is a deploy-time fact (README "Layout Rules" /
			// "Interactions"), not something the UI can toggle. When the API is
			// unreachable we deliberately do NOT guess "connected" — an unknown
			// mode hides mode-gated nav (Download Catalog) rather than risk
			// showing a feature the appliance cannot actually serve.
			setSystem(systemResult.status === "fulfilled" ? systemResult.value : null);
			setStigman(stigmanResult.status === "fulfilled" ? stigmanResult.value : null);
			setReady(true);
		}

		void load();
		return () => {
			cancelled = true;
		};
	}, [status]);

	// "unknown" only during the window between sign-in and the first /system
	// fetch settling; once `ready`, a resolved SystemInfo says "connected" or
	// "disconnected", and a fetch failure (system === null after ready) folds
	// into "disconnected" — the same fail-safe direction the comment above
	// already documents for hiding mode-gated nav on an unreachable API. That
	// window is now bounded at SYSTEM_FETCH_TIMEOUT_MS in every case,
	// including a request that hangs rather than errors: an abandoned fetch
	// rejects, so it reaches this same failure fold instead of leaving the
	// app "unknown" indefinitely.
	const mode = useMemo<ModeState>(() => {
		if (!ready) {
			return "unknown";
		}
		return system?.mode === "connected" ? "connected" : "disconnected";
	}, [ready, system]);

	const value = useMemo(() => ({ system, stigman, ready, mode }), [system, stigman, ready, mode]);

	return <SystemContext.Provider value={value}>{children}</SystemContext.Provider>;
}
