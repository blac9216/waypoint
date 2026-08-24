/**
 * Job-detail renderer registry (ADR-0019 decision 2 / issue #591) — the seam
 * `detailRenderers.tsx` (issue #590) documented: a `job_type`-keyed map of
 * renderer components, with `GenericJobDetail` as the explicit fallback for
 * any type not in the map (including future types the closed `job_type` set
 * grows to hold — see `JobCapabilities.cs` for the authoritative set this
 * registry is keyed against today).
 *
 * Split into its own module (issue #692) so this file exports only
 * non-component values (`JOB_DETAIL_RENDERERS`, `resolveJobDetailRenderer`)
 * and `detailRenderers.tsx` exports only the `GenericJobDetail` component —
 * oxlint's `react(only-export-components)` flagged the prior single-file mix
 * twice (a `Record`/function living beside a component breaks Fast Refresh's
 * "this file only exports components" assumption). Per-type renderer
 * components live in sibling files grouped by owning domain
 * (`complianceRenderers.tsx`, `operationalRenderers.tsx`) and are imported
 * here only to populate the map — never re-exported from this module.
 */
import { GenericJobDetail, type JobDetailRenderer } from "./detailRenderers";
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
 * Renderer registry keyed by `job_type` — every key here is one of
 * `JobCapabilities.Compliance`/`JobCapabilities.Download`'s values
 * (backend/Waypoint.Core/Jobs/JobCapabilities.cs), the closed set
 * `jobs_job_type_check` enforces. A `job_type` with no entry (including any
 * future addition to that closed set that lands before this map is updated)
 * falls through to `GenericJobDetail` via `resolveJobDetailRenderer` — never
 * throws, never guesses a renderer.
 */
export const JOB_DETAIL_RENDERERS: Record<string, JobDetailRenderer> = {
	// Compliance domain (compliance-runner allowlist) — stage/finding/
	// attestation/artifact presentation stays with compliance renderers
	// (complianceRenderers.tsx), which link out to the compliance-owned
	// Results screen rather than duplicating its tables.
	scan: ScanJobDetail,
	remediate: RemediateJobDetail,
	discover: DiscoverJobDetail,
	"credential-test": CredentialTestJobDetail,
	"content-pull": ContentJobDetail,
	"content-import": ContentJobDetail,
	// Purge is compliance-adjacent (deletes scan-artifact volume state,
	// JobCapabilities.Compliance) but is operational history maintenance, not
	// a domain outcome (ADR-0019 decision 4: "deleting operational history
	// never implicitly deletes domain state") — a concise summary, not full
	// Results-panel treatment.
	purge: PurgeJobDetail,

	// Download domain (download-runner allowlist) — concise operational
	// summaries + links to Catalog/Library/Transfer. `HadErrors` on these
	// (issue #612) is advisory-only and must never render as a failure.
	"catalog-index": CatalogIndexJobDetail,
	download: DownloadJobDetail,
	"bundle-export": BundleJobDetail,
	"bundle-import": BundleJobDetail,
	"content-library-sync": ContentLibrarySyncJobDetail,
	update: UpdateJobDetail,
	"tool-install": DownloadJobDetail,
};

export function resolveJobDetailRenderer(jobType: string): JobDetailRenderer {
	return JOB_DETAIL_RENDERERS[jobType] ?? GenericJobDetail;
}
