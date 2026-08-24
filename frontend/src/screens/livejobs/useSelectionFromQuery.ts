/**
 * Deep-link selection state for the Live Jobs workspace (issue #590 AC4):
 * `?run=<id>&job=<id>` on `/live-jobs`, mirroring `../liverun/useRunIdFromQuery.ts`'s
 * "ride the existing path's query string" approach rather than graduating
 * the hand-rolled router (`lib/router.tsx`) to param routes for one screen.
 * `job` is optional — selecting a run alone (no job) is a valid deep link
 * that means "show this run's group, no job highlighted yet".
 *
 * Distinct from the scan screen's hook because this one also WRITES the
 * query string (`select`) so keyboard/mouse selection inside the workspace
 * updates the address bar — `useRunIdFromQuery` was read-only because
 * nothing on that screen navigates within itself.
 */
import { useCallback, useEffect, useState } from "react";

export interface LiveJobsSelection {
	runId: string | undefined;
	jobId: string | undefined;
}

function readSelection(): LiveJobsSelection {
	const params = new URLSearchParams(window.location.search);
	return {
		runId: params.get("run") ?? undefined,
		jobId: params.get("job") ?? undefined,
	};
}

export interface UseSelectionFromQueryResult extends LiveJobsSelection {
	/** Updates the URL (pushState, no reload) and local state together.
	 * Passing `jobId: undefined` clears just the job param, keeping `run`. */
	select: (next: LiveJobsSelection) => void;
}

export function useSelectionFromQuery(): UseSelectionFromQueryResult {
	const [selection, setSelection] = useState<LiveJobsSelection>(readSelection);

	useEffect(() => {
		const sync = () => setSelection(readSelection());
		window.addEventListener("popstate", sync);
		return () => window.removeEventListener("popstate", sync);
	}, []);

	const select = useCallback((next: LiveJobsSelection) => {
		const params = new URLSearchParams(window.location.search);
		if (next.runId) {
			params.set("run", next.runId);
		} else {
			params.delete("run");
		}
		if (next.jobId) {
			params.set("job", next.jobId);
		} else {
			params.delete("job");
		}
		const qs = params.toString();
		const url = `${window.location.pathname}${qs ? `?${qs}` : ""}`;
		window.history.pushState(null, "", url);
		setSelection(next);
	}, []);

	return { ...selection, select };
}
