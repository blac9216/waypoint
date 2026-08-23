/**
 * Managed download-tool install/upload tracking for the Config → Depot &
 * Tokens tab (issue #39/#571). Both `POST /downloads/tool/install`
 * (local-repository) and `POST /downloads/tool/upload` (manual upload) queue
 * one `tool-install` job and return the same `{run_id, job_id}` shape
 * (`ManagedToolInstallQueuedResponse`) — this hook polls the run to a
 * terminal state exactly like `ComplianceContentTab.tsx`'s `pollRun`/
 * `PullUiState` pattern (issue #40), including its duplicate-submission
 * guard: a synchronous ref, not just state, because two clicks landing in
 * the same event-loop tick both see the same stale `install` state under
 * React's batching.
 *
 * Once terminal, the history (`fetchManagedToolInstalls`) and readiness are
 * both refetched by the caller (see `DepotTokensTab.tsx`) — this hook only
 * owns the in-flight/last-outcome banner state, not the ledger table or the
 * readiness card.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchDiscoveryRun } from "./sites"; // GET /runs/{id} — same RunResponse subset every poller in this app reuses
import {
	installManagedToolFromLocalRepository,
	uploadManagedTool,
	type ManagedToolInstallQueuedResponse,
} from "./depot";

/** Same terminal-state contract as sites.ts/compliance-content.ts. */
function isTerminalRunState(state: string): boolean {
	return state === "completed" || state === "completed_with_failures" || state === "aborted";
}

export interface InstallUiState {
	runId: string;
	/** `null` while queued/running; set once the run reaches a terminal state. */
	outcome: "failed" | "succeeded" | null;
}

export interface UseManagedToolInstallResult {
	install: InstallUiState | null;
	installError: string | null;
	inFlight: boolean;
	installFromLocalRepository: (sourcePath: string, version?: string) => Promise<void>;
	uploadTool: (artifact: File, signature: File, version?: string) => Promise<void>;
}

export function useManagedToolInstall(onSettled: () => void): UseManagedToolInstallResult {
	const [install, setInstall] = useState<InstallUiState | null>(null);
	const [installError, setInstallError] = useState<string | null>(null);
	const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollGeneration = useRef(0);
	// Synchronous duplicate-submission guard (ComplianceContentTab.tsx precedent).
	const inFlightRef = useRef(false);

	useEffect(() => {
		return () => {
			if (pollTimer.current) {
				clearTimeout(pollTimer.current);
			}
			// Unmount supersedes any in-flight poll generation so a stray tick
			// that was already scheduled cannot call setState after unmount.
			pollGeneration.current += 1;
		};
	}, []);

	const pollRun = useCallback(
		(runId: string, generation: number) => {
			const POLL_INTERVAL_MS = 3000;
			const tick = async () => {
				if (pollGeneration.current !== generation) {
					return;
				}
				try {
					const run = await fetchDiscoveryRun(runId);
					if (pollGeneration.current !== generation) {
						return;
					}
					if (isTerminalRunState(run.state)) {
						const failed = run.state !== "completed" || run.job_count_failed > 0 || run.job_count_blocked > 0;
						setInstall({ runId, outcome: failed ? "failed" : "succeeded" });
						inFlightRef.current = false;
						onSettled();
						return;
					}
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				} catch {
					// Transient poll failure must not abandon the queued/running
					// affordance (ComplianceContentTab precedent) — keep polling.
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				}
			};
			void tick();
		},
		[onSettled],
	);

	const start = useCallback(
		async (queue: () => Promise<ManagedToolInstallQueuedResponse>) => {
			if ((install && install.outcome === null) || inFlightRef.current) {
				return;
			}
			inFlightRef.current = true;
			setInstallError(null);
			const generation = pollGeneration.current + 1;
			pollGeneration.current = generation;
			try {
				const queued = await queue();
				setInstall({ runId: queued.run_id, outcome: null });
				pollRun(queued.run_id, generation);
			} catch (err) {
				setInstallError(err instanceof ApiError ? err.message : "Could not start the tool install.");
				inFlightRef.current = false;
			}
		},
		[install, pollRun],
	);

	const installFromLocalRepository = useCallback(
		(sourcePath: string, version?: string) => start(() => installManagedToolFromLocalRepository(sourcePath, version)),
		[start],
	);

	const uploadTool = useCallback(
		(artifact: File, signature: File, version?: string) => start(() => uploadManagedTool(artifact, signature, version)),
		[start],
	);

	const inFlight = install !== null && install.outcome === null;

	return { install, installError, inFlight, installFromLocalRepository, uploadTool };
}
