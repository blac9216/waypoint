import { PlaceholderScreen } from "./PlaceholderScreen";
import { AuditScreen as AuditLogScreen } from "./audit/AuditScreen";
import { BenchmarksScreen as BenchmarksProfileScreen } from "./benchmarks/BenchmarksScreen";
import { DownloadCatalogScreen } from "./catalog/DownloadCatalogScreen";
import { ConfigurationScreen as SitesTargetsConfigurationScreen } from "./configuration/ConfigurationScreen";
import { ContentLibraryScreen as ContentLibraryViewShellScreen } from "./content-library/ContentLibraryScreen";
import { DashboardScreen as DashboardAggregateScreen } from "./dashboard/DashboardScreen";
import { LibraryScreen as RepositoryLibraryScreen } from "./library/LibraryScreen";
import { LiveJobsRoute } from "./livejobs/LiveJobsScreen";
import { LiveRunRoute } from "./liverun/LiveRunScreen";
import { PresetsScreen as PresetsAdoptCloneEditScreen } from "./presets/PresetsScreen";
import { RetentionReviewScreen as RetentionReviewRoute } from "./retention/RetentionReviewScreen";
import { ResultsScreen as ResultsHistoryScreen } from "./results/ResultsScreen";
import { StartScanScreen as StartScanWizardScreen } from "./startscan/StartScanScreen";
import { SubscriptionEditorScreen as SubscriptionCreateEditScreen } from "./subscriptions/SubscriptionEditorScreen";

export function DashboardScreen() {
	return <DashboardAggregateScreen />;
}

export function LiveJobsScreen() {
	return <LiveJobsRoute />;
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
	return <BenchmarksProfileScreen />;
}

export function CatalogScreen() {
	return <DownloadCatalogScreen />;
}

export function LibraryScreen() {
	return <RepositoryLibraryScreen />;
}

export function ContentLibraryScreen() {
	return <ContentLibraryViewShellScreen />;
}

export function RetentionScreen() {
	return <RetentionReviewRoute />;
}

export function PresetsScreen() {
	return <PresetsAdoptCloneEditScreen />;
}

export function SubscriptionEditorScreen() {
	return <SubscriptionCreateEditScreen />;
}

export function TransferScreen() {
	return <PlaceholderScreen title="Transfer" reads={["GET /api/v1/bundles"]} />;
}

export function ConfigurationScreen() {
	return <SitesTargetsConfigurationScreen />;
}

export function AuditScreen() {
	return <AuditLogScreen />;
}
