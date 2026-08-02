import type { ComponentType } from "react";
import { Chrome } from "./components/chrome/Chrome";
import { LoginScreen } from "./components/auth/LoginScreen";
import { AuthProvider, useAuth } from "./lib/auth";
import { RouterProvider, useRouter } from "./lib/router";
import { SystemProvider } from "./lib/system";
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

function AppShell() {
	const { status } = useAuth();
	const { route } = useRouter();

	if (status === "restoring") {
		return null;
	}

	if (status !== "signed-in") {
		return <LoginScreen />;
	}

	const Screen = route ? SCREENS[route.key] : DashboardScreen;

	return (
		<Chrome>
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
