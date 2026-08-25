/**
 * Run list + search + selection state for `ResultsScreen` (issue #416
 * decomposition of the former monolithic component — no behavior change).
 * Fetches `GET /runs` once on mount, auto-selects the first row so the
 * detail pane isn't empty on load, and exposes a case-insensitive filter
 * over the run id and scope's site id (matching the prior inline behavior).
 *
 * List is filtered to compliance-owned run types (issue #591: "Compliance
 * Results lists only scan/remediation-owned results") — `run_type` is the
 * closed set `JobCapabilities.cs`/`jobs_job_type_check` enforce server-side;
 * `scan`/`remediate` are the only two this screen owns (see `COMPLIANCE_RUN_TYPES`).
 *
 * `?run=<id>` (issue #591): if present and it names a run in the (filtered)
 * list, it is auto-selected instead of the first row — the same read-only
 * "ride the existing path's query string" pattern the former
 * `screens/liverun/useRunIdFromQuery.ts` used, now needed here so a
 * type-specific Live Jobs detail renderer can link a scan/remediate job
 * straight to its run's Results row.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchRunList, scopeSiteId, type RunListItem } from "./results";

/** Compliance-owned `run_type` values (`JobCapabilities.Compliance`'s
 * scan/remediate; the rest of that set — discover/credential-test/
 * content-pull/content-import/purge — has no results/artifacts presentation
 * and is not a "result" in this screen's sense). */
export const COMPLIANCE_RUN_TYPES: ReadonlySet<string> = new Set(["scan", "remediate"]);

function readRunIdFromQuery(): string | undefined {
	return new URLSearchParams(window.location.search).get("run") ?? undefined;
}

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
				const items = result.items.filter((r) => COMPLIANCE_RUN_TYPES.has(r.run_type));
				setRuns(items);
				if (items.length > 0) {
					const deepLinkedId = readRunIdFromQuery();
					const deepLinked = deepLinkedId ? items.find((r) => r.id === deepLinkedId) : undefined;
					const initial = deepLinked ?? items[0];
					setSelectedRunId((current) => current ?? initial.id);
					setSelectedRow((current) => current ?? initial);
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
