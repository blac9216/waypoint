import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "../../lib/auth-context";
import type { RouteDef } from "../../lib/routes";
import { JobLogDrawer } from "./JobLogDrawer";
import { LeftRail } from "./LeftRail";
import { TopBar } from "./TopBar";
import "./Chrome.css";

const RAIL_STORAGE_KEY = "waypoint.railOpen";

function readRailOpen(): boolean {
	try {
		const stored = window.localStorage.getItem(RAIL_STORAGE_KEY);
		return stored === null ? true : stored === "1";
	} catch {
		return true;
	}
}

/** The global chrome: top bar + left rail + routed screen + job log drawer.
 *
 * `route` is supplied by the caller (`AppShell` in `src/App.tsx`) already
 * resolved through the screen-level role/mode guard: `canAccessRoute` runs
 * during `AppShell`'s render, before `children` (the routed screen
 * component) is ever created, and `AppShell` substitutes `DEFAULT_ROUTE`'s
 * screen when the current route isn't allowed (README "Roles &
 * Permissions": "changing role while inside a screen the new role cannot
 * access redirects to Dashboard. Do not rely on gating the nav entry point
 * alone."). Chrome itself performs no guard check and issues no redirect —
 * by design, so the disallowed screen is never instantiated even for one
 * frame (issue #78: a `useEffect`-based redirect here fires only after
 * React has already mounted and painted the disallowed component). The
 * actual `navigate()` call that corrects the URL also lives in `AppShell`,
 * scheduled from an effect there — Chrome only ever renders what it was
 * told is allowed. */
export function Chrome({ route, children }: { route: RouteDef; children: ReactNode }) {
	const { user } = useAuth();
	const [railOpen, setRailOpen] = useState(readRailOpen);

	useEffect(() => {
		try {
			window.localStorage.setItem(RAIL_STORAGE_KEY, railOpen ? "1" : "0");
		} catch {
			// best-effort
		}
	}, [railOpen]);

	if (!user) {
		return <>{children}</>;
	}

	return (
		<div className="chrome">
			<TopBar screenTitle={route.title} />
			<div className="chrome__body">
				<LeftRail open={railOpen} onToggle={() => setRailOpen((v) => !v)} />
				<main className="chrome__main">{children}</main>
			</div>
			<JobLogDrawer />
		</div>
	);
}
