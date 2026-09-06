import { describe, expect, it } from "vitest";
import { GenericJobDetail } from "./detailRenderers";
import { JOB_DETAIL_RENDERERS, resolveJobDetailRenderer } from "./detailRenderers.registry";
import { ScanJobDetail, RemediateJobDetail, PurgeJobDetail } from "./complianceRenderers";
import {
	DiscoverJobDetail,
	CredentialTestJobDetail,
	ContentJobDetail,
	DownloadJobDetail,
	BinariesDownloadJobDetail,
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
		["binaries-download", BinariesDownloadJobDetail],
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

	it("covers every currently-registered job_type with a non-generic renderer (subset of JobCapabilities.cs's two allowlists)", () => {
		// NOT the full JobCapabilities.Compliance/.Download allowlists (PR #1759
		// review: this test used to claim it was, but its DOWNLOAD array omitted
		// three JobCapabilities.Download members -- depot-enrollment, catalog-pull,
		// retention-sweep -- which have no registered renderer in
		// detailRenderers.registry.ts today and so correctly fall through to
		// GenericJobDetail; asserting them here would fail against the intended
		// behavior, not guard it). This only lists the two allowlists' members that
		// ARE registered in JOB_DETAIL_RENDERERS, so a future renderer removal
		// (rather than a JobCapabilities.cs addition) is what this test catches —
		// a future job_type added to JobCapabilities.cs with no renderer falls
		// through to GenericJobDetail (a safe, documented fallback per the AC),
		// which this test does not, and must not, treat as a failure.
		const COMPLIANCE = ["discover", "credential-test", "scan", "remediate", "content-pull", "content-import", "purge"];
		const DOWNLOAD = [
			"catalog-index",
			"download",
			"binaries-download",
			"bundle-export",
			"bundle-import",
			"content-library-sync",
			"update",
			"tool-install",
		];
		for (const jobType of [...COMPLIANCE, ...DOWNLOAD]) {
			expect(resolveJobDetailRenderer(jobType)).not.toBe(GenericJobDetail);
		}

		// The three JobCapabilities.Download members this test deliberately does
		// NOT cover above — pinned here so a future renderer for one of them
		// updates this list in the same change, rather than leaving a silently
		// stale "not yet covered" claim.
		for (const jobType of ["depot-enrollment", "catalog-pull", "retention-sweep"]) {
			expect(resolveJobDetailRenderer(jobType)).toBe(GenericJobDetail);
		}
	});
});
