/**
 * Retention review-list screen data layer (issue #1481). Wired against
 * `RetentionController` (issue #1453, `docs/reference/api-contract.md`
 * `/download-retention/*`): `GET state`, `POST {id}/pin`/`unpin`/`purge-now`
 * (Admin-only), `GET`/`DELETE review-list` (delete Admin-only). Shapes below
 * mirror `RetentionContracts.cs` field-for-field.
 *
 * Alerts: the api-contract's own "planned" note under `/alerts` records that
 * `retention_grace_approaching`/`retention_grace_expired` `kind` values are
 * NOT YET SHIPPED in `AlertKinds` — there is no backend alert row for them
 * yet. AC4 ("grace-entry/review-list-addition alerts visible") is satisfied
 * by deriving alert rows client-side from the state/review-list responses
 * already fetched here, rather than polling a `kind` value that would 400
 * against today's closed set.
 */

import { apiDelete, apiGet, apiPost } from "../../lib/api";

/** The three ADR-0034 states this screen ever lists (`RetentionController.ListableStates`) — `tracked`/`purged` are not listable here. */
export type RetainedContentDisplayState = "grace" | "pending-purge" | "pinned";

export interface RetainedContentState {
	id: string;
	depot_artifact_id: string;
	state: string;
	grace_started_at: string | null;
	/**
	 * Issue #1962: absolute timestamp at which this row's grace period ends
	 * (`grace_started_at` + the resolved grace-period length — the SAME source
	 * the purge scheduler uses). Present only for `grace`-state rows with a
	 * resolvable policy; null/absent for `pending-purge`/`pinned` and any row
	 * with no resolvable policy. This is what makes a true time-remaining
	 * countdown possible.
	 */
	grace_ends_at: string | null;
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
 * True time-remaining countdown until this row's grace period ends, computed
 * from `grace_ends_at` (issue #1962: absolute end timestamp = grace start + the
 * resolved grace-period length, the SAME source the purge scheduler uses).
 * Rendered as e.g. "3d 4h left". When `grace_ends_at` is null/absent (a
 * `pending-purge`/`pinned` row, or any row with no resolvable policy) there is
 * nothing to count down to, so returns "—". When the end is already in the past
 * (or now), the purge is due, so returns "past due" rather than a negative
 * value.
 */
export function graceCountdownLabel(graceEndsAt: string | null, now: Date = new Date()): string {
	if (!graceEndsAt) {
		return "—";
	}
	const ends = new Date(graceEndsAt).getTime();
	if (!Number.isFinite(ends)) {
		return "—";
	}
	const remainingMs = ends - now.getTime();
	if (remainingMs <= 0) {
		return "past due";
	}
	const totalHours = Math.floor(remainingMs / (1000 * 60 * 60));
	const days = Math.floor(totalHours / 24);
	const hours = totalHours % 24;
	if (days === 0 && hours === 0) {
		return "<1h left";
	}
	if (days === 0) {
		return `${hours}h left`;
	}
	return `${days}d ${hours}h left`;
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
