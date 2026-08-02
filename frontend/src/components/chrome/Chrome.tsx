import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "../../lib/auth";
import { canAccessRoute, DEFAULT_ROUTE, useRouter } from "../../lib/router";
import { useSystem } from "../../lib/system";
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
 * Also owns the screen-level role/mode guard (README "Roles & Permissions":
 * "changing role while inside a screen the new role cannot access redirects
 * to Dashboard. Do not rely on gating the nav entry point alone."). */
export function Chrome({ children }: { children: ReactNode }) {
	const { user } = useAuth();
	const { route, navigate } = useRouter();
	const { system } = useSystem();
	const [railOpen, setRailOpen] = useState(readRailOpen);

	useEffect(() => {
		try {
			window.localStorage.setItem(RAIL_STORAGE_KEY, railOpen ? "1" : "0");
		} catch {
			// best-effort
		}
	}, [railOpen]);

	useEffect(() => {
		if (!user) {
			return;
		}
		const mode = system?.mode ?? null;
		const current = route ?? DEFAULT_ROUTE;
		if (!canAccessRoute(current, user.role, mode)) {
			navigate(DEFAULT_ROUTE.path);
		}
	}, [user, route, system, navigate]);

	if (!user) {
		return <>{children}</>;
	}

	const screenTitle = route?.title ?? DEFAULT_ROUTE.title;

	return (
		<div className="chrome">
			<TopBar screenTitle={screenTitle} />
			<div className="chrome__body">
				<LeftRail open={railOpen} onToggle={() => setRailOpen((v) => !v)} />
				<main className="chrome__main">{children}</main>
			</div>
			<JobLogDrawer />
		</div>
	);
}
