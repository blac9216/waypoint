import { useAuth } from "../lib/auth";
import { roleGateProps } from "../lib/roles";
import { PlaceholderScreen } from "./PlaceholderScreen";
import { DownloadCatalogScreen } from "./catalog/DownloadCatalogScreen";

export function DashboardScreen() {
	return <PlaceholderScreen title="Dashboard" reads={["GET /api/v1/dashboard", "global SSE /api/v1/events"]} />;
}

export function LiveRunScreen() {
	return (
		<PlaceholderScreen
			title="Live Run"
			reads={["GET /api/v1/runs/{id}", "GET /api/v1/runs/{id}/jobs", "SSE /api/v1/runs/{id}/events"]}
		/>
	);
}

export function StartScanScreen() {
	return (
		<PlaceholderScreen
			title="Start a Scan"
			reads={[
				"GET /api/v1/sites",
				"GET /api/v1/targets",
				"GET /api/v1/targets/{id}/inventory",
				"GET /api/v1/profiles",
				"GET /api/v1/credentials",
			]}
		/>
	);
}

export function ResultsScreen() {
	const { user } = useAuth();
	const gate = user ? roleGateProps(user.role, "Admin", "Requires Admin — remediation is not available to your role") : { disabled: true };
	return (
		<PlaceholderScreen
			title="Results & History"
			reads={["GET /api/v1/runs", "GET /api/v1/runs/{id}", "GET /api/v1/runs/{id}/attestations-applied"]}
		>
			{/* Demonstrates the README "Roles & Permissions" treatment: visible
			    but disabled at opacity 0.42 with a reason, never hidden. */}
			<button type="button" {...gate} style={{ ...gate.style, marginTop: 6 }}>
				Remediate findings…
			</button>
		</PlaceholderScreen>
	);
}

export function BenchmarksScreen() {
	return (
		<PlaceholderScreen
			title="Benchmarks"
			reads={["GET /api/v1/profiles", "GET /api/v1/profiles/{id}/controls", "GET /api/v1/config-docs"]}
		/>
	);
}

export function CatalogScreen() {
	return <DownloadCatalogScreen />;
}

export function LibraryScreen() {
	return <PlaceholderScreen title="Library" reads={["GET /api/v1/library/items", "GET /api/v1/content-library/items"]} />;
}

export function TransferScreen() {
	return <PlaceholderScreen title="Transfer" reads={["GET /api/v1/bundles"]} />;
}

export function ConfigurationScreen() {
	return (
		<PlaceholderScreen
			title="Configuration"
			reads={[
				"GET /api/v1/sites",
				"GET /api/v1/targets",
				"GET /api/v1/credentials",
				"GET /api/v1/stigman",
				"GET /api/v1/compliance-content",
				"GET /api/v1/users",
			]}
		/>
	);
}
