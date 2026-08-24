/**
 * Purge action + progress polling for Compliance Results (issue #656,
 * completing #594's frontend half, epic #577). Wraps `POST`/`GET
 * /runs/{id}/purge` (`results.ts`) with the same async-job UI idiom this
 * codebase already uses three times — `SiteTargetsPanel` (#562),
 * `ComplianceContentTab`/`useManagedToolInstall` (#576/#571) — poll-to-terminal
 * with a synchronous ref duplicate-submission guard and poll-generation
 * cleanup on unmount/terminal, not just component state (two clicks landing
 * in the same event-loop tick both see the same stale state under React's
 * batching).
 *
 * Unlike `useManagedToolInstall`'s run-state poll, this polls the purge
 * status resource itself (`GET /runs/{id}/purge`), not `GET /runs/{id}` —
 * purge has its own outcome/phase state machine
 * (`pending|running|done|failed` artifacts phase, `InProgress|Failed|
 * Completed|AlreadyPurged` outcome) independent of the run's own terminal
 * run-state, which was already terminal before purge was ever requested.
 *
 * Retry (AC "retry affordance on partial failure"): the backend purge is
 * idempotent/resumable (RunPurgeStatus's doc comment) — retry is just
 * `purgeRun` called again, reusing the same `start` path as the initial
 * confirm.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchPurgeStatus, purgeRun, type RunPurgeStatus } from "./results";

const POLL_INTERVAL_MS = 3000;

/** Purge outcomes with no further transition — polling stops here. */
function isTerminalPurgeOutcome(outcome: string): boolean {
	return outcome === "Completed" || outcome === "AlreadyPurged" || outcome === "Failed";
}

export interface UsePurgeRunResult {
	/** `null` until a purge has been requested (or an existing one loaded) for the current run. */
	status: RunPurgeStatus | null;
	/** True while the initial `POST` or a poll tick is in flight. */
	busy: boolean;
	/** Set on a request failure (e.g. 409 `run_not_terminal`, network) — distinct from `status.last_error`, which is the backend's own partial-failure detail. */
	requestError: string | null;
	/** Fires the purge (or resumes/retries an in-flight or failed one). No-op while already in flight for this run. */
	confirmPurge: () => Promise<void>;
	/** Alias of `confirmPurge` for the retry affordance — same call, different UI entry point. */
	retryPurge: () => Promise<void>;
	/** Loads any purge already requested for this run (e.g. reselecting a run mid-purge) without firing a new one. 404 (never requested) is treated as "no purge", not an error. */
	loadExistingStatus: () => Promise<void>;
	reset: () => void;
}

export function usePurgeRun(runId: string | null): UsePurgeRunResult {
	const [status, setStatus] = useState<RunPurgeStatus | null>(null);
	const [busy, setBusy] = useState(false);
	const [requestError, setRequestError] = useState<string | null>(null);
	const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollGeneration = useRef(0);
	// Synchronous duplicate-submission guard (ComplianceContentTab/useManagedToolInstall precedent) --
	// two clicks in the same tick must not both fire POST /purge.
	const inFlightRef = useRef(false);

	const stopPolling = useCallback(() => {
		pollGeneration.current += 1;
		if (pollTimer.current) {
			clearTimeout(pollTimer.current);
			pollTimer.current = null;
		}
	}, []);

	// Reset all purge state whenever the selected run changes, and stop any
	// poll for the previous run — otherwise a stray tick for run A could paint
	// over run B's freshly-selected (non-purged) detail pane.
	useEffect(() => {
		stopPolling();
		setStatus(null);
		setBusy(false);
		setRequestError(null);
		inFlightRef.current = false;
	}, [runId, stopPolling]);

	useEffect(() => {
		return () => {
			stopPolling();
		};
	}, [stopPolling]);

	const poll = useCallback(
		(id: string, generation: number) => {
			const tick = async () => {
				if (pollGeneration.current !== generation) {
					return;
				}
				try {
					const next = await fetchPurgeStatus(id);
					if (pollGeneration.current !== generation) {
						return;
					}
					setStatus(next);
					if (isTerminalPurgeOutcome(next.outcome)) {
						setBusy(false);
						inFlightRef.current = false;
						return;
					}
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				} catch {
					// Transient poll failure must not abandon the in-flight affordance
					// (ComplianceContentTab/useManagedToolInstall precedent) — keep polling.
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				}
			};
			void tick();
		},
		[],
	);

	const start = useCallback(async () => {
		if (!runId || inFlightRef.current) {
			return;
		}
		inFlightRef.current = true;
		setBusy(true);
		setRequestError(null);
		const generation = pollGeneration.current + 1;
		pollGeneration.current = generation;
		try {
			const result = await purgeRun(runId);
			if (pollGeneration.current !== generation) {
				return;
			}
			setStatus(result);
			if (isTerminalPurgeOutcome(result.outcome)) {
				setBusy(false);
				inFlightRef.current = false;
				return;
			}
			poll(runId, generation);
		} catch (err) {
			if (pollGeneration.current === generation) {
				setRequestError(err instanceof ApiError ? err.message : "Could not start the purge.");
				setBusy(false);
			}
			inFlightRef.current = false;
		}
	}, [runId, poll]);

	const loadExistingStatus = useCallback(async () => {
		if (!runId || inFlightRef.current) {
			return;
		}
		try {
			const existing = await fetchPurgeStatus(runId);
			setStatus(existing);
			if (!isTerminalPurgeOutcome(existing.outcome)) {
				inFlightRef.current = true;
				setBusy(true);
				const generation = pollGeneration.current + 1;
				pollGeneration.current = generation;
				poll(runId, generation);
			}
		} catch (err) {
			// 404 ("no purge requested yet") is the expected, non-error case —
			// only surface a real failure (network/5xx).
			if (err instanceof ApiError && err.status !== 404) {
				setRequestError(err.message);
			}
		}
	}, [runId, poll]);

	const reset = useCallback(() => {
		stopPolling();
		setStatus(null);
		setBusy(false);
		setRequestError(null);
		inFlightRef.current = false;
	}, [stopPolling]);

	return { status, busy, requestError, confirmPurge: start, retryPurge: start, loadExistingStatus, reset };
}
