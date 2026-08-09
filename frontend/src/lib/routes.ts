import type { Role } from "./roles";
import { roleAtLeast } from "./roles";

/**
 * A small hand-rolled History-API router. The prototype had no routing at
 * all ("single-page, screen state only" — README "Interactions & Behavior");
 * a real installable app needs real URLs (deep links, browser back/forward,
 * refresh-safe), but the screen set for this issue is chrome + placeholders
 * — pulling in a routing library for nine stub screens isn't worth the
 * dependency yet. Screens that need nested/param routes can graduate this
 * to a library later without touching the route table's shape.
 */
export type ScreenKey =
	| "dashboard"
	| "live-run"
	| "start-scan"
	| "results"
	| "benchmarks"
	| "catalog"
	| "library"
	| "transfer"
	| "configuration";

export interface RouteDef {
	key: ScreenKey;
	path: string;
	title: string;
	/** Minimum role to access the screen at all (README "Roles & Permissions"
	 * screen-level guard: "changing role while inside a screen the new role
	 * cannot access redirects to Dashboard"). */
	requiredRole: Role;
	/** When true, the route (and its nav entry) only exists in connected
	 * mode — mode-gating removes it outright rather than disabling it. */
	connectedOnly?: boolean;
}

export const ROUTES: RouteDef[] = [
	{ key: "dashboard", path: "/", title: "Dashboard", requiredRole: "Viewer" },
	{ key: "live-run", path: "/live-run", title: "Live Run", requiredRole: "Viewer" },
	{ key: "start-scan", path: "/scan/new", title: "Start a Scan", requiredRole: "Cyber" },
	{ key: "results", path: "/results", title: "Results & History", requiredRole: "Viewer" },
	{ key: "benchmarks", path: "/benchmarks", title: "Benchmarks", requiredRole: "Viewer" },
	{ key: "catalog", path: "/catalog", title: "Download Catalog", requiredRole: "Operator", connectedOnly: true },
	{ key: "library", path: "/library", title: "Library", requiredRole: "Viewer" },
	{ key: "transfer", path: "/transfer", title: "Transfer", requiredRole: "Viewer" },
	{ key: "configuration", path: "/config", title: "Configuration", requiredRole: "Admin" },
];

export const DEFAULT_ROUTE = ROUTES[0];

export function routeForPath(path: string): RouteDef | undefined {
	return ROUTES.find((r) => r.path === path);
}

export function routeForKey(key: ScreenKey): RouteDef {
	const found = ROUTES.find((r) => r.key === key);
	if (!found) {
		throw new Error(`Unknown screen key: ${key}`);
	}
	return found;
}

/** Tri-state deployment mode. `"unknown"` means "not yet known" — genuinely
 * distinct from `"disconnected"` ("known, and the answer is no"). Conflating
 * the two was issue #82: a `connectedOnly` route was denied (and the URL
 * rewritten) before `GET /api/v1/system` had a chance to resolve, because
 * the old code used `mode: "connected" | "disconnected" | null` and treated
 * `null` exactly like `"disconnected"`. Defined here (not `./system`, which
 * imports this type) because route access is the concern that consumes it;
 * `SystemProvider` computes the value. */
export type ModeState = "connected" | "disconnected" | "unknown";

/**
 * Outcome of evaluating whether `role`/`mode` may access `route` right now.
 * - `"allowed"` — render the route.
 * - `"denied"` — role is insufficient, or mode is *known* and says no;
 *   substitute `DEFAULT_ROUTE` and correct the URL.
 * - `"pending"` — the route is `connectedOnly` and mode is not yet known.
 *   The caller must not decide in either direction: rendering the requested
 *   screen risks mounting a connected-only screen on a disconnected
 *   appliance for a frame (the #78 never-mount property this guard exists
 *   to protect), while redirecting away risks bouncing a deep link mode
 *   will turn out to allow (issue #82, the bug this type exists to fix).
 *   Resolves to `"allowed"` or `"denied"` as soon as mode settles.
 */
export type RouteAccess = "allowed" | "denied" | "pending";

/** Evaluates route access as a tri-state (see `RouteAccess`) rather than a
 * boolean, so a mode-gated route can be held pending instead of forced to a
 * premature yes/no. Role is checked first and settles immediately — it
 * needs no async state, so a role failure is always `"denied"`, never
 * `"pending"`, even before mode is known. Does not consider role-gating of
 * individual in-screen actions — that's each screen's own concern (README:
 * actions stay visible-but-disabled). */
export function evaluateRouteAccess(route: RouteDef, role: Role, mode: ModeState): RouteAccess {
	if (!roleAtLeast(role, route.requiredRole)) {
		return "denied";
	}
	if (route.connectedOnly) {
		if (mode === "unknown") {
			return "pending";
		}
		if (mode !== "connected") {
			return "denied";
		}
	}
	return "allowed";
}
