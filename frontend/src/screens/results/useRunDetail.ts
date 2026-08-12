/**
 * Loads `run`, `jobs`, `artifacts` (best-effort), and the expired-skips
 * substitute for one run id — extracted from `ResultsScreen.tsx` (issue #416
 * decomposition, no behavior change). Kept as one hook (rather than three)
 * because every consumer of this screen wants the same combined
 * loading/error state — there is no case where only jobs or only artifacts
 * is useful without the others. `initialRun` seeds the header instantly from
 * the row the sidebar click already has in hand, so selecting a run doesn't
 * blank the detail pane while `GET /runs/{id}` is in flight; the fetch below
 * still runs to pick up fields the list row doesn't carry.
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import {
	fetchAttestationResolution,
	fetchAttestationsApplied,
	fetchRun,
	fetchRunArtifacts,
	fetchRunJobs,
	type AppliedAttestation,
	type ConfigDocResolution,
	type RunArtifactRow,
	type RunJobItem,
	type RunListItem,
} from "./results";

export interface ExpiredAttestation {
	target: string;
	profile: string;
	resolution: ConfigDocResolution;
}

export interface UseRunDetailResult {
	run: RunListItem | null;
	setRun: (run: RunListItem | null) => void;
	jobs: RunJobItem[];
	artifacts: RunArtifactRow[] | null;
	artifactsUnavailable: boolean;
	expiredAttestations: ExpiredAttestation[];
	attestationsApplied: AppliedAttestation[] | null;
	loading: boolean;
	loadError: string | null;
}

export function useRunDetail(runId: string | null, initialRun: RunListItem | null): UseRunDetailResult {
	const [run, setRun] = useState<RunListItem | null>(initialRun);
	const [jobs, setJobs] = useState<RunJobItem[]>([]);
	const [artifacts, setArtifacts] = useState<RunArtifactRow[] | null>(null);
	const [artifactsUnavailable, setArtifactsUnavailable] = useState(false);
	const [expiredAttestations, setExpiredAttestations] = useState<ExpiredAttestation[]>([]);
	const [attestationsApplied, setAttestationsApplied] = useState<AppliedAttestation[] | null>(null);
	const [loading, setLoading] = useState(false);
	const [loadError, setLoadError] = useState<string | null>(null);

	useEffect(() => {
		setRun(initialRun);
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [runId]);

	useEffect(() => {
		if (!runId) {
			setJobs([]);
			setArtifacts(null);
			setArtifactsUnavailable(false);
			setExpiredAttestations([]);
			setAttestationsApplied(null);
			return;
		}

		let cancelled = false;
		setLoading(true);
		setLoadError(null);

		async function loadExpiredAttestations(rows: RunArtifactRow[]) {
			// One (profile, target) pair per distinct benchmark+target combination
			// in the artifacts rows — results.ts's fetchAttestationResolution takes
			// a target id, but RunArtifactRow only carries a display name (the
			// documented artifacts shape has no target id field), so this can only
			// resolve when the artifact row's target name is itself a valid target
			// id/lookup key. That mismatch is exactly the "current api-contract does
			// not fully connect these two surfaces yet" gap called out in the PR
			// body; guarded here rather than assumed away.
			const results: ExpiredAttestation[] = [];
			for (const row of rows) {
				// `benchmark` is nullable on the wire (RunArtifactResponse) — a row
				// with no benchmark has nothing to resolve an attestation against.
				if (!row.benchmark) {
					continue;
				}
				try {
					const resolutions = await fetchAttestationResolution(row.benchmark, row.target);
					for (const resolution of resolutions) {
						if (resolution.attestation_expired) {
							results.push({ target: row.target, profile: row.benchmark, resolution });
						}
					}
				} catch {
					// Best-effort per-row: one target's resolve failing (e.g. the name
					// isn't a real target id) must not blank the rest of the sidebar.
				}
			}
			if (!cancelled) {
				setExpiredAttestations(results);
			}
		}

		async function load() {
			try {
				const [runDetail, jobRows] = await Promise.all([fetchRun(runId!), fetchRunJobs(runId!)]);
				if (cancelled) {
					return;
				}
				setRun(runDetail);
				setJobs(jobRows);
			} catch (err) {
				if (!cancelled) {
					setLoadError(err instanceof ApiError ? err.message : "Could not load this run.");
				}
			} finally {
				if (!cancelled) {
					setLoading(false);
				}
			}

			try {
				const rows = await fetchRunArtifacts(runId!);
				if (!cancelled) {
					setArtifacts(rows);
					void loadExpiredAttestations(rows);
				}
			} catch {
				// A 404/network failure here must not blank the whole screen — the
				// table renders its own "not available yet" state so one flaky
				// sub-fetch doesn't take out KPIs/jobs/sidebar with it.
				if (!cancelled) {
					setArtifacts(null);
					setArtifactsUnavailable(true);
				}
			}

			try {
				const applied = await fetchAttestationsApplied(runId!);
				if (!cancelled) {
					setAttestationsApplied(applied);
				}
			} catch {
				// Same best-effort treatment as artifacts above — the sidebar
				// panel falls back to its existing expired-only view.
				if (!cancelled) {
					setAttestationsApplied(null);
				}
			}
		}

		void load();
		return () => {
			cancelled = true;
		};
	}, [runId]);

	return { run, setRun, jobs, artifacts, artifactsUnavailable, expiredAttestations, attestationsApplied, loading, loadError };
}
