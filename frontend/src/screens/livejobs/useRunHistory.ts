/**
 * Data layer for the Jobs workspace's History mode (issue #708/#689, epic
 * #706) — pages `GET /runs/history` (`api/runHistory.ts`) with server-side
 * filters (state/run_type/time) and a keyset cursor, independent of
 * `useLiveJobs.ts`'s active-work seed+SSE reducer: history is inherently a
 * request/response paging concern, not a live stream.
 *
 * Windowing (epic #706): the default filter set excludes `scan`/`remediate`
 * from the visible page unless the caller explicitly widens `runTypes` —
 * compliance runs are "windowed out of default views but never
 * auto-deleted" (epic #706's Design section). This hook does not itself
 * decide the default filter; `HistoryPanel.tsx` owns the filter UI and
 * passes whatever `RunHistoryFilters` the operator has selected, defaulting
 * to non-compliance types on first render (see that file for the exact
 * default and how "show compliance runs too" is exposed as an explicit
 * toggle, never silently on).
 */
import { useCallback, useEffect, useState } from "react";
import { fetchRunHistory } from "../../api/runHistory";
import type { RunListItem } from "../results/results";

export interface RunHistoryFilters {
	/** Comma-separated `runs.state` allow-list, or undefined for every state. */
	state?: string;
	/** Comma-separated `runs.run_type` allow-list, or undefined for every type. */
	runType?: string;
	since?: string;
	until?: string;
}

export interface UseRunHistoryResult {
	items: RunListItem[];
	loading: boolean;
	loadError: string | null;
	/** True when more matching rows exist past the currently loaded page. */
	hasMore: boolean;
	/** Fetches the next page (appends to `items`). No-op if already loading or `hasMore` is false. */
	loadMore: () => void;
}

const PAGE_SIZE = 50;

export function useRunHistory(filters: RunHistoryFilters): UseRunHistoryResult {
	const [items, setItems] = useState<RunListItem[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [cursor, setCursor] = useState<string | null>(null);
	const [hasMore, setHasMore] = useState(false);

	// Filter changes (including the compliance-visibility toggle) restart
	// paging from the first page -- a stale cursor from a different filter
	// set would silently skip or duplicate rows once the query changes.
	const filterKey = `${filters.state ?? ""}|${filters.runType ?? ""}|${filters.since ?? ""}|${filters.until ?? ""}`;

	useEffect(() => {
		let cancelled = false;
		setLoading(true);
		setLoadError(null);
		fetchRunHistory({ ...filters, limit: PAGE_SIZE })
			.then((page) => {
				if (cancelled) {
					return;
				}
				setItems(page.items);
				setCursor(page.nextCursor);
				setHasMore(page.nextCursor !== null);
			})
			.catch((err: unknown) => {
				if (!cancelled) {
					setLoadError(err instanceof Error ? err.message : "Could not load run history.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setLoading(false);
				}
			});
		return () => {
			cancelled = true;
		};
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [filterKey]);

	const loadMore = useCallback(() => {
		if (loading || !hasMore || !cursor) {
			return;
		}
		setLoading(true);
		setLoadError(null);
		fetchRunHistory({ ...filters, cursor, limit: PAGE_SIZE })
			.then((page) => {
				setItems((prev) => [...prev, ...page.items]);
				setCursor(page.nextCursor);
				setHasMore(page.nextCursor !== null);
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof Error ? err.message : "Could not load more run history.");
			})
			.finally(() => {
				setLoading(false);
			});
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [loading, hasMore, cursor, filterKey]);

	return { items, loading, loadError, hasMore, loadMore };
}
