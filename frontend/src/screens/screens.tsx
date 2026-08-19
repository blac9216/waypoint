import { PlaceholderScreen } from "./PlaceholderScreen";
import { DownloadCatalogScreen } from "./catalog/DownloadCatalogScreen";
import { ConfigurationScreen as SitesTargetsConfigurationScreen } from "./configuration/ConfigurationScreen";
import { DashboardScreen as DashboardAggregateScreen } from "./dashboard/DashboardScreen";
import { LiveRunRoute } from "./liverun/LiveRunScreen";
import { ResultsScreen as ResultsHistoryScreen } from "./results/ResultsScreen";
import { StartScanScreen as StartScanWizardScreen } from "./startscan/StartScanScreen";

export function DashboardScreen() {
	return <DashboardAggregateScreen />;
}

export function LiveRunScreen() {
	return <LiveRunRoute />;
}

export function StartScanScreen() {
	return <StartScanWizardScreen />;
}

export function ResultsScreen() {
	return <ResultsHistoryScreen />;
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
	return <SitesTargetsConfigurationScreen />;
}
