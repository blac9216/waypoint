/**
 * Historical log view for a selected TERMINAL job (issue #590 AC3: "completed/
 * recent work reachable via the #581 history API"). Active jobs render from
 * the live snapshot (`useLiveJobs.ts`); once a job/run has reached a
 * terminal state, its detail switches to this hook, which pages the run's
 * persisted history in bounded batches via `api/jobEventHistory.ts` and
 * narrows to the selected job client-side (mirrors the backend's own
 * `job_id` filter, but fetched once per run rather than re-querying per job
 * row switch within the same run).
 *
 * Issue #721: `fetchAllJobEventHistory` used to silently stop at an implicit
 * 5,000-event bound and present the prefix as complete history. This hook
 * now surfaces `truncated`/`loadMore` (mirroring `useRunHistory.ts`'s
 * existing page+cursor idiom in this same screen) so the UI can show an
 * honest truncation indicator with an explicit continuation control instead
 * of eagerly collecting an unbounded event set into browser memory (epic
 * #726 §7's 10,000+-job scale contract).
 */
import { useCallback, useEffect, useState } from "react";
import { fetchAllJobEventHistory } from "../../api/jobEventHistory";
import type { WaypointEvent } from "../../lib/events";

export interface UseJobHistoryResult {
	events: WaypointEvent[];
	loading: boolean;
	error: string | null;
	/** True when more history exists past `events` (batch bound reached, not end of history). */
	truncated: boolean;
	/** Fetches the next batch and appends it. No-op while already loading or when not truncated. */
	loadMore: () => void;
}

export function useJobHistory(runId: string | undefined, jobId: string | undefined, enabled: boolean): UseJobHistoryResult {
	const [events, setEvents] = useState<WaypointEvent[]>([]);
	const [loading, setLoading] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const [cursor, setCursor] = useState<string | null>(null);
	const [truncated, setTruncated] = useState(false);

	useEffect(() => {
		if (!enabled || !runId) {
			setEvents([]);
			setError(null);
			setCursor(null);
			setTruncated(false);
			return;
		}
		let cancelled = false;
		setLoading(true);
		setError(null);
		fetchAllJobEventHistory(runId, jobId ? { jobId } : {})
			.then((result) => {
				if (!cancelled) {
					setEvents(result.events);
					setCursor(result.nextCursor);
					setTruncated(result.truncated);
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

	const loadMore = useCallback(() => {
		if (loading || !truncated || !runId || !cursor) {
			return;
		}
		setLoading(true);
		setError(null);
		fetchAllJobEventHistory(runId, jobId ? { jobId } : {}, undefined, cursor)
			.then((result) => {
				setEvents((prev) => [...prev, ...result.events]);
				setCursor(result.nextCursor);
				setTruncated(result.truncated);
			})
			.catch((err: unknown) => {
				setError(err instanceof Error ? err.message : "Could not load more history.");
			})
			.finally(() => {
				setLoading(false);
			});
	}, [loading, truncated, runId, jobId, cursor]);

	return { events, loading, error, truncated, loadMore };
}
