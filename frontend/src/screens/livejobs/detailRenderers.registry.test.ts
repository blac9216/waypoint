import { describe, expect, it } from "vitest";
import { GenericJobDetail } from "./detailRenderers";
import { JOB_DETAIL_RENDERERS, resolveJobDetailRenderer } from "./detailRenderers.registry";
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
 * Type-mapping coverage (issue #591 AC: "Every currently supported job type
 * resolves to the correct renderer or documented generic fallback"). Every
 * key checked here is one of `JobCapabilities.Compliance`/`.Download`'s
 * values (backend/Waypoint.Core/Jobs/JobCapabilities.cs) — the closed
 * `job_type` set `jobs_job_type_check` enforces — so this test is a direct
 * assertion against that authoritative set, not a guess at what job types
 * exist.
 */
describe("JOB_DETAIL_RENDERERS / resolveJobDetailRenderer (issue #591)", () => {
	it.each([
		["scan", ScanJobDetail],
		["remediate", RemediateJobDetail],
		["discover", DiscoverJobDetail],
		["credential-test", CredentialTestJobDetail],
		["content-pull", ContentJobDetail],
		["content-import", ContentJobDetail],
		["purge", PurgeJobDetail],
		["catalog-index", CatalogIndexJobDetail],
		["download", DownloadJobDetail],
		["bundle-export", BundleJobDetail],
		["bundle-import", BundleJobDetail],
		["content-library-sync", ContentLibrarySyncJobDetail],
		["update", UpdateJobDetail],
		["tool-install", DownloadJobDetail],
	] as const)("maps job_type %s to its registered renderer", (jobType, expected) => {
		expect(JOB_DETAIL_RENDERERS[jobType]).toBe(expected);
		expect(resolveJobDetailRenderer(jobType)).toBe(expected);
	});

	it("falls through to GenericJobDetail for an unregistered/future job_type", () => {
		expect(JOB_DETAIL_RENDERERS["some-future-type"]).toBeUndefined();
		expect(resolveJobDetailRenderer("some-future-type")).toBe(GenericJobDetail);
	});

	it("covers every job_type in JobCapabilities.cs's closed set (Compliance + Download) with a registered renderer", () => {
		// Mirrors backend/Waypoint.Core/Jobs/JobCapabilities.cs's two allowlists
		// verbatim — a future job_type added there without a corresponding
		// registry entry falls through to GenericJobDetail (a safe, documented
		// fallback per the AC), which this test does not treat as a failure;
		// it only guards that today's known set is NOT silently generic.
		const COMPLIANCE = ["discover", "credential-test", "scan", "remediate", "content-pull", "content-import", "purge"];
		const DOWNLOAD = ["catalog-index", "download", "bundle-export", "bundle-import", "content-library-sync", "update", "tool-install"];
		for (const jobType of [...COMPLIANCE, ...DOWNLOAD]) {
			expect(resolveJobDetailRenderer(jobType)).not.toBe(GenericJobDetail);
		}
	});
});
