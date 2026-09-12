/**
 * Retention review-list screen data layer (issue #1481, epic #1182). Wired
 * against `RetentionController` (issue #1453, `backend/Waypoint.Api/Controllers/RetentionController.cs`,
 * documented in `docs/reference/api-contract.md` under `/download-retention/*`):
 *
 *   GET  /download-retention/state              — grace/pending-purge/pinned rows
 *   POST /download-retention/{id}/pin           — Admin-only
 *   POST /download-retention/{id}/unpin         — Admin-only
 *   POST /download-retention/{id}/purge-now     — Admin-only
 *   GET  /download-retention/review-list        — orphan/out-of-scope union
 *   DELETE /download-retention/review-list      — Admin-only, the sole deletion path
 *
 * The response shapes below mirror `RetainedContentStateResponse` /
 * `ReviewListEntryResponse` / `PurgeNowResponse` / `DeleteReviewListEntryResponse`
 * in `backend/Waypoint.Api/Contracts/RetentionContracts.cs` field-for-field.
 *
 * Alerts: `docs/reference/api-contract.md`'s own "Download-domain extension, planned"
 * note under `/alerts` records that the `retention_grace_approaching` /
 * `retention_grace_expired` / pinned-informational `kind` values are NOT YET SHIPPED
 * in `AlertKinds` — there is no backend alert row to fetch for them yet. Issue #1481's
 * AC4 ("new alerts — grace-entry, review-list addition — are visible on this screen")
 * is therefore satisfied by deriving alert-shaped entries client-side from the same
 * `state`/`review-list` responses this screen already fetches, not by polling a
 * `/alerts?kind=...` filter that would 400 against today's closed `kind` set. When
 * the real alert kinds ship, this derivation can be replaced by a real fetch without
 * changing the screen's rendering contract.
 */

import { apiDelete, apiGet, apiPost } from "../../lib/api";

/** The three ADR-0034 states this screen ever lists (`RetentionController.ListableStates`) — `tracked`/`purged` are not listable here. */
export type RetainedContentDisplayState = "grace" | "pending-purge" | "pinned";

export interface RetainedContentState {
	id: string;
	depot_artifact_id: string;
	state: string;
	grace_started_at: string | null;
	pinned_by: string | null;
	pinned_at: string | null;
	pin_note: string | null;
	purged_at: string | null;
	created_at: string;
	updated_at: string;
}

export function fetchRetentionState(state?: RetainedContentDisplayState): Promise<RetainedContentState[]> {
	const query = state ? `?state=${encodeURIComponent(state)}` : "";
	return apiGet<RetainedContentState[]>(`/download-retention/state${query}`);
}

export function pinContent(id: string, note?: string): Promise<RetainedContentState> {
	return apiPost<RetainedContentState>(`/download-retention/${id}/pin`, note ? { note } : {});
}

export function unpinContent(id: string): Promise<RetainedContentState> {
	return apiPost<RetainedContentState>(`/download-retention/${id}/unpin`, {});
}

export interface PurgeNowResult {
	retained_content_state_id: string;
	purged: boolean;
	error: string | null;
}

export function purgeNow(id: string, reason?: string): Promise<PurgeNowResult> {
	return apiPost<PurgeNowResult>(`/download-retention/${id}/purge-now`, reason ? { reason } : {});
}

/** `ReviewListEntryKind` on the wire (`Waypoint.Core.Downloads.ReviewListEntryKind`) — closed set, never free text. */
export type ReviewListEntryKind = "Orphan" | "OutOfScope";

export interface ReviewListEntry {
	kind: ReviewListEntryKind;
	depot_artifact_id: string | null;
	relative_path: string;
	size_bytes: number | null;
	reason: string | null;
	first_seen_at: string;
	last_seen_at: string;
}

export function fetchReviewList(): Promise<ReviewListEntry[]> {
	return apiGet<ReviewListEntry[]>("/download-retention/review-list");
}

export interface DeleteReviewListEntryResult {
	deleted: boolean;
	error: string | null;
}

/** Deletes exactly one review-list entry — the sole mutating action this section ever offers (issue #1481 AC3: no bulk auto-action). */
export function deleteReviewListEntry(entry: ReviewListEntry, reason?: string): Promise<DeleteReviewListEntryResult> {
	return apiDelete<DeleteReviewListEntryResult>("/download-retention/review-list", {
		body: {
			kind: entry.kind,
			depot_artifact_id: entry.kind === "OutOfScope" ? entry.depot_artifact_id : undefined,
			relative_path: entry.kind === "Orphan" ? entry.relative_path : undefined,
			reason,
		},
	});
}

/**
 * Elapsed time since `grace_started_at`, rendered as this screen's "countdown"
 * (e.g. "in grace 3d 4h"). The API does not expose the resolved grace-period
 * length on this row or on `GET /download-retention/dial` (that endpoint returns
 * only the manual-download dial, not `RetentionPolicy.GracePeriodDays`) — so a
 * true "time remaining" cannot be computed from any response this screen can
 * fetch today. Showing elapsed time since grace entry is the honest countdown
 * this contract supports; a true remaining-time display can replace this once
 * grace-period length is exposed on the wire.
 */
export function graceElapsedLabel(graceStartedAt: string | null, now: Date = new Date()): string {
	if (!graceStartedAt) {
		return "—";
	}
	const started = new Date(graceStartedAt).getTime();
	const elapsedMs = Math.max(0, now.getTime() - started);
	const totalHours = Math.floor(elapsedMs / (1000 * 60 * 60));
	const days = Math.floor(totalHours / 24);
	const hours = totalHours % 24;
	if (days === 0 && hours === 0) {
		return "in grace <1h";
	}
	if (days === 0) {
		return `in grace ${hours}h`;
	}
	return `in grace ${days}d ${hours}h`;
}

/** One derived, client-side alert row (see this file's header comment). */
export interface DerivedAlert {
	id: string;
	kind: "grace-entry" | "review-list-addition";
	message: string;
	raised_at: string;
}

export function deriveAlerts(graceItems: RetainedContentState[], reviewEntries: ReviewListEntry[]): DerivedAlert[] {
	const graceAlerts: DerivedAlert[] = graceItems
		.filter((item) => item.state === "grace" && item.grace_started_at)
		.map((item) => ({
			id: `grace-entry:${item.id}`,
			kind: "grace-entry" as const,
			message: `Content ${item.depot_artifact_id} entered its grace period.`,
			raised_at: item.grace_started_at as string,
		}));

	const reviewAlerts: DerivedAlert[] = reviewEntries.map((entry) => ({
		id: `review-list-addition:${entry.kind}:${entry.depot_artifact_id ?? entry.relative_path}`,
		kind: "review-list-addition" as const,
		message: `${entry.kind === "Orphan" ? "Orphaned" : "Out-of-scope"} content added to the review list: ${entry.relative_path}.`,
		raised_at: entry.first_seen_at,
	}));

	return [...graceAlerts, ...reviewAlerts].sort((a, b) => (a.raised_at < b.raised_at ? 1 : -1));
}

export function formatBytes(bytes: number | null): string {
	if (bytes === null || !Number.isFinite(bytes) || bytes < 0) {
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
