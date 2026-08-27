/**
 * Loads the run-level component-results rollup (issue #745 remainder) for
 * one run id. Kept as its own hook (mirroring `useRunDetail`'s "one hook per
 * independently-failing sub-fetch" precedent) so a rollup failure never
 * blanks the rest of the Results detail pane — `PurgeRunPanel`'s tombstone
 * state and the existing per-target artifacts table are unaffected by this
 * endpoint erroring.
 *
 * Three distinct states the panel must render honestly (design brief:
 * "a coverage omission is never silently dropped or presented as
 * successful coverage"):
 *   - `loading`: request in flight.
 *   - `unavailable`: the request itself failed (network/404/500) — NOT the
 *     same as "no results yet", which is a normal 200 with `by_status: []`.
 *   - `rollup` with `by_status: []`: the run legitimately has no component
 *     results yet (still running, or a pre-#745 run) — an empty-state
 *     message, never an error banner.
 */
import { useEffect, useState } from "react";
import { fetchComponentResultsSummary, type ComponentResultRollup } from "./component-results";

export interface UseComponentResultsResult {
	rollup: ComponentResultRollup | null;
	loading: boolean;
	unavailable: boolean;
}

export function useComponentResults(runId: string | null): UseComponentResultsResult {
	const [rollup, setRollup] = useState<ComponentResultRollup | null>(null);
	const [loading, setLoading] = useState(false);
	const [unavailable, setUnavailable] = useState(false);

	useEffect(() => {
		if (!runId) {
			setRollup(null);
			setUnavailable(false);
			return;
		}

		let cancelled = false;
		setLoading(true);
		setUnavailable(false);

		fetchComponentResultsSummary(runId)
			.then((result) => {
				if (!cancelled) {
					setRollup(result);
				}
			})
			.catch(() => {
				if (!cancelled) {
					setRollup(null);
					setUnavailable(true);
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
	}, [runId]);

	return { rollup, loading, unavailable };
}
