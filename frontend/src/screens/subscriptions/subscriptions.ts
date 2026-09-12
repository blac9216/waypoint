/**
 * Subscription editor data layer (issue #1473, epic #1182). Wired against
 * `SubscriptionsController` (issue #1450, `docs/reference/api-contract.md`
 * "Subscriptions & presets"): `GET/POST /subscriptions`,
 * `GET/PUT /subscriptions/{id}`. Shapes below mirror `SubscriptionDtos.cs`
 * field-for-field.
 *
 * Note on scope vs. the issue's original "line pickers ... projected-size
 * display" framing (written against design record #1048, before #1450
 * shaped the real domain model): a `Subscription` is one product/lane pair
 * tracked at a single `anchor_version` (`Subscription.cs`), not a bag of
 * many independently added/removed lines, and there is no shipped
 * projected-size API. This module reinterprets both against what #1450
 * actually shipped rather than the pre-#1450 assumption:
 *   - the "line picker" is a single active product/anchor-version pick,
 *     sourced from the real indexed download catalog (`catalog/catalog.ts`'s
 *     `fetchCatalogArtifacts`, the same data-access path `useCatalogPull.ts`
 *     and `DownloadCatalogScreen.tsx` already use) — added by picking a
 *     product+version, removed by clearing the pick, never a multi-select.
 *   - "projected size" is a client-side display estimate only: the sum of
 *     indexed-catalog artifact `size_bytes` for the selected product whose
 *     version falls in the anchor's line, using the same three-width
 *     vocabulary `SubscriptionLineGranularity`'s own doc comments define
 *     (subminor: first 3 dot segments; minor: first 2; major: first 1).
 *     This is NOT the authoritative evaluator (`ISubscriptionLineEvaluator`
 *     is server-side, and the job that would actually enumerate/acquire a
 *     line is #1046's still-open evaluation job) — it exists purely so the
 *     operator sees a live, non-authoritative sense of scope while picking.
 */
import { apiGet, apiPost, apiPut } from "../../lib/api";
import type { LineGranularity } from "../presets/presets";

export interface SubscriptionWriteFields {
	product: string;
	lane: string;
	line_granularity: LineGranularity;
	anchor_version: string;
	preset_id?: string | null;
	refresh_window_days?: number | null;
	retention_override_days?: number | null;
	is_enabled: boolean;
}

export interface Subscription {
	id: string;
	product: string;
	lane: string;
	line_granularity: LineGranularity;
	anchor_version: string;
	preset_id: string | null;
	refresh_window_days: number | null;
	retention_override_days: number | null;
	is_enabled: boolean;
	created_at: string;
	updated_at: string;
}

export function fetchSubscription(id: string): Promise<Subscription> {
	return apiGet<Subscription>(`/subscriptions/${encodeURIComponent(id)}`);
}

export function createSubscription(fields: SubscriptionWriteFields): Promise<Subscription> {
	return apiPost<Subscription>("/subscriptions", fields);
}

export function updateSubscription(id: string, fields: SubscriptionWriteFields): Promise<Subscription> {
	return apiPut<Subscription>(`/subscriptions/${encodeURIComponent(id)}`, fields);
}

/** First `count` dot-separated numeric segments of `version`, e.g.
 * `versionPrefix("8.0.3.100", 3) === "8.0.3"`. Missing trailing segments are
 * simply absent from the joined result rather than padded — a version with
 * fewer segments than `count` never spuriously matches a longer anchor. */
function versionPrefix(version: string, count: number): string {
	return version.split(".").slice(0, count).join(".");
}

const GRANULARITY_SEGMENT_COUNT: Record<LineGranularity, number> = {
	subminor: 3,
	minor: 2,
	major: 1,
};

/** Display-only line match — see this module's doc comment. Mirrors
 * `SubscriptionLineGranularity`'s doc-commented segment counts, not the
 * server's actual `ISubscriptionLineEvaluator` (which also handles
 * pre-release/build-metadata parsing this client estimate does not). */
export function isInLine(candidateVersion: string, anchorVersion: string, granularity: LineGranularity): boolean {
	const count = GRANULARITY_SEGMENT_COUNT[granularity];
	return versionPrefix(candidateVersion, count) === versionPrefix(anchorVersion, count);
}

/** Sums `size_bytes` for every candidate whose version is in the anchor's
 * line — the projected-size display's whole computation, kept pure and
 * exported so `SubscriptionEditorScreen.test.tsx` can prove it changes
 * when the selection changes without re-deriving the sum inline. */
export function computeProjectedSizeBytes(
	candidates: { version: string; size_bytes: number }[],
	anchorVersion: string,
	granularity: LineGranularity,
): number {
	if (!anchorVersion) {
		return 0;
	}
	return candidates.filter((c) => isInLine(c.version, anchorVersion, granularity)).reduce((sum, c) => sum + c.size_bytes, 0);
}

/** e.g. `3,221,225,472` bytes -> `"3.0 GiB"`. Local to this screen — no
 * existing shared byte-formatter to reuse. */
export function formatBytes(bytes: number): string {
	if (bytes <= 0) {
		return "0 B";
	}
	const units = ["B", "KiB", "MiB", "GiB", "TiB"];
	const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
	const value = bytes / 1024 ** exponent;
	return `${exponent === 0 ? value : value.toFixed(1)} ${units[exponent]}`;
}
