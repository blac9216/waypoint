import {
	createContext,
	useCallback,
	useContext,
	useEffect,
	useMemo,
	useState,
	type AnchorHTMLAttributes,
	type ReactNode,
} from "react";
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

interface RouterContextValue {
	path: string;
	route: RouteDef | undefined;
	navigate: (path: string) => void;
}

const RouterContext = createContext<RouterContextValue | null>(null);

export function RouterProvider({ children }: { children: ReactNode }) {
	const [path, setPath] = useState(() => window.location.pathname);

	useEffect(() => {
		const onPopState = () => setPath(window.location.pathname);
		window.addEventListener("popstate", onPopState);
		return () => window.removeEventListener("popstate", onPopState);
	}, []);

	const navigate = useCallback((next: string) => {
		if (next !== window.location.pathname) {
			window.history.pushState(null, "", next);
		}
		setPath(next);
	}, []);

	const value = useMemo<RouterContextValue>(
		() => ({ path, route: routeForPath(path), navigate }),
		[path, navigate],
	);

	return <RouterContext.Provider value={value}>{children}</RouterContext.Provider>;
}

export function useRouter(): RouterContextValue {
	const ctx = useContext(RouterContext);
	if (!ctx) {
		throw new Error("useRouter must be used within a RouterProvider");
	}
	return ctx;
}

/** True if `role` may access `route` at all right now (role + mode). Does
 * not consider role-gating of individual in-screen actions — that's each
 * screen's own concern (README: actions stay visible-but-disabled). */
export function canAccessRoute(route: RouteDef, role: Role, mode: "connected" | "disconnected" | null): boolean {
	if (route.connectedOnly && mode !== "connected") {
		return false;
	}
	return roleAtLeast(role, route.requiredRole);
}

export function Link({
	to,
	children,
	...rest
}: { to: string; children: ReactNode } & AnchorHTMLAttributes<HTMLAnchorElement>) {
	const { navigate } = useRouter();
	return (
		<a
			href={to}
			{...rest}
			onClick={(event) => {
				if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
					return;
				}
				event.preventDefault();
				navigate(to);
			}}
		>
			{children}
		</a>
	);
}
