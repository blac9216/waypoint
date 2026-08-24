/**
 * Looks up whether a selected History-mode run's operational history has
 * already been deleted (issue #592's `GET /runs/{id}/history` tombstone
 * read) so the Jobs workspace can render an honest "history deleted" state
 * instead of pretending a tombstoned run still has detail/logs to show
 * (issue #708 AC "history-deleted runs show their honest tombstone state").
 * Deliberately per-selected-run (not bulk-fetched for the whole page) --
 * the same lazy-lookup posture `RunHistoryDeletionPanel.tsx`'s
 * `loadExistingStatus` already uses on Compliance Results, since most rows
 * in a page are never selected.
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchRunHistoryDeletionStatus, type RunHistoryDeletionStatus } from "../results/results";

export interface UseHistoryTombstoneResult {
	/** Null while loading, while the run has never had its history deleted, or with no selection. */
	tombstone: RunHistoryDeletionStatus | null;
	loading: boolean;
}

export function useHistoryTombstone(runId: string | undefined): UseHistoryTombstoneResult {
	const [tombstone, setTombstone] = useState<RunHistoryDeletionStatus | null>(null);
	const [loading, setLoading] = useState(false);

	useEffect(() => {
		if (!runId) {
			setTombstone(null);
			setLoading(false);
			return;
		}
		let cancelled = false;
		setLoading(true);
		setTombstone(null);
		fetchRunHistoryDeletionStatus(runId)
			.then((status) => {
				if (!cancelled) {
					setTombstone(status);
				}
			})
			.catch((err: unknown) => {
				// 404 ("never deleted") is the expected, non-error case for the vast
				// majority of history rows -- only a genuine failure should ever
				// surface, and even then this hook has nowhere to show it except
				// silently leaving the row as "not tombstoned" (the caller's normal
				// detail renderer still works from the run summary it already has).
				if (!cancelled && !(err instanceof ApiError && err.status === 404)) {
					setTombstone(null);
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

	return { tombstone, loading };
}
