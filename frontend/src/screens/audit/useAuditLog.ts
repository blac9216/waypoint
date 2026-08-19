/**
 * Filter + paging state for `AuditScreen` (issue #531), mirroring
 * `results/useRunList.ts`'s "screen owns a hook, hook owns the fetch" split.
 * Unlike `useRunList` (single best-effort page, no pager UI), this fetch
 * re-runs whenever the filter or page changes — the filter narrows what
 * `X-Total-Count` reports, so paging state resets to page 0 on every filter
 * change rather than leaving a stale offset pointed past the new, smaller
 * result set.
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchAuditLog, type AuditEntry, type AuditFilter } from "./audit";

export const PAGE_SIZE = 50;

export interface UseAuditLogResult {
	entries: AuditEntry[];
	totalCount: number;
	loading: boolean;
	loadError: string | null;
	filter: AuditFilter;
	setFilter: (next: AuditFilter) => void;
	offset: number;
	setOffset: (next: number) => void;
	reload: () => void;
}

export function useAuditLog(): UseAuditLogResult {
	const [filter, setFilterState] = useState<AuditFilter>({});
	const [offset, setOffset] = useState(0);
	const [entries, setEntries] = useState<AuditEntry[]>([]);
	const [totalCount, setTotalCount] = useState(0);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [reloadToken, setReloadToken] = useState(0);

	useEffect(() => {
		let cancelled = false;
		setLoading(true);
		setLoadError(null);
		fetchAuditLog(filter, { limit: PAGE_SIZE, offset })
			.then((result) => {
				if (cancelled) {
					return;
				}
				setEntries(result.items);
				setTotalCount(result.totalCount);
			})
			.catch((err: unknown) => {
				if (!cancelled) {
					setLoadError(err instanceof ApiError ? err.message : "Could not load the audit log.");
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
	}, [filter, offset, reloadToken]);

	const setFilter = useCallback((next: AuditFilter) => {
		setFilterState(next);
		setOffset(0);
	}, []);

	const reload = useCallback(() => setReloadToken((t) => t + 1), []);

	return { entries, totalCount, loading, loadError, filter, setFilter, offset, setOffset, reload };
}
