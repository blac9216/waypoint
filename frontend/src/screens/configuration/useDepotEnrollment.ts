/**
 * Assisted VCF 9.1 Software Depot enrollment (issue #691): drives
 * `GET/POST /downloads/enrollment/*` for the Depot & Tokens tab's two entry
 * states -- an operator generating a fresh Software Depot ID ("I need a
 * code") or pasting one they already hold ("use existing code"). Both paths
 * converge on the same `acceptActivationCode` call server-side. Identity
 * follows the code (owner decision 2026-08-25): any structurally valid code is
 * stored as-is -- no match against the disposable Depot ID is required -- and
 * validation seeds the tool's identity from that code before asking it.
 *
 * `generateDepotId`/`validateActivationCode` are queued jobs (202 +
 * run_id/job_id) -- this hook polls the run to a terminal state via the same
 * `fetchDiscoveryRun`/`isTerminalRunState` contract `useManagedToolInstall.ts`
 * already uses for the sibling tool-install jobs in this same tab, then
 * reloads the authoritative enrollment state once terminal.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchDiscoveryRun } from "./sites";
import {
	acceptActivationCode,
	fetchDepotEnrollment,
	generateDepotId,
	resetDepotEnrollment,
	validateActivationCode,
	type DepotEnrollment,
} from "./depot";

function isTerminalRunState(state: string): boolean {
	return state === "completed" || state === "completed_with_failures" || state === "aborted";
}

export interface UseDepotEnrollmentResult {
	enrollment: DepotEnrollment | null;
	loading: boolean;
	loadError: string | null;
	reload: () => void;

	busy: boolean;
	actionError: string | null;

	doGenerateDepotId: () => Promise<void>;
	doAcceptActivationCode: (code: string) => Promise<boolean>;
	doValidate: () => Promise<void>;
	doReset: () => Promise<void>;
}

export function useDepotEnrollment(): UseDepotEnrollmentResult {
	const [enrollment, setEnrollment] = useState<DepotEnrollment | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [busy, setBusy] = useState(false);
	const [actionError, setActionError] = useState<string | null>(null);
	const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollGeneration = useRef(0);

	useEffect(() => {
		return () => {
			if (pollTimer.current) {
				clearTimeout(pollTimer.current);
			}
			pollGeneration.current += 1;
		};
	}, []);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchDepotEnrollment()
			.then(setEnrollment)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load depot enrollment status."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const pollRunToTerminal = useCallback((runId: string, generation: number): Promise<void> => {
		const POLL_INTERVAL_MS = 3000;
		return new Promise((resolve) => {
			const tick = async () => {
				if (pollGeneration.current !== generation) {
					resolve();
					return;
				}
				try {
					const run = await fetchDiscoveryRun(runId);
					if (pollGeneration.current !== generation) {
						resolve();
						return;
					}
					if (isTerminalRunState(run.state)) {
						resolve();
						return;
					}
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				} catch {
					// Transient poll failure must not abandon the in-flight action --
					// keep polling (useManagedToolInstall.ts precedent).
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				}
			};
			void tick();
		});
	}, []);

	const doGenerateDepotId = useCallback(async () => {
		if (busy) {
			return;
		}
		setBusy(true);
		setActionError(null);
		const generation = pollGeneration.current + 1;
		pollGeneration.current = generation;
		try {
			const queued = await generateDepotId();
			await pollRunToTerminal(queued.run_id, generation);
			load();
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not generate the Software Depot ID.");
		} finally {
			setBusy(false);
		}
	}, [busy, load, pollRunToTerminal]);

	const doAcceptActivationCode = useCallback(async (code: string) => {
		setBusy(true);
		setActionError(null);
		try {
			const updated = await acceptActivationCode(code);
			setEnrollment(updated);
			return true;
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not store the Activation Code.");
			return false;
		} finally {
			setBusy(false);
		}
	}, []);

	const doValidate = useCallback(async () => {
		if (busy) {
			return;
		}
		setBusy(true);
		setActionError(null);
		const generation = pollGeneration.current + 1;
		pollGeneration.current = generation;
		try {
			const queued = await validateActivationCode();
			await pollRunToTerminal(queued.run_id, generation);
			load();
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not validate the Activation Code.");
		} finally {
			setBusy(false);
		}
	}, [busy, load, pollRunToTerminal]);

	const doReset = useCallback(async () => {
		setBusy(true);
		setActionError(null);
		try {
			const updated = await resetDepotEnrollment();
			setEnrollment(updated);
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not reset depot enrollment.");
		} finally {
			setBusy(false);
		}
	}, []);

	return { enrollment, loading, loadError, reload: load, busy, actionError, doGenerateDepotId, doAcceptActivationCode, doValidate, doReset };
}
