/**
 * Live Run job-control state (#285): Pause queue / Abort run (run-scoped)
 * and per-job Cancel. Every control's server-side effect is reflected back
 * through SSE (job.state / run.progress / queue.state), never a local
 * optimistic patch — this hook only tracks in-flight/error UI state for the
 * buttons themselves, never the board.
 *
 * Controls follow the README "Roles & Permissions" visible-but-disabled
 * convention: an insufficient role still sees the button, disabled, with a
 * `title` naming the required role (`roleGateProps`) — never silently
 * hidden. Confirmation before a destructive action (`window.confirm`, same
 * pattern as CredentialsTab/SiteTargetsPanel deletes) guards Abort and
 * per-job Cancel; Pause/Resume act immediately since they're reversible.
 */
import { useState } from "react";
import { ApiError } from "../../lib/api";
import { roleGateProps, type Role } from "../../lib/roles";
import { abortRun, cancelJob, pauseRun, resumeRun, type RunJob } from "./liverun";

export interface JobControlProps {
	cancelGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	onCancel: (job: RunJob) => void;
}

export interface UseRunControlsResult {
	runControlGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	jobControls: JobControlProps;
	pausing: boolean;
	aborting: boolean;
	actionError: string | null;
	setActionError: (message: string | null) => void;
	handlePauseToggle: (paused: boolean) => Promise<void>;
	handleAbort: () => Promise<void>;
}

export function useRunControls(runId: string | undefined, role: Role | undefined): UseRunControlsResult {
	const [actionError, setActionError] = useState<string | null>(null);
	const [pausing, setPausing] = useState(false);
	const [aborting, setAborting] = useState(false);
	const [cancellingJobId, setCancellingJobId] = useState<string | null>(null);

	// api-contract.md "Runs & jobs": pause/resume/abort are Cyber+ (own runs),
	// Admin any (issue #757's "Cyber controls owned live scans" owner decision
	// lowered this floor from Operator+ — PR #819's role-matrix reconciliation);
	// the run-ownership check itself is server-side (this is presentation only,
	// per roles.ts). Per-job cancel rides the same floor — it is a
	// finer-grained abort, not a separate capability.
	const runControlGate = role ? roleGateProps(role, "Cyber") : { disabled: true, style: { opacity: 0.42 } };

	async function handlePauseToggle(paused: boolean) {
		if (!runId) {
			return;
		}
		setActionError(null);
		setPausing(true);
		try {
			await (paused ? resumeRun(runId) : pauseRun(runId));
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : paused ? "Could not resume the run." : "Could not pause the run.");
		} finally {
			setPausing(false);
		}
	}

	async function handleAbort() {
		if (!runId) {
			return;
		}
		if (!window.confirm("Abort this run? In-flight targets stop cooperatively; queued targets are cancelled. This cannot be undone.")) {
			return;
		}
		setActionError(null);
		setAborting(true);
		try {
			await abortRun(runId);
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not abort the run.");
		} finally {
			setAborting(false);
		}
	}

	async function handleCancelJob(job: RunJob) {
		if (!window.confirm(`Cancel "${job.target}"? This cannot be undone.`)) {
			return;
		}
		setActionError(null);
		setCancellingJobId(job.job_id);
		try {
			await cancelJob(job.job_id);
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not cancel the job.");
		} finally {
			setCancellingJobId(null);
		}
	}

	const jobControls: JobControlProps = {
		cancelGate: runControlGate.disabled
			? runControlGate
			: cancellingJobId
				? { disabled: true, style: { opacity: 0.42 }, title: "Cancelling…" }
				: { disabled: false },
		onCancel: (job) => {
			void handleCancelJob(job);
		},
	};

	return {
		runControlGate,
		jobControls,
		pausing,
		aborting,
		actionError,
		setActionError,
		handlePauseToggle,
		handleAbort,
	};
}
