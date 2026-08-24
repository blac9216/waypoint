/**
 * Typed client for `GET /runs/history` (issue #708/#689, epic #706) — the
 * filtered, keyset-cursor-paged run list that backs the Jobs workspace's
 * History mode. Mirrors `api/jobEventHistory.ts`'s house style (opaque
 * `next_cursor`, closed-set filter passthrough, `ApiError` for 400/404
 * surfacing) since this is the sibling cursor-paged read the same PR adds.
 *
 * Wire shapes matched field-for-field against `RunHistoryListResponse`/
 * `RunResponse` (backend/Waypoint.Api/Contracts/RunContracts.cs), not
 * guessed. `items` reuses `RunListItem` from `screens/results/results.ts`
 * (the same `RunResponse` shape `GET /runs`/`GET /runs/{id}` already type) —
 * no separate view model needed since this endpoint returns identical rows.
 */
import { apiGet } from "../lib/api";
import type { RunListItem } from "../screens/results/results";

export interface RunHistoryQuery {
	/** Comma-separated allow-list of `runs.state` values. Omit for every state. */
	state?: string;
	/** Comma-separated allow-list of `runs.run_type` values. Omit for every type. */
	runType?: string;
	/** Inclusive lower bound on `created_at` (ISO-8601). Omit for no lower bound. */
	since?: string;
	/** Inclusive upper bound on `created_at` (ISO-8601). Omit for no upper bound. */
	until?: string;
	/** Opaque cursor from a previous page's `next_cursor`. Omit for the first page. */
	cursor?: string;
	/** Page size; server applies its own default/max (50/200) when omitted. */
	limit?: number;
}

export interface RunHistoryPage {
	items: RunListItem[];
	/** Opaque; pass verbatim as the next call's `cursor`. Null at the end of matching history. */
	nextCursor: string | null;
}

interface RunHistoryListResponseWire {
	items: RunListItem[];
	next_cursor: string | null;
}

function buildQueryString(query: RunHistoryQuery): string {
	const params = new URLSearchParams();
	if (query.state) {
		params.set("state", query.state);
	}
	if (query.runType) {
		params.set("run_type", query.runType);
	}
	if (query.since) {
		params.set("since", query.since);
	}
	if (query.until) {
		params.set("until", query.until);
	}
	if (query.cursor) {
		params.set("cursor", query.cursor);
	}
	if (query.limit !== undefined) {
		params.set("limit", String(query.limit));
	}
	const qs = params.toString();
	return qs ? `?${qs}` : "";
}

/**
 * Reads one page of run history. Viewer+ (ADR-0019 decision 6: observing
 * operational history is not a domain action). Throws `ApiError` with
 * `status === 400`/`code === "validation_error"` for an unrecognized
 * `state`/`run_type` value, an unparseable `since`/`until`, or a garbage
 * `cursor` — never a 500 on client-abusable input, matching the backend
 * contract `jobEventHistory.ts` already documents for its sibling endpoint.
 */
export async function fetchRunHistory(query: RunHistoryQuery = {}): Promise<RunHistoryPage> {
	const wire = await apiGet<RunHistoryListResponseWire>(`/runs/history${buildQueryString(query)}`);
	return { items: wire.items, nextCursor: wire.next_cursor };
}
