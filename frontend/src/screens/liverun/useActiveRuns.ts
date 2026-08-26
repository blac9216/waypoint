/**
 * Backs `LiveRunScreen`'s contextless default state (issue #711, a
 * regression against #714: making `/live-run` a permanent top-level nav
 * entry exposed the route's bare "No run selected." early return with no
 * context or action). Rather than a new endpoint, this reuses the same
 * `GET /runs` list `results/useRunList.ts` already fetches for the
 * Compliance Scan Results screen — filtered to compliance-owned run types
 * (`results.ts`'s `COMPLIANCE_RUN_TYPES`) and to non-terminal state
 * (`isTerminalRunState`), newest-first, so the picker only offers runs a
 * user would plausibly want to jump into live (a completed run belongs on
 * the Results screen, not this one).
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { COMPLIANCE_RUN_TYPES } from "../results/useRunList";
import { fetchRunList, isTerminalRunState, type RunListItem } from "../results/results";

export interface UseActiveRunsResult {
	runs: RunListItem[];
	loading: boolean;
	error: string | null;
}

export function useActiveRuns(): UseActiveRunsResult {
	const [runs, setRuns] = useState<RunListItem[]>([]);
	const [loading, setLoading] = useState(true);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		let cancelled = false;
		fetchRunList(50, 0)
			.then((result) => {
				if (cancelled) {
					return;
				}
				const active = result.items.filter((r) => COMPLIANCE_RUN_TYPES.has(r.run_type) && !isTerminalRunState(r.state));
				setRuns(active);
			})
			.catch((err) => {
				if (!cancelled) {
					setError(err instanceof ApiError ? err.message : "Could not load compliance runs.");
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
	}, []);

	return { runs, loading, error };
}
