/**
 * Run list + search + selection state for `ResultsScreen` (issue #416
 * decomposition of the former monolithic component — no behavior change).
 * Fetches `GET /runs` once on mount, auto-selects the first row so the
 * detail pane isn't empty on load, and exposes a case-insensitive filter
 * over the run id and scope's site id (matching the prior inline behavior).
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchRunList, scopeSiteId, type RunListItem } from "./results";

export interface UseRunListResult {
	runs: RunListItem[];
	runsLoading: boolean;
	runsError: string | null;
	search: string;
	setSearch: (value: string) => void;
	filteredRuns: RunListItem[];
	selectedRunId: string | null;
	selectedRow: RunListItem | null;
	handleSelectRun: (row: RunListItem) => void;
}

export function useRunList(): UseRunListResult {
	const [runs, setRuns] = useState<RunListItem[]>([]);
	const [runsLoading, setRunsLoading] = useState(true);
	const [runsError, setRunsError] = useState<string | null>(null);
	const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
	const [selectedRow, setSelectedRow] = useState<RunListItem | null>(null);
	const [search, setSearch] = useState("");

	useEffect(() => {
		let cancelled = false;
		fetchRunList(50, 0)
			.then((result) => {
				if (cancelled) {
					return;
				}
				setRuns(result.items);
				if (result.items.length > 0) {
					setSelectedRunId((current) => current ?? result.items[0].id);
					setSelectedRow((current) => current ?? result.items[0]);
				}
			})
			.catch((err) => {
				if (!cancelled) {
					setRunsError(err instanceof ApiError ? err.message : "Could not load run history.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setRunsLoading(false);
				}
			});
		return () => {
			cancelled = true;
		};
	}, []);

	const filteredRuns = useMemo(() => {
		const term = search.trim().toLowerCase();
		if (!term) {
			return runs;
		}
		return runs.filter((r) => r.id.toLowerCase().includes(term) || (scopeSiteId(r.scope) ?? "").toLowerCase().includes(term));
	}, [runs, search]);

	const handleSelectRun = useCallback((row: RunListItem) => {
		setSelectedRunId(row.id);
		setSelectedRow(row);
	}, []);

	return { runs, runsLoading, runsError, search, setSearch, filteredRuns, selectedRunId, selectedRow, handleSelectRun };
}
