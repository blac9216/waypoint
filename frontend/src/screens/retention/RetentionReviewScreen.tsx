/**
 * Retention review-list screen (issue #1481, epic #1182) — the UI half of the
 * retention engine, consuming the API `RetentionController` shipped by #1453.
 * Three sections per the issue's ACs:
 *
 *   1. Alerts    — grace-entry / review-list-addition, derived client-side
 *                  (see retention.ts's header comment for why).
 *   2. Grace list — imminent-purge display: grace/pending-purge content, with
 *                  a true time-remaining countdown (computed from #1962's
 *                  `grace_ends_at`) and pin / purge-now actions. Pinning
 *                  removes the row from this list (it now reports
 *                  `state: "pinned"`, which the fetch below excludes).
 *   3. Review list — orphan/out-of-scope content; delete is the only
 *                  mutating action available here (no bulk auto-action).
 *
 * Pin/purge-now/delete are Admin-only per RetentionController's RBAC
 * (`RequireAdminRole`); Viewer/Operator get the same read-only view with
 * every action disabled-with-reason (README "Roles & Permissions").
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleGateProps } from "../../lib/roles";
import {
	deleteReviewListEntry,
	deriveAlerts,
	fetchReviewList,
	fetchRetentionState,
	formatBytes,
	graceCountdownLabel,
	pinContent,
	purgeNow,
	type RetainedContentState,
	type ReviewListEntry,
} from "./retention";
import "./RetentionReviewScreen.css";

export function RetentionReviewScreen() {
	const { user } = useAuth();

	const [graceItems, setGraceItems] = useState<RetainedContentState[]>([]);
	const [reviewEntries, setReviewEntries] = useState<ReviewListEntry[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [actionError, setActionError] = useState<string | null>(null);
	const [busyId, setBusyId] = useState<string | null>(null);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		Promise.all([fetchRetentionState(), fetchReviewList()])
			.then(([states, review]) => {
				setGraceItems(states.filter((s) => s.state === "grace" || s.state === "pending-purge"));
				setReviewEntries(review);
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load retention state.");
			})
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const alerts = useMemo(() => deriveAlerts(graceItems, reviewEntries), [graceItems, reviewEntries]);

	const adminGate = user ? roleGateProps(user.role, "Admin") : { disabled: true };

	const handlePin = useCallback(
		async (item: RetainedContentState) => {
			setBusyId(item.id);
			setActionError(null);
			try {
				await pinContent(item.id);
				load();
			} catch (err) {
				setActionError(err instanceof ApiError ? err.message : "Could not pin this content.");
			} finally {
				setBusyId(null);
			}
		},
		[load],
	);

	const handlePurgeNow = useCallback(
		async (item: RetainedContentState) => {
			setBusyId(item.id);
			setActionError(null);
			try {
				await purgeNow(item.id);
				load();
			} catch (err) {
				setActionError(err instanceof ApiError ? err.message : "Could not purge this content.");
			} finally {
				setBusyId(null);
			}
		},
		[load],
	);

	const handleDelete = useCallback(
		async (entry: ReviewListEntry) => {
			const entryKey = entry.depot_artifact_id ?? entry.relative_path;
			setBusyId(entryKey);
			setActionError(null);
			try {
				await deleteReviewListEntry(entry);
				load();
			} catch (err) {
				setActionError(err instanceof ApiError ? err.message : "Could not delete this review-list entry.");
			} finally {
				setBusyId(null);
			}
		},
		[load],
	);

	return (
		<div className="retention-screen">
			<div className="retention-screen__header">
				<h1>Retention Review</h1>
				<p className="retention-screen__subtitle">Tracked content approaching or past its grace period, and content awaiting explicit review.</p>
			</div>

			{loadError && <div className="retention-screen__error">{loadError}</div>}
			{actionError && <div className="retention-screen__error">{actionError}</div>}

			<section className="retention-section" aria-label="Alerts">
				<h2>Alerts</h2>
				{alerts.length === 0 ? (
					<div className="retention-empty">No retention alerts.</div>
				) : (
					<ul className="retention-alert-list">
						{alerts.map((alert) => (
							<li key={alert.id} className={`retention-alert retention-alert--${alert.kind}`}>
								<span className="retention-alert__badge">{alert.kind === "grace-entry" ? "grace" : "review"}</span>
								<span>{alert.message}</span>
							</li>
						))}
					</ul>
				)}
			</section>

			<section className="retention-section" aria-label="Grace and pending-purge content">
				<h2>Grace &amp; pending purge</h2>
				<table className="retention-table">
					<thead>
						<tr>
							<th>ARTIFACT</th>
							<th>STATE</th>
							<th>COUNTDOWN</th>
							<th className="retention-col-actions">ACTIONS</th>
						</tr>
					</thead>
					<tbody>
						{graceItems.map((item) => (
							<tr key={item.id}>
								<td className="mono">{item.depot_artifact_id}</td>
								<td>
									<span className={`retention-badge retention-badge--${item.state}`}>{item.state}</span>
								</td>
								<td className="mono">{graceCountdownLabel(item.grace_ends_at)}</td>
								<td className="retention-col-actions">
									<button
										type="button"
										onClick={() => handlePin(item)}
										disabled={adminGate.disabled || busyId === item.id}
										title={adminGate.title}
									>
										Pin
									</button>
									<button
										type="button"
										onClick={() => handlePurgeNow(item)}
										disabled={adminGate.disabled || busyId === item.id}
										title={adminGate.title}
									>
										Purge now
									</button>
								</td>
							</tr>
						))}
						{!loading && graceItems.length === 0 && (
							<tr>
								<td colSpan={4} className="retention-table__empty">
									Nothing is currently in grace or pending purge.
								</td>
							</tr>
						)}
					</tbody>
				</table>
			</section>

			<section className="retention-section" aria-label="Review list">
				<h2>Review list</h2>
				<p className="retention-section__note">
					Orphaned and out-of-scope content, never removed automatically. Delete is the only action available here.
				</p>
				<table className="retention-table">
					<thead>
						<tr>
							<th>PATH</th>
							<th>KIND</th>
							<th>SIZE</th>
							<th>REASON</th>
							<th className="retention-col-actions">ACTIONS</th>
						</tr>
					</thead>
					<tbody>
						{reviewEntries.map((entry) => {
							const entryKey = entry.depot_artifact_id ?? entry.relative_path;
							return (
								<tr key={entryKey}>
									<td className="mono">{entry.relative_path}</td>
									<td>{entry.kind === "Orphan" ? "orphan" : "out of scope"}</td>
									<td className="mono">{formatBytes(entry.size_bytes)}</td>
									<td>{entry.reason ?? "—"}</td>
									<td className="retention-col-actions">
										<button
											type="button"
											onClick={() => handleDelete(entry)}
											disabled={adminGate.disabled || busyId === entryKey}
											title={adminGate.title}
										>
											Delete
										</button>
									</td>
								</tr>
							);
						})}
						{!loading && reviewEntries.length === 0 && (
							<tr>
								<td colSpan={5} className="retention-table__empty">
									Nothing is on the review list.
								</td>
							</tr>
						)}
					</tbody>
				</table>
			</section>
		</div>
	);
}
