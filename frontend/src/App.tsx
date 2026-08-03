import { useEffect, type ComponentType } from "react";
import { Chrome } from "./components/chrome/Chrome";
import { LoginScreen } from "./components/auth/LoginScreen";
import { AuthProvider, useAuth } from "./lib/auth";
import { DEFAULT_ROUTE, evaluateRouteAccess, RouterProvider, useRouter } from "./lib/router";
import { SystemProvider, useSystem } from "./lib/system";
import { ThemeProvider } from "./lib/theme";
import {
	BenchmarksScreen,
	CatalogScreen,
	ConfigurationScreen,
	DashboardScreen,
	LibraryScreen,
	LiveRunScreen,
	ResultsScreen,
	StartScanScreen,
	TransferScreen,
} from "./screens/screens";

const SCREENS: Record<string, ComponentType> = {
	dashboard: DashboardScreen,
	"live-run": LiveRunScreen,
	"start-scan": StartScanScreen,
	results: ResultsScreen,
	benchmarks: BenchmarksScreen,
	catalog: CatalogScreen,
	library: LibraryScreen,
	transfer: TransferScreen,
	configuration: ConfigurationScreen,
};

/** Owns the screen-level role/mode guard (README "Roles & Permissions":
 * "changing role while inside a screen the new role cannot access redirects
 * to Dashboard. Do not rely on gating the nav entry point alone.") — and
 * owns it here, at render time, rather than as a post-mount effect (issue
 * #78). `effectiveRoute` is computed BEFORE `Screen` is looked up, so a
 * route the current role/mode may not access never becomes a `<Screen />`
 * element in the first place: the disallowed component is never
 * instantiated, not even for one frame, and the top bar title never flashes
 * the wrong screen either since `Chrome` renders `effectiveRoute.title`.
 * The actual URL correction is still a `navigate()` call inside a
 * `useEffect` (history mutation is an established side effect, not a
 * render), it just no longer gates *what renders* — only what the address
 * bar eventually shows.
 *
 * `access` is `evaluateRouteAccess`'s tri-state, not a boolean (issue #82):
 * a `connectedOnly` route whose mode isn't known yet is `"pending"`, and
 * AppShell renders neither the requested screen nor the DEFAULT_ROUTE
 * fallback while pending — see the `access === "pending"` branch below for
 * why both alternatives are wrong. */
function AppShell() {
	const { status, user } = useAuth();
	const { route, navigate } = useRouter();
	const { mode } = useSystem();

	const requestedRoute = route ?? DEFAULT_ROUTE;
	const access = user ? evaluateRouteAccess(requestedRoute, user.role, mode) : "allowed";
	const effectiveRoute = access === "allowed" ? requestedRoute : DEFAULT_ROUTE;

	useEffect(() => {
		if (status === "signed-in" && user && access === "denied") {
			navigate(DEFAULT_ROUTE.path);
		}
	}, [status, user, access, navigate]);

	if (status === "restoring") {
		return null;
	}

	if (status !== "signed-in" || !user) {
		return <LoginScreen />;
	}

	if (access === "pending") {
		// Mode not yet known for a connectedOnly route (issue #82). Deciding
		// either way here is wrong: rendering `requestedRoute`'s screen risks
		// mounting a connected-only screen for a frame on an appliance that
		// turns out to be disconnected (exactly the #78 mount property this
		// whole guard exists to protect); substituting DEFAULT_ROUTE and
		// navigating away (the old behavior) is #82 itself — a deep link
		// bounced before the answer could ever be known. So: render nothing,
		// the same blank-pending treatment `status === "restoring"` already
		// uses above, until `/api/v1/system` resolves and `access` becomes
		// "allowed" or "denied". This only affects the one connectedOnly
		// route (`/catalog` today) during the brief window right after
		// sign-in; every other route settles synchronously from role alone.
		return null;
	}

	const Screen = SCREENS[effectiveRoute.key];

	return (
		<Chrome route={effectiveRoute}>
			<Screen />
		</Chrome>
	);
}

export default function App() {
	return (
		<ThemeProvider>
			<AuthProvider>
				<SystemProvider>
					<RouterProvider>
						<AppShell />
					</RouterProvider>
				</SystemProvider>
			</AuthProvider>
		</ThemeProvider>
	);
}
