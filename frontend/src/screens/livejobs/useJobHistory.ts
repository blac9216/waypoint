/**
 * Historical log view for a selected TERMINAL job (issue #590 AC3: "completed/
 * recent work reachable via the #581 history API"). Active jobs render from
 * the live snapshot (`useLiveJobs.ts`); once a job/run has reached a
 * terminal state, its detail switches to this hook, which pages the whole
 * run's persisted history via `api/jobEventHistory.ts` and narrows to the
 * selected job client-side (mirrors the backend's own `job_id` filter, but
 * fetched once per run rather than re-querying per job row switch within
 * the same run).
 */
import { useEffect, useState } from "react";
import { fetchAllJobEventHistory } from "../../api/jobEventHistory";
import type { WaypointEvent } from "../../lib/events";

export interface UseJobHistoryResult {
	events: WaypointEvent[];
	loading: boolean;
	error: string | null;
}

export function useJobHistory(runId: string | undefined, jobId: string | undefined, enabled: boolean): UseJobHistoryResult {
	const [events, setEvents] = useState<WaypointEvent[]>([]);
	const [loading, setLoading] = useState(false);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		if (!enabled || !runId) {
			setEvents([]);
			setError(null);
			return;
		}
		let cancelled = false;
		setLoading(true);
		setError(null);
		fetchAllJobEventHistory(runId, jobId ? { jobId } : {})
			.then((items) => {
				if (!cancelled) {
					setEvents(items);
				}
			})
			.catch((err: unknown) => {
				if (!cancelled) {
					setError(err instanceof Error ? err.message : "Could not load history.");
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
	}, [runId, jobId, enabled]);

	return { events, loading, error };
}
