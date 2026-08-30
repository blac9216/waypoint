/**
 * Download Catalog data layer (docs/api-contract.md "Depot catalog &
 * downloads (connected mode)" + the "Download Catalog" ledger row):
 *
 *   GET  /catalog/artifacts   — indexed depot listing, filterable
 *   POST /catalog/sync        — 202 -> catalog-index job
 *   GET  /downloads           — queue view (rate, ETA, retries)
 *   POST /downloads           — artifact ids -> ONE run containing N jobs
 *                                (PR #228: "one run, N jobs")
 *
 * Progress is never polled — `download.progress` / `job.state` SSE events
 * (docs/api-contract.md "Event streams (SSE)") are the only source for
 * anything that moves on this screen, per the contract's explicit rule.
 */

import { apiGet, apiGetPaged, apiPost } from "../../lib/api";

export type ArtifactStatus = "not_downloaded" | "queued" | "downloading" | "verified" | "failed";

export interface CatalogArtifact {
	id: string;
	name: string;
	sha256: string;
	product: string;
	version: string;
	size_bytes: number;
	status: ArtifactStatus;
	/** Present while `status === "downloading"`; 0-100. */
	progress_percent?: number;
	/** Present when `status === "failed"` — e.g. "checksum mismatch". */
	failure_reason?: string;
}

/**
 * Product grouping (issue #796): the real Broadcom 9.x catalog is 1,088
 * entries dominated by the Kubernetes stack (VKR alone is 433; plus
 * VKS_ and SUPERVISOR_SERVICE_ releases), which drowns the ~40 core-infrastructure
 * products (VCENTER, ESX_HOST, NSX_T_MANAGER, ...) an operator actually
 * looks for. Grouping/ordering/collapse-by-default is entirely a frontend
 * concern — the backend's `product` field is an opaque catalog key with no
 * type or friendly-name metadata (`VendorProductVersionCatalogParser`
 * writes only `product`/`version`/`size_bytes`).
 */
export type ProductType = "core" | "kubernetes";

/** Friendly names for the catalog keys named in issue #796's discovery
 * write-up. Unrecognized keys fall back to `humanizeProductKey` below
 * rather than silently rendering nothing — the full ~49-product catalog is
 * not enumerated here since the backend carries no display-name field to
 * validate a hardcoded list against. */
const KNOWN_PRODUCT_NAMES: Record<string, string> = {
	VCENTER: "vCenter Server",
	ESX_HOST: "ESXi",
	NSX_T_MANAGER: "NSX Manager",
	SDDC_MANAGER_VCF: "SDDC Manager (VCF)",
	VROPS: "Aria/vRealize Operations",
	VRA: "Aria/vRealize Automation",
	VRSLCM: "Aria/vRealize Suite Lifecycle Manager",
	HCX: "HCX",
	VKR: "VKR (Kubernetes Release)",
};

/** `SUPERVISOR_SERVICE_ABC` -> "Supervisor Service ABC"; short (<=3 char)
 * segments (version numbers, acronyms) are upper-cased rather than
 * title-cased. */
function humanizeProductKey(key: string): string {
	return key
		.split("_")
		.filter(Boolean)
		.map((word) => (word.length <= 3 ? word.toUpperCase() : word[0] + word.slice(1).toLowerCase()))
		.join(" ");
}

export function friendlyProductName(productKey: string): string {
	return KNOWN_PRODUCT_NAMES[productKey] ?? humanizeProductKey(productKey);
}

/** VKR itself, every `VKS_*` release, and every `SUPERVISOR_SERVICE_*`
 * component — the Kubernetes-stack bulk called out in issue #796. */
export function isKubernetesProduct(productKey: string): boolean {
	return productKey === "VKR" || productKey.startsWith("VKS_") || productKey.startsWith("SUPERVISOR_SERVICE_");
}

export function productType(productKey: string): ProductType {
	return isKubernetesProduct(productKey) ? "kubernetes" : "core";
}

/** Core-infrastructure products named in issue #796, shown first and in
 * this order; every other core product follows, alphabetically by friendly
 * name, then Kubernetes-stack products last, also alphabetically. */
const CORE_PRODUCT_PRIORITY = ["VCENTER", "ESX_HOST", "NSX_T_MANAGER", "SDDC_MANAGER_VCF", "VROPS", "VRA", "VRSLCM", "HCX"];

export interface ProductGroup {
	key: string;
	friendlyName: string;
	type: ProductType;
	artifacts: CatalogArtifact[];
	versionCount: number;
}

/** Groups artifacts by their `product` catalog key, with a per-group
 * version count (distinct `version` values) and default ordering:
 * `CORE_PRODUCT_PRIORITY` first, remaining core products alphabetically by
 * friendly name, Kubernetes-stack products last, also alphabetically. */
export function groupArtifactsByProduct(artifacts: CatalogArtifact[]): ProductGroup[] {
	const byKey = new Map<string, CatalogArtifact[]>();
	for (const artifact of artifacts) {
		const list = byKey.get(artifact.product);
		if (list) {
			list.push(artifact);
		} else {
			byKey.set(artifact.product, [artifact]);
		}
	}

	const groups: ProductGroup[] = Array.from(byKey.entries()).map(([key, groupArtifacts]) => ({
		key,
		friendlyName: friendlyProductName(key),
		type: productType(key),
		artifacts: groupArtifacts,
		versionCount: new Set(groupArtifacts.map((a) => a.version)).size,
	}));

	return groups.sort((a, b) => {
		if (a.type !== b.type) {
			return a.type === "core" ? -1 : 1;
		}
		if (a.type === "core") {
			const ai = CORE_PRODUCT_PRIORITY.indexOf(a.key);
			const bi = CORE_PRODUCT_PRIORITY.indexOf(b.key);
			if (ai !== -1 || bi !== -1) {
				return (ai === -1 ? CORE_PRODUCT_PRIORITY.length : ai) - (bi === -1 ? CORE_PRODUCT_PRIORITY.length : bi);
			}
		}
		return a.friendlyName.localeCompare(b.friendlyName);
	});
}

export interface CatalogArtifactsResponse {
	artifacts: CatalogArtifact[];
	/** Last successful `catalog-index` job completion, ISO-8601. Always `null`
	 * today — see the note on `fetchCatalogArtifacts` below; kept as a typed
	 * field (rather than dropped) so a future backend addition is a
	 * no-frontend-change wire-up, not a new field to invent client shape for. */
	index_synced_at: string | null;
}

export interface CatalogArtifactsQuery {
	search?: string;
	product?: string;
	version?: string;
	status?: ArtifactStatus;
}

/**
 * `CatalogController.ListArtifacts` binds `[FromQuery] PageRequest page`
 * (`DefaultLimit = 50`, `MaxLimit = 200` — `Waypoint.Core.Pagination.PageRequest`),
 * so a single unpaged request returns at most 50 of the real catalog's 1,088
 * rows. `fetchCatalogArtifactsPage` below always requests the backend's
 * `MaxLimit` per page to keep the page count (and therefore the fetch time)
 * as low as the contract allows.
 */
const ARTIFACTS_PAGE_LIMIT = 200;

/**
 * Adapts the real `CatalogController.ListArtifacts` response, which is a
 * bare `CatalogArtifactResponse[]` (`return Ok(items...)` — no envelope, no
 * `index_synced_at`, and no `search` query binding), to this module's
 * `{artifacts, index_synced_at}` shape. Found live by the issue #468
 * Playwright suite: this function previously assumed the api-contract.md
 * envelope shape directly (`apiGet<CatalogArtifactsResponse>(...)`), which
 * silently returned `res.artifacts === undefined` against the real backend
 * and crashed `DownloadCatalogScreen`'s `useMemo` on `.map` over `undefined`
 * — invisible to `npm test`'s mocked-fetch unit tests, which only ever
 * exercised the shape this code assumed, never the shape the backend
 * actually sends. `search` is filtered client-side below since the backend
 * has no `search` query parameter to send it to; `product`/`version`/
 * `status` DO bind server-side (`ListArtifacts`'s query parameters) and are
 * still sent as query params.
 */
function fetchCatalogArtifactsPage(query: CatalogArtifactsQuery, offset: number) {
	const params = new URLSearchParams();
	if (query.product) params.set("product", query.product);
	if (query.version) params.set("version", query.version);
	if (query.status) params.set("status", query.status);
	params.set("limit", String(ARTIFACTS_PAGE_LIMIT));
	params.set("offset", String(offset));
	return apiGetPaged<CatalogArtifact>(`/catalog/artifacts?${params.toString()}`);
}

/**
 * Fetches every page of `/catalog/artifacts` (issue #796 finding 1: the
 * catalog is 1,088 rows against a 50-row default / 200-row max page, and
 * this module's grouping/counts/filters are meant to describe the whole
 * indexed catalog, not one page of it). Pages sequentially — the endpoint
 * has no documented concurrent-request guarantee, and this keeps `offset`
 * math trivial — accumulating until the running total meets the
 * `X-Total-Count` the first response reports, or a page comes back short
 * (defensive: the total could legitimately shrink between requests if the
 * catalog is re-indexed mid-fetch).
 */
export function fetchCatalogArtifacts(query: CatalogArtifactsQuery = {}): Promise<CatalogArtifactsResponse> {
	const collected: CatalogArtifact[] = [];

	async function loadFrom(offset: number): Promise<CatalogArtifact[]> {
		const { items, totalCount } = await fetchCatalogArtifactsPage(query, offset);
		collected.push(...items);
		const nextOffset = offset + items.length;
		if (items.length < ARTIFACTS_PAGE_LIMIT || nextOffset >= totalCount) {
			return collected;
		}
		return loadFrom(nextOffset);
	}

	return loadFrom(0).then((artifacts) => {
		const search = query.search?.trim().toLowerCase();
		const filtered = search
			? artifacts.filter((a) => a.name.toLowerCase().includes(search) || a.sha256.toLowerCase().includes(search))
			: artifacts;
		return { artifacts: filtered, index_synced_at: null };
	});
}

export interface CatalogSyncResponse {
	job_id: string;
}

export function syncCatalog(): Promise<CatalogSyncResponse> {
	return apiPost<CatalogSyncResponse>("/catalog/sync");
}

/**
 * `GET /catalog/pull` (issue #687): connected vendor catalog-pull readiness
 * plus the most recent attempt/success facts. Distinct from `catalog-index`
 * (`syncCatalog` above) — this contacts Broadcom via the installed managed
 * tool and the stored Activation Code; `not_ready_reason` names exactly which
 * #691 enrollment prerequisite is unmet, so the screen never has to guess a
 * reason on its own. Nullable fields are omitted on the wire, not `null`
 * (`CatalogPullStatusResponse.FromDomain`), hence optional here.
 */
export interface CatalogPullStatus {
	ready: boolean;
	not_ready_reason?: string;
	last_attempt_at?: string;
	last_outcome?: "succeeded" | "failed" | "auth_failed";
	last_failure_reason?: string;
	last_success_at?: string;
	last_success_item_count?: number;
}

export function fetchCatalogPullStatus(): Promise<CatalogPullStatus> {
	return apiGet<CatalogPullStatus>("/catalog/pull");
}

export interface CatalogPullStartedResponse {
	run_id: string;
	job_id: string;
}

/** `POST /catalog/pull` (Admin) — 202 `{run_id, job_id}` to follow via SSE
 * (see useCatalogPull.ts), or 409 `catalog_pull_not_ready` if raced against
 * the enrollment gate (surfaced via the thrown `ApiError`'s `message`). */
export function pullCatalog(): Promise<CatalogPullStartedResponse> {
	return apiPost<CatalogPullStartedResponse>("/catalog/pull");
}

export interface DownloadQueueItem {
	id: string;
	artifact_id: string;
	job_id: string;
	run_id: string;
	state: "queued" | "downloading" | "verifying" | "verified" | "failed";
	progress_percent: number;
	rate_bytes_per_sec: number | null;
	eta_seconds: number | null;
	retries: number;
}

export function fetchDownloadQueue(): Promise<DownloadQueueItem[]> {
	return apiGet<DownloadQueueItem[]>("/downloads");
}

export interface QueueDownloadsResponse {
	run_id: string;
	job_ids: string[];
}

/** One run, N jobs (PR #228) — the whole selection is a single POST. */
export function queueDownloads(artifactIds: string[]): Promise<QueueDownloadsResponse> {
	return apiPost<QueueDownloadsResponse>("/downloads", { artifact_ids: artifactIds });
}

/** `GET /system`'s disk-usage-by-store fields (api-contract.md "System, users,
 * audit": "disk usage by store"). Issue #226 is landing these on the backend
 * concurrently — every field is optional so a build against this contract
 * renders a graceful loading/empty state instead of crashing or fabricating
 * numbers if the backend hasn't shipped them yet. */
export interface StoreUsage {
	name: string;
	used_bytes: number;
	total_bytes: number;
}

export interface SystemDiskUsage {
	stores?: StoreUsage[];
}

export function formatBytes(bytes: number): string {
	if (!Number.isFinite(bytes) || bytes < 0) {
		return "—";
	}
	const units = ["B", "KB", "MB", "GB", "TB"];
	let value = bytes;
	let unitIndex = 0;
	while (value >= 1024 && unitIndex < units.length - 1) {
		value /= 1024;
		unitIndex += 1;
	}
	const precision = unitIndex === 0 ? 0 : 1;
	return `${value.toFixed(precision)} ${units[unitIndex]}`;
}

export function formatRate(bytesPerSec: number | null): string {
	if (bytesPerSec === null || !Number.isFinite(bytesPerSec)) {
		return "—";
	}
	return `${formatBytes(bytesPerSec)}/s`;
}

export function formatEta(seconds: number | null): string {
	if (seconds === null || !Number.isFinite(seconds)) {
		return "—";
	}
	if (seconds < 60) {
		return `${Math.round(seconds)}s`;
	}
	const minutes = Math.floor(seconds / 60);
	const rest = Math.round(seconds % 60);
	return `${minutes}m ${rest}s`;
}

/**
 * Conservative assumed download bandwidth (bytes/sec), used to estimate the
 * selection footer's transfer time when the queue has no live aggregate
 * rate to measure from (nothing currently downloading). ~10 Mbps — a
 * deliberately modest guess for a DoD network path to an internet depot;
 * it is meant to avoid an overconfident estimate, not to model any
 * particular circuit. Documented here rather than inferred silently so a
 * future tuning pass has one place to change it.
 */
export const ASSUMED_BANDWIDTH_BYTES_PER_SEC = 1_250_000; // 10 Mbps

/**
 * Selection-footer transfer estimate (docs/ui/prototype/README.md screen 6:
 * "selection count, total size, transfer estimate, Clear, and Queue N
 * downloads"; the prototype HTML renders "est. 24m at 13 MB/s"). Prefers
 * the queue's live aggregate rate (sum of `rate_bytes_per_sec` across
 * actively-downloading jobs) when one exists, since that reflects real
 * throughput; falls back to `ASSUMED_BANDWIDTH_BYTES_PER_SEC` otherwise.
 */
export function formatTransferEstimate(selectedBytes: number, liveRateBytesPerSec: number): string | null {
	if (!Number.isFinite(selectedBytes) || selectedBytes <= 0) {
		return null;
	}
	const rate = liveRateBytesPerSec > 0 ? liveRateBytesPerSec : ASSUMED_BANDWIDTH_BYTES_PER_SEC;
	const seconds = selectedBytes / rate;
	return `est. ${formatDuration(seconds)} at ${formatRate(rate)}`;
}

function formatDuration(seconds: number): string {
	if (seconds < 60) {
		return "<1m";
	}
	const totalMinutes = Math.round(seconds / 60);
	if (totalMinutes < 60) {
		return `${totalMinutes}m`;
	}
	const hours = Math.floor(totalMinutes / 60);
	const minutes = totalMinutes % 60;
	return minutes === 0 ? `${hours}hr` : `${hours}hr ${minutes}m`;
}
