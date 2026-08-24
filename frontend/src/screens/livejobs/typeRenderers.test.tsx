import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { RouterProvider } from "../../lib/router";
import type { JobDetailProps } from "./detailRenderers";
import type { LiveJobRow, LiveRunGroup } from "./livejobs";
import { ScanJobDetail, RemediateJobDetail, PurgeJobDetail } from "./complianceRenderers";
import {
	DiscoverJobDetail,
	CredentialTestJobDetail,
	ContentJobDetail,
	DownloadJobDetail,
	CatalogIndexJobDetail,
	BundleJobDetail,
	ContentLibrarySyncJobDetail,
	UpdateJobDetail,
} from "./operationalRenderers";

/**
 * Rendering coverage for each type-specific renderer's summary + domain
 * link (issue #591 AC: "tests cover... each new renderer's summary/link
 * rendering"). Every job here is non-terminal (`state: "running"`) so
 * `GenericJobDetail`'s live-tail log path renders without needing to mock
 * `fetchAllJobEventHistory` (terminal jobs route through that fetch instead
 * — already covered for the shared fallback by `detailRenderers.test.tsx`/
 * `LiveJobsScreen.test.tsx`, not re-tested per type here).
 */
function job(overrides: Partial<LiveJobRow> = {}): LiveJobRow {
	return {
		job_id: "job-1",
		run_id: "run-1",
		job_type: "scan",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "running",
		stage: null,
		attempt_count: 0,
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:01Z",
		finished_at: null,
		lastLogLine: null,
		logLines: [],
		...overrides,
	};
}

function group(overrides: Partial<LiveRunGroup> = {}): LiveRunGroup {
	return {
		run_id: "run-1",
		run_type: "scan",
		state: "running",
		paused: false,
		blocked: false,
		blocked_reason: null,
		scope: '{"site_id":"s-1"}',
		initiated_by: "j.moreno",
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:00Z",
		completed_at: null,
		job_count: 1,
		job_count_completed: 0,
		job_count_failed: 0,
		jobs: [],
		...overrides,
	};
}

function renderRenderer(Renderer: (props: JobDetailProps) => React.ReactElement, props: JobDetailProps) {
	return render(
		<RouterProvider>
			<Renderer {...props} />
		</RouterProvider>,
	);
}

describe("Compliance renderers (issue #591)", () => {
	it("ScanJobDetail shows a compliance kicker, run-progress fact, and a link to Compliance Scan Results", () => {
		renderRenderer(ScanJobDetail, {
			job: job({ job_type: "scan", stage: "attesting" }),
			group: group({ job_count: 4, job_count_completed: 2, job_count_failed: 1 }),
		});
		expect(screen.getByText("Compliance scan")).toBeInTheDocument();
		expect(screen.getByText("2/4 targets complete, 1 failed")).toBeInTheDocument();
		const link = screen.getByRole("link", { name: "View in Compliance Scan Results →" });
		expect(link).toHaveAttribute("href", "/results?run=run-1");
	});

	it("RemediateJobDetail shows a remediation kicker and links to Compliance Scan Results, never implying scheduling", () => {
		renderRenderer(RemediateJobDetail, { job: job({ job_type: "remediate" }), group: group({ run_type: "remediate" }) });
		expect(screen.getByText("Remediation")).toBeInTheDocument();
		expect(screen.getByRole("link", { name: "View in Compliance Scan Results →" })).toBeInTheDocument();
	});

	it("PurgeJobDetail shows an operational-maintenance summary with no domain link (ADR-0019 decision 4)", () => {
		renderRenderer(PurgeJobDetail, { job: job({ job_type: "purge" }), group: group({ run_type: "scan", run_id: "run-9" }) });
		expect(screen.getByText("Run purge")).toBeInTheDocument();
		expect(screen.getByText("run-9")).toBeInTheDocument();
		expect(screen.getByText(/operational maintenance, not a scan\/remediation outcome/)).toBeInTheDocument();
		expect(screen.queryByRole("link")).not.toBeInTheDocument();
	});
});

describe("Operational (non-compliance) renderers (issue #591)", () => {
	it("DiscoverJobDetail links to Sites & Targets in Configuration, no scan-only controls", () => {
		renderRenderer(DiscoverJobDetail, { job: job({ job_type: "discover" }), group: group({ run_type: "discover" }) });
		expect(screen.getByText("Discovery")).toBeInTheDocument();
		expect(screen.getByRole("link", { name: "View discovered targets in Sites & Targets →" })).toHaveAttribute("href", "/config");
	});

	it("CredentialTestJobDetail shows a concise pass/fail summary", () => {
		renderRenderer(CredentialTestJobDetail, {
			job: job({ job_type: "credential-test", state: "done" }),
			group: group({ run_type: "credential-test" }),
		});
		expect(screen.getByText("Credential test")).toBeInTheDocument();
		expect(screen.getAllByText("done").length).toBeGreaterThan(0);
	});

	it("ContentJobDetail links to Compliance Content", () => {
		renderRenderer(ContentJobDetail, { job: job({ job_type: "content-pull" }), group: group({ run_type: "content-pull" }) });
		expect(screen.getByRole("link", { name: "View Compliance Content →" })).toHaveAttribute("href", "/config");
	});

	it("CatalogIndexJobDetail links to the Download Catalog", () => {
		renderRenderer(CatalogIndexJobDetail, { job: job({ job_type: "catalog-index" }), group: group({ run_type: "catalog-index" }) });
		expect(screen.getByRole("link", { name: "View Download Catalog →" })).toHaveAttribute("href", "/catalog");
	});

	it("DownloadJobDetail links to the Library and does not render HadErrors as a failure (issue #612: no such field is read)", () => {
		renderRenderer(DownloadJobDetail, { job: job({ job_type: "download", state: "done" }), group: group({ run_type: "download" }) });
		expect(screen.getByText("Download")).toBeInTheDocument();
		expect(screen.getByRole("link", { name: "View Library →" })).toHaveAttribute("href", "/library");
		expect(screen.queryByText(/error/i)).not.toBeInTheDocument();
	});

	it("BundleJobDetail links to Transfer", () => {
		renderRenderer(BundleJobDetail, { job: job({ job_type: "bundle-export" }), group: group({ run_type: "bundle-export" }) });
		expect(screen.getByRole("link", { name: "View Transfer →" })).toHaveAttribute("href", "/transfer");
	});

	it("ContentLibrarySyncJobDetail links to the Library", () => {
		renderRenderer(ContentLibrarySyncJobDetail, {
			job: job({ job_type: "content-library-sync" }),
			group: group({ run_type: "content-library-sync" }),
		});
		expect(screen.getByRole("link", { name: "View Library →" })).toHaveAttribute("href", "/library");
	});

	it("UpdateJobDetail links to Transfer", () => {
		renderRenderer(UpdateJobDetail, { job: job({ job_type: "update" }), group: group({ run_type: "update" }) });
		expect(screen.getByRole("link", { name: "View Transfer →" })).toHaveAttribute("href", "/transfer");
	});
});
