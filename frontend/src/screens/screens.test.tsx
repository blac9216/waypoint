import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import {
	AuditScreen,
	BenchmarksScreen,
	CatalogScreen,
	ConfigurationScreen,
	DashboardScreen,
	LibraryScreen,
	LiveJobsScreen,
	LiveRunScreen,
	ResultsScreen,
	StartScanScreen,
	TransferScreen,
} from "./screens";

/**
 * Issue #1314: `screens.tsx` is pure routing glue — each export renders
 * exactly one real screen component under an aliased name so `src/lib/
 * routes.ts` has a stable, documented import surface. It sat at 45% line
 * coverage once `src/screens/**` was included in the gate purely because
 * nothing ever rendered these thin wrappers. The heavy real screens are
 * mocked out here (they carry their own dedicated test suites); this file
 * only pins that each wrapper renders its real counterpart.
 */
vi.mock("./audit/AuditScreen", () => ({ AuditScreen: () => <div>audit-screen</div> }));
vi.mock("./benchmarks/BenchmarksScreen", () => ({ BenchmarksScreen: () => <div>benchmarks-screen</div> }));
vi.mock("./catalog/DownloadCatalogScreen", () => ({ DownloadCatalogScreen: () => <div>catalog-screen</div> }));
vi.mock("./configuration/ConfigurationScreen", () => ({ ConfigurationScreen: () => <div>configuration-screen</div> }));
vi.mock("./dashboard/DashboardScreen", () => ({ DashboardScreen: () => <div>dashboard-screen</div> }));
vi.mock("./library/LibraryScreen", () => ({ LibraryScreen: () => <div>library-screen</div> }));
vi.mock("./livejobs/LiveJobsScreen", () => ({ LiveJobsRoute: () => <div>livejobs-screen</div> }));
vi.mock("./liverun/LiveRunScreen", () => ({ LiveRunRoute: () => <div>liverun-screen</div> }));
vi.mock("./results/ResultsScreen", () => ({ ResultsScreen: () => <div>results-screen</div> }));
vi.mock("./startscan/StartScanScreen", () => ({ StartScanScreen: () => <div>startscan-screen</div> }));

describe("screens.tsx wrapper exports", () => {
	it.each([
		[DashboardScreen, "dashboard-screen"],
		[LiveJobsScreen, "livejobs-screen"],
		[LiveRunScreen, "liverun-screen"],
		[StartScanScreen, "startscan-screen"],
		[ResultsScreen, "results-screen"],
		[BenchmarksScreen, "benchmarks-screen"],
		[CatalogScreen, "catalog-screen"],
		[LibraryScreen, "library-screen"],
		[ConfigurationScreen, "configuration-screen"],
		[AuditScreen, "audit-screen"],
	] as const)("renders its aliased real screen", (Wrapper, marker) => {
		render(<Wrapper />);
		expect(screen.getByText(marker)).toBeInTheDocument();
	});

	it("TransferScreen renders the placeholder with its documented read", () => {
		render(<TransferScreen />);
		expect(screen.getByText("Transfer")).toBeInTheDocument();
		expect(screen.getByText("GET /api/v1/bundles")).toBeInTheDocument();
	});
});
