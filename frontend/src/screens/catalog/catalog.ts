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

import { apiGet, apiPost } from "../../lib/api";

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
export function fetchCatalogArtifacts(query: CatalogArtifactsQuery = {}): Promise<CatalogArtifactsResponse> {
	const params = new URLSearchParams();
	if (query.product) params.set("product", query.product);
	if (query.version) params.set("version", query.version);
	if (query.status) params.set("status", query.status);
	const qs = params.toString();
	return apiGet<CatalogArtifact[]>(`/catalog/artifacts${qs ? `?${qs}` : ""}`).then((artifacts) => {
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
