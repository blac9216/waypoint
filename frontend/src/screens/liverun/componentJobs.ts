/**
 * Component-job scale data layer (issue #757, epic #726 §7, ADR-0024's
 * 10,000+-job contract): typed clients for the two new run-scoped read
 * endpoints —
 *   - `GET /runs/{id}/component-jobs/counts`: server-side GROUP BY counts by
 *     (priority, component_kind, state). The state board renders every
 *     counter from this bounded result (vocabulary-sized, never job-count-
 *     sized) and NEVER derives a count by fetching job rows.
 *   - `GET /runs/{id}/component-jobs`: cursor-paged, filtered, searchable
 *     rows for the virtualized component list. `next_cursor` is opaque and
 *     null exactly at the end of the filtered set — never a silent
 *     truncation (same contract as api/jobEventHistory.ts).
 *
 * Plus `computeWindow`, the pure windowing function the virtualized list
 * renders through: given scroll position and row geometry it returns the
 * visible index range and spacer heights, so the DOM only ever contains the
 * on-screen slice (+overscan) no matter how many rows are loaded. Pure and
 * DOM-free by design — the unit tests assert windowing arithmetic directly
 * instead of mounting 10,000 nodes.
 */
import { apiGet } from "../../lib/api";

export interface ComponentJobCount {
	priority: number;
	component_kind: string;
	state: string;
	count: number;
}

export interface ComponentJobItem {
	id: string;
	job_type: string;
	target_id: string | null;
	target_name: string | null;
	state: string;
	stage: string | null;
	priority: number;
	component_kind: string;
	attempt_count: number;
	created_at: string | null;
	started_at: string | null;
	finished_at: string | null;
}

export interface ComponentJobPage {
	items: ComponentJobItem[];
	/** Opaque; pass verbatim as the next call's `cursor`. Null at the end. */
	nextCursor: string | null;
}

export interface ComponentJobFilters {
	/** Comma-separated `jobs.state` allow-list, e.g. `"queued,running"`. */
	state?: string;
	/** Comma-separated priority allow-list (1-6), e.g. `"1,2"`. */
	priority?: string;
	/** Comma-separated component-kind allow-list (vcenter/esxi/vm/service/target/unknown). */
	componentKind?: string;
	/** Case-insensitive substring match on target_name. */
	search?: string;
}

function buildQueryString(filters: ComponentJobFilters, cursor?: string, limit?: number): string {
	const params = new URLSearchParams();
	if (filters.state) {
		params.set("state", filters.state);
	}
	if (filters.priority) {
		params.set("priority", filters.priority);
	}
	if (filters.componentKind) {
		params.set("component_kind", filters.componentKind);
	}
	if (filters.search) {
		params.set("search", filters.search);
	}
	if (cursor) {
		params.set("cursor", cursor);
	}
	if (limit !== undefined) {
		params.set("limit", String(limit));
	}
	const qs = params.toString();
	return qs ? `?${qs}` : "";
}

interface ComponentJobListWire {
	items: ComponentJobItem[];
	next_cursor: string | null;
}

/** Grouped counts — bounded by vocabulary size, never by job count. */
export function fetchComponentJobCounts(runId: string, filters: ComponentJobFilters = {}): Promise<ComponentJobCount[]> {
	return apiGet<ComponentJobCount[]>(`/runs/${runId}/component-jobs/counts${buildQueryString(filters)}`);
}

/** One bounded page of component-job rows (server default 100, max 500). */
export async function fetchComponentJobsPage(
	runId: string,
	filters: ComponentJobFilters = {},
	cursor?: string,
	limit?: number,
): Promise<ComponentJobPage> {
	const wire = await apiGet<ComponentJobListWire>(`/runs/${runId}/component-jobs${buildQueryString(filters, cursor, limit)}`);
	return { items: wire.items, nextCursor: wire.next_cursor };
}

// -- virtualization windowing (pure) -----------------------------------------

export interface ListWindow {
	/** First row index to render (inclusive). */
	start: number;
	/** One past the last row index to render (exclusive). */
	end: number;
	/** Height of the spacer above the rendered slice, px. */
	topPad: number;
	/** Height of the spacer below the rendered slice, px. */
	bottomPad: number;
	/** Total scrollable height, px — rowHeight x totalCount. */
	totalHeight: number;
}

/**
 * Computes the render window for a fixed-row-height virtualized list. Only
 * `end - start` rows are ever mounted; the two pads keep the scrollbar
 * honest. All inputs are clamped so garbage scroll positions (elastic
 * overscroll, mid-refetch shrink) can never produce a negative or
 * out-of-range window.
 */
export function computeWindow(
	scrollTop: number,
	viewportHeight: number,
	rowHeight: number,
	totalCount: number,
	overscan = 5,
): ListWindow {
	if (rowHeight <= 0 || totalCount <= 0 || viewportHeight <= 0) {
		return { start: 0, end: 0, topPad: 0, bottomPad: 0, totalHeight: Math.max(0, totalCount) * Math.max(0, rowHeight) };
	}
	const clampedScrollTop = Math.max(0, scrollTop);
	const first = Math.floor(clampedScrollTop / rowHeight);
	const visibleCount = Math.ceil(viewportHeight / rowHeight) + 1;
	const start = Math.max(0, first - overscan);
	const end = Math.min(totalCount, first + visibleCount + overscan);
	return {
		start,
		end,
		topPad: start * rowHeight,
		bottomPad: (totalCount - end) * rowHeight,
		totalHeight: totalCount * rowHeight,
	};
}
