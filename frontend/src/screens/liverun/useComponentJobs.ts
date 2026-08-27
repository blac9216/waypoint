/**
 * Component-job board state (issue #757): grouped counts + incrementally
 * loaded, cursor-paged rows for the virtualized list. Bounded by design —
 * the initial load is one counts call (vocabulary-sized) plus one page of
 * rows (server-bounded); further pages load only on explicit `loadMore()`
 * (fired by the list when the scroll window nears the loaded end), and a
 * filter/search change resets the row set and cursor rather than appending
 * across incompatible filter states.
 *
 * No SSE folding in this slice: the board offers an explicit Refresh, and
 * live per-item updates ride the existing board layouts. Bounded SSE
 * live-update scaling is issue #757's stated remainder.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import {
	fetchComponentJobCounts,
	fetchComponentJobsPage,
	type ComponentJobCount,
	type ComponentJobFilters,
	type ComponentJobItem,
} from "./componentJobs";

export interface UseComponentJobsResult {
	counts: ComponentJobCount[];
	items: ComponentJobItem[];
	/** True when more filtered rows exist past the loaded set. */
	hasMore: boolean;
	loading: boolean;
	loadingMore: boolean;
	error: string | null;
	loadMore: () => void;
	refresh: () => void;
}

const PAGE_SIZE = 200;

export function useComponentJobs(runId: string | undefined, filters: ComponentJobFilters): UseComponentJobsResult {
	const { status } = useAuth();
	const [counts, setCounts] = useState<ComponentJobCount[]>([]);
	const [items, setItems] = useState<ComponentJobItem[]>([]);
	const [nextCursor, setNextCursor] = useState<string | null>(null);
	const [loading, setLoading] = useState(false);
	const [loadingMore, setLoadingMore] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [refreshNonce, setRefreshNonce] = useState(0);

	// Guards a stale page-append after filters changed mid-flight.
	const generation = useRef(0);

	const filterKey = `${filters.state ?? ""}|${filters.priority ?? ""}|${filters.componentKind ?? ""}|${filters.search ?? ""}`;

	useEffect(() => {
		if (!runId || status !== "signed-in") {
			setCounts([]);
			setItems([]);
			setNextCursor(null);
			return;
		}
		const gen = ++generation.current;
		setLoading(true);
		setError(null);
		Promise.all([fetchComponentJobCounts(runId, filters), fetchComponentJobsPage(runId, filters, undefined, PAGE_SIZE)])
			.then(([countRows, page]) => {
				if (generation.current !== gen) {
					return;
				}
				setCounts(countRows);
				setItems(page.items);
				setNextCursor(page.nextCursor);
			})
			.catch((err: unknown) => {
				if (generation.current === gen) {
					setError(err instanceof Error ? err.message : "Could not load component jobs.");
				}
			})
			.finally(() => {
				if (generation.current === gen) {
					setLoading(false);
				}
			});
		// filterKey is the stable serialization of `filters`; the object identity
		// changes every render, so depending on it directly would refetch forever.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [runId, status, filterKey, refreshNonce]);

	const loadMore = useCallback(() => {
		if (!runId || !nextCursor || loadingMore) {
			return;
		}
		const gen = generation.current;
		setLoadingMore(true);
		fetchComponentJobsPage(runId, filters, nextCursor, PAGE_SIZE)
			.then((page) => {
				if (generation.current !== gen) {
					return;
				}
				setItems((prev) => [...prev, ...page.items]);
				setNextCursor(page.nextCursor);
			})
			.catch((err: unknown) => {
				if (generation.current === gen) {
					setError(err instanceof Error ? err.message : "Could not load more component jobs.");
				}
			})
			.finally(() => {
				if (generation.current === gen) {
					setLoadingMore(false);
				}
			});
		// Same filterKey rationale as above.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [runId, nextCursor, loadingMore, filterKey]);

	const refresh = useCallback(() => setRefreshNonce((n) => n + 1), []);

	return { counts, items, hasMore: nextCursor !== null, loading, loadingMore, error, loadMore, refresh };
}
