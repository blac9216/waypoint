import { useEffect, type ComponentType } from "react";
import { Chrome } from "./components/chrome/Chrome";
import { LoginScreen } from "./components/auth/LoginScreen";
import { AuthProvider, useAuth } from "./lib/auth";
import { canAccessRoute, DEFAULT_ROUTE, RouterProvider, useRouter } from "./lib/router";
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
 * bar eventually shows. */
function AppShell() {
	const { status, user } = useAuth();
	const { route, navigate } = useRouter();
	const { system } = useSystem();

	const mode = system?.mode ?? null;
	const requestedRoute = route ?? DEFAULT_ROUTE;
	const allowed = user ? canAccessRoute(requestedRoute, user.role, mode) : true;
	const effectiveRoute = allowed ? requestedRoute : DEFAULT_ROUTE;

	useEffect(() => {
		if (status === "signed-in" && user && !allowed) {
			navigate(DEFAULT_ROUTE.path);
		}
	}, [status, user, allowed, navigate]);

	if (status === "restoring") {
		return null;
	}

	if (status !== "signed-in" || !user) {
		return <LoginScreen />;
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
