/**
 * Generic operational-history deletion (issue #592, epic #588's last child)
 * — `DELETE`/`GET /runs/{id}/history` (`results.ts`). Simpler than
 * `usePurgeRun.ts`: deletion completes synchronously in one database
 * transaction (no runner-executed artifact-deletion phase), so there is no
 * in-progress state to poll — just a single request that settles to
 * `"Completed"`/`"AlreadyDeleted"` or a request error (e.g. 409
 * `requires_domain_purge_first`).
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../../lib/api";
import { deleteRunHistory, fetchRunHistoryDeletionStatus, type RunHistoryDeletionStatus } from "./results";

export interface UseRunHistoryDeletionResult {
	/** `null` until deletion has been requested (or an existing one loaded) for the current run. */
	status: RunHistoryDeletionStatus | null;
	/** True while the request is in flight. */
	busy: boolean;
	/** Set on a request failure (e.g. 409 `requires_domain_purge_first`/`run_not_terminal`, network). */
	requestError: string | null;
	/** Machine-readable error code parsed from the response, if any (e.g. `"requires_domain_purge_first"`) — drives the honest refusal message routing compliance runs to the Results purge action. */
	requestErrorCode: string | null;
	/** Fires the deletion (or confirms an already-deleted run, per the backend's idempotent contract). No-op while already in flight for this run. */
	confirmDelete: () => Promise<void>;
	/** Loads any deletion already requested for this run (e.g. reselecting a run) without firing a new one. 404 (never requested) is treated as "not deleted", not an error. */
	loadExistingStatus: () => Promise<void>;
	reset: () => void;
}

export function useRunHistoryDeletion(runId: string | null): UseRunHistoryDeletionResult {
	const [status, setStatus] = useState<RunHistoryDeletionStatus | null>(null);
	const [busy, setBusy] = useState(false);
	const [requestError, setRequestError] = useState<string | null>(null);
	const [requestErrorCode, setRequestErrorCode] = useState<string | null>(null);
	// Synchronous duplicate-submission guard (usePurgeRun.ts precedent) — two
	// clicks in the same tick must not both fire DELETE /history.
	const inFlightRef = useRef(false);

	useEffect(() => {
		setStatus(null);
		setBusy(false);
		setRequestError(null);
		setRequestErrorCode(null);
		inFlightRef.current = false;
	}, [runId]);

	const confirmDelete = useCallback(async () => {
		if (!runId || inFlightRef.current) {
			return;
		}
		inFlightRef.current = true;
		setBusy(true);
		setRequestError(null);
		setRequestErrorCode(null);
		try {
			const result = await deleteRunHistory(runId);
			setStatus(result);
		} catch (err) {
			if (err instanceof ApiError) {
				setRequestError(err.message);
				setRequestErrorCode(err.code);
			} else {
				setRequestError("Could not delete this run's history.");
			}
		} finally {
			setBusy(false);
			inFlightRef.current = false;
		}
	}, [runId]);

	const loadExistingStatus = useCallback(async () => {
		if (!runId || inFlightRef.current) {
			return;
		}
		try {
			const existing = await fetchRunHistoryDeletionStatus(runId);
			setStatus(existing);
		} catch (err) {
			// 404 ("never requested") is the expected, non-error case — only
			// surface a real failure (network/5xx).
			if (err instanceof ApiError && err.status !== 404) {
				setRequestError(err.message);
			}
		}
	}, [runId]);

	const reset = useCallback(() => {
		setStatus(null);
		setBusy(false);
		setRequestError(null);
		setRequestErrorCode(null);
		inFlightRef.current = false;
	}, []);

	return { status, busy, requestError, requestErrorCode, confirmDelete, loadExistingStatus, reset };
}
