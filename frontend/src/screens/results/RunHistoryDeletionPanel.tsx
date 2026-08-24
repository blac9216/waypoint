/**
 * Generic operational-history deletion for Compliance Results (issue #592,
 * epic #588's last child) — against `DELETE`/`GET /runs/{id}/history`
 * (`RunsController`, `RunContracts.cs`). Admin-only, terminal-runs-only,
 * requires the operator to type the literal `DELETE` confirmation, mirroring
 * `PurgeRunPanel`'s step-up pattern.
 *
 * Structurally distinct from `PurgeRunPanel`: this is the OPERATIONAL-history
 * record (the run/job rows' visibility), not the compliance-owned artifacts
 * purge already handles. On this screen (scan/remediate runs only, per issue
 * #591's `COMPLIANCE_RUN_TYPES` filter) the two are ordered — the server
 * refuses history deletion for an unpurged compliance run with 409
 * `requires_domain_purge_first` — so this panel only renders once the run has
 * been purged (`ResultsScreen.tsx` gates it behind `purged`), and its refusal
 * message routes the operator back to the purge action above it rather than
 * leaving a bare 409 on screen.
 *
 * States rendered here:
 *   - Not deleted, Admin: "Delete history…" button opens the typed
 *     confirmation. Non-Admin: visible-but-disabled with a reason.
 *   - Failed (`requestErrorCode === "requires_domain_purge_first"`): an honest
 *     message pointing back at the purge action (should not normally be
 *     reachable from this screen given the render gate above, but the
 *     message stays honest in case of a race — e.g. two admins acting on the
 *     same run concurrently).
 *   - Completed/AlreadyDeleted: a tombstone summary replaces the trigger.
 */
import { useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { roleGateProps } from "../../lib/roles";
import { formatTimestamp, isTerminalRunState, type RunListItem } from "./results";
import { useRunHistoryDeletion } from "./useRunHistoryDeletion";
import "./PurgeRunPanel.css";

const CONFIRMATION_WORD = "DELETE";

export interface RunHistoryDeletionPanelProps {
	run: RunListItem;
}

export function RunHistoryDeletionPanel({ run }: RunHistoryDeletionPanelProps) {
	const { user } = useAuth();
	const { status, busy, requestError, requestErrorCode, confirmDelete, loadExistingStatus, reset } = useRunHistoryDeletion(run.id);
	const [confirming, setConfirming] = useState(false);
	const [typed, setTyped] = useState("");

	useEffect(() => {
		void loadExistingStatus();
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [run.id]);

	const terminal = isTerminalRunState(run.state);
	const gate = user
		? roleGateProps(user.role, "Admin", `Requires Admin — deleting operational history is not available to ${user.role}`)
		: { disabled: true, style: { opacity: 0.42 } };
	const deleteDisabled = gate.disabled || !terminal || busy;
	const deleteTitle = gate.disabled ? gate.title : !terminal ? "Run must reach a terminal state before its history can be deleted" : undefined;

	const deleted = status?.outcome === "Completed" || status?.outcome === "AlreadyDeleted";

	if (deleted && status) {
		return (
			<div className="purge-panel purge-panel--tombstone" aria-live="polite">
				<div className="purge-panel__title">OPERATIONAL HISTORY DELETED</div>
				<div className="purge-panel__tombstone-body">
					This run's operational history record was deleted by {status.actor || "—"} at {formatTimestamp(status.occurred_at)} (prior
					state: {status.prior_state || "—"}). The run and job rows themselves are retained for referential integrity but are no
					longer part of the operator-facing history.
				</div>
			</div>
		);
	}

	return (
		<div className="purge-panel">
			{!confirming && (
				<button type="button" className="purge-panel__trigger" disabled={deleteDisabled} title={deleteTitle} onClick={() => setConfirming(true)}>
					Delete history…
				</button>
			)}

			{confirming && (
				<div className="purge-panel__confirm" role="group" aria-label="Confirm history deletion">
					<div className="purge-panel__confirm-warning">
						This permanently deletes this run's operational history record (its visibility in Jobs/history). The run's
						compliance results have already been purged separately. This cannot be undone.
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
							{requestErrorCode === "requires_domain_purge_first"
								? "This run must be purged first — use the purge action above before deleting its operational history."
								: requestError}
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
							onClick={() => void confirmDelete()}
						>
							{busy ? "Deleting…" : "Delete history"}
						</button>
					</div>
				</div>
			)}
		</div>
	);
}
