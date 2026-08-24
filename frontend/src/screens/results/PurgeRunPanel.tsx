/**
 * Destructive purge action for Compliance Results (issue #656, completing
 * #594's frontend half, epic #577) — against `POST`/`GET /runs/{id}/purge`
 * (`RunsController`, `RunContracts.cs`). Admin-only, terminal-runs-only,
 * requires the operator to type the literal `PURGE` confirmation, mirroring
 * the backend's own `RunPurgeRequest.Confirmation` step-up requirement
 * (`RunsController.PurgeConfirmation`) rather than a softer `window.confirm`
 * — purge is irreversible (removes attestation snapshots, scan artifact
 * files, and nulls schedule `last_run_id` references) so this screen never
 * makes it easier to trigger than the API demands.
 *
 * States rendered here:
 *   - Not purged, terminal run, Admin: "Purge run…" button opens the typed
 *     confirmation. Non-Admin/non-terminal: `roleGateProps`-style
 *     visible-but-disabled treatment with a reason (never hidden).
 *   - In flight (`outcome: "InProgress"`): progress text
 *     (`artifacts_deleted`/`artifacts_total`), `aria-live="polite"` — not
 *     color-only.
 *   - Failed (`outcome: "Failed"`): the backend's own `last_error`, plus a
 *     retry button that re-`POST`s (the purge is idempotent/resumable).
 *   - Completed/AlreadyPurged: a tombstone summary (`requested_by`,
 *     `requested_at`, `prior_state`, `artifacts_deleted`) replaces the
 *     results panes — a purged run's artifacts/attestations are genuinely
 *     gone server-side, so this screen must not keep rendering stale
 *     artifact rows next to it (the honest-state AC).
 */
import { useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { roleGateProps } from "../../lib/roles";
import { formatTimestamp, isTerminalRunState, type RunListItem } from "./results";
import { usePurgeRun } from "./usePurgeRun";
import "./PurgeRunPanel.css";

const CONFIRMATION_WORD = "PURGE";

export interface PurgeRunPanelProps {
	run: RunListItem;
	/** Called once the purge reaches a terminal success outcome so the parent can refresh the run list/detail. */
	onPurged: () => void;
}

export function PurgeRunPanel({ run, onPurged }: PurgeRunPanelProps) {
	const { user } = useAuth();
	const { status, busy, requestError, confirmPurge, retryPurge, loadExistingStatus, reset } = usePurgeRun(run.id);
	const [confirming, setConfirming] = useState(false);
	const [typed, setTyped] = useState("");

	// Pick up an already-in-flight or already-completed purge if this run is
	// reselected mid-purge (or was purged in a previous session) — never
	// re-fires a purge, just loads and (if still running) resumes polling.
	useEffect(() => {
		void loadExistingStatus();
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [run.id]);

	const terminal = isTerminalRunState(run.state);
	const gate = user
		? roleGateProps(user.role, "Admin", `Requires Admin — purging results is not available to ${user.role}`)
		: { disabled: true, style: { opacity: 0.42 } };
	const purgeDisabled = gate.disabled || !terminal || busy;
	const purgeTitle = gate.disabled ? gate.title : !terminal ? "Run must reach a terminal state (completed, completed with failures, or aborted) before it can be purged" : undefined;

	const outcome = status?.outcome;
	const purged = outcome === "Completed" || outcome === "AlreadyPurged";
	const inProgress = outcome === "InProgress";
	const failed = outcome === "Failed";

	useEffect(() => {
		if (purged) {
			onPurged();
		}
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [purged]);

	if (purged && status) {
		return (
			<div className="purge-panel purge-panel--tombstone" aria-live="polite">
				<div className="purge-panel__title">RUN PURGED</div>
				<div className="purge-panel__tombstone-body">
					This run's results and artifacts have been removed. Requested by {status.requested_by || "—"} at{" "}
					{formatTimestamp(status.requested_at)} (prior state: {status.prior_state || "—"}). {status.artifacts_deleted} artifact
					{status.artifacts_deleted === 1 ? "" : "s"} deleted.
				</div>
			</div>
		);
	}

	return (
		<div className="purge-panel">
			{!confirming && (
				<button type="button" className="purge-panel__trigger" disabled={purgeDisabled} title={purgeTitle} onClick={() => setConfirming(true)}>
					Purge run…
				</button>
			)}

			{confirming && !inProgress && (
				<div className="purge-panel__confirm" role="group" aria-label="Confirm purge">
					<div className="purge-panel__confirm-warning">
						This permanently deletes this run's scan artifacts, attestation snapshots, and unlinks it from any schedule.
						This cannot be undone.
					</div>
					<label className="purge-panel__confirm-label">
						Type <span className="mono purge-panel__confirm-word">{CONFIRMATION_WORD}</span> to confirm
						<input
							className="purge-panel__confirm-input mono"
							value={typed}
							onChange={(e) => setTyped(e.target.value)}
							placeholder={CONFIRMATION_WORD}
							aria-label={`Type ${CONFIRMATION_WORD} to confirm`}
							autoComplete="off"
							autoFocus
						/>
					</label>
					{requestError && (
						<div className="purge-panel__error" aria-live="assertive">
							{requestError}
						</div>
					)}
					<div className="purge-panel__confirm-actions">
						<button
							type="button"
							onClick={() => {
								setConfirming(false);
								setTyped("");
								reset();
							}}
							disabled={busy}
						>
							Cancel
						</button>
						<button
							type="button"
							className="purge-panel__confirm-submit"
							disabled={typed !== CONFIRMATION_WORD || busy}
							onClick={() => void confirmPurge()}
						>
							{busy ? "Purging…" : "Purge run"}
						</button>
					</div>
				</div>
			)}

			{inProgress && status && (
				<div className="purge-panel__progress" aria-live="polite">
					Purging… {status.artifacts_deleted} of {status.artifacts_total || "?"} artifacts removed.
				</div>
			)}

			{failed && status && (
				<div className="purge-panel__failed" aria-live="assertive">
					<div className="purge-panel__failed-message">
						Purge did not complete: {status.last_error ?? "an unknown error occurred"}. {status.artifacts_deleted} of{" "}
						{status.artifacts_total} artifacts were removed before the failure.
					</div>
					<button type="button" className="purge-panel__retry" disabled={busy} onClick={() => void retryPurge()}>
						{busy ? "Retrying…" : "Retry purge"}
					</button>
				</div>
			)}
		</div>
	);
}
