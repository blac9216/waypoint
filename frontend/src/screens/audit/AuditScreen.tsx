/**
 * Audit — issue #531 (epic #14, split from #32). Cyber+ read-only view over
 * `GET /api/v1/audit` (`AuditController`, PR #529/#512): every
 * `audit_log` row this codebase's writers already insert (secret decrypt,
 * job retry, credential swap/delete, run initiation).
 *
 * No prototype screen exists for this surface (issue #531's Motivation) —
 * layout below is invented, following the `config-table`/`config-panel`
 * conventions the rest of Configuration already uses (`ConfigurationScreen.css`)
 * rather than a bespoke look. Filters (kind/actor/from/to) narrow the same
 * query the CSV export downloads, so what an operator sees is exactly what
 * they export (`AuditController.List`'s "one query surface, two
 * representations" comment).
 *
 * Role gating is entirely route-level (`routes.ts`'s `/audit`,
 * `requiredRole: "Cyber"` — see that file's comment for why this is a
 * top-level route rather than nested under `/config`): this screen renders
 * no in-screen role checks of its own, matching `start-scan`'s precedent
 * for other Cyber+-only screens.
 */
import { useState, type FormEvent } from "react";
import { useAuth } from "../../lib/auth-context";
import "../configuration/ConfigurationScreen.css";
import { formatTimestamp } from "../configuration/credentials";
import type { AuditFilter } from "./audit";
import "./AuditScreen.css";
import { PAGE_SIZE, useAuditLog } from "./useAuditLog";
import { useAuditExport } from "./useAuditExport";

function detailSummary(detail: string): string {
	if (!detail) {
		return "—";
	}
	return detail.length > 120 ? `${detail.slice(0, 120)}…` : detail;
}

export function AuditScreen() {
	const { token } = useAuth();
	const { entries, totalCount, loading, loadError, filter, setFilter, offset, setOffset } = useAuditLog();
	const { exporting, exportError, handleExport } = useAuditExport(filter, token);

	const [kindInput, setKindInput] = useState("");
	const [actorInput, setActorInput] = useState("");
	const [fromInput, setFromInput] = useState("");
	const [toInput, setToInput] = useState("");

	const applyFilters = (event: FormEvent) => {
		event.preventDefault();
		const next: AuditFilter = {};
		if (kindInput.trim()) {
			next.kind = kindInput.trim();
		}
		if (actorInput.trim()) {
			next.actor = actorInput.trim();
		}
		if (fromInput.trim()) {
			next.from = new Date(fromInput).toISOString();
		}
		if (toInput.trim()) {
			next.to = new Date(toInput).toISOString();
		}
		setFilter(next);
	};

	const clearFilters = () => {
		setKindInput("");
		setActorInput("");
		setFromInput("");
		setToInput("");
		setFilter({});
	};

	const hasNextPage = offset + PAGE_SIZE < totalCount;
	const hasPrevPage = offset > 0;
	const rangeStart = totalCount === 0 ? 0 : offset + 1;
	const rangeEnd = Math.min(offset + PAGE_SIZE, totalCount);

	return (
		<div className="config-screen audit-screen">
			<div className="config-screen__content">
				<div className="config-panel">
					<div className="config-panel__header">
						<div className="config-panel__title">AUDIT LOG</div>
						<div className="config-panel__spacer" />
						<button type="button" onClick={() => void handleExport()} disabled={exporting || loading}>
							{exporting ? "Exporting…" : "Export CSV"}
						</button>
					</div>

					<form className="audit-screen__filters" onSubmit={applyFilters}>
						<label className="audit-screen__field">
							<span>Kind</span>
							<input
								value={kindInput}
								onChange={(e) => setKindInput(e.target.value)}
								placeholder="e.g. secret.decrypted"
							/>
						</label>
						<label className="audit-screen__field">
							<span>Actor</span>
							<input value={actorInput} onChange={(e) => setActorInput(e.target.value)} placeholder="e.g. j.moreno" />
						</label>
						<label className="audit-screen__field">
							<span>From</span>
							<input type="datetime-local" value={fromInput} onChange={(e) => setFromInput(e.target.value)} />
						</label>
						<label className="audit-screen__field">
							<span>To</span>
							<input type="datetime-local" value={toInput} onChange={(e) => setToInput(e.target.value)} />
						</label>
						<div className="audit-screen__filter-actions">
							<button type="submit">Apply</button>
							<button type="button" onClick={clearFilters}>
								Clear
							</button>
						</div>
					</form>

					{loadError && <div className="config-panel__error">{loadError}</div>}
					{exportError && <div className="config-panel__error">{exportError}</div>}

					<table className="config-table">
						<colgroup>
							<col style={{ width: "14%" }} />
							<col style={{ width: "18%" }} />
							<col style={{ width: "14%" }} />
							<col style={{ width: "12%" }} />
							<col style={{ width: "12%" }} />
							<col style={{ width: "30%" }} />
						</colgroup>
						<thead>
							<tr>
								<th>WHEN</th>
								<th>KIND</th>
								<th>ACTOR</th>
								<th>RUN</th>
								<th>JOB</th>
								<th>DETAIL</th>
							</tr>
						</thead>
						<tbody>
							{loading && (
								<tr>
									<td colSpan={6} className="config-table__empty">
										Loading audit log…
									</td>
								</tr>
							)}
							{!loading && entries.length === 0 && (
								<tr>
									<td colSpan={6} className="config-table__empty">
										No audit entries match this filter.
									</td>
								</tr>
							)}
							{!loading &&
								entries.map((entry) => (
									<tr key={entry.id}>
										<td className="mono">{formatTimestamp(entry.occurred_at)}</td>
										<td>
											<span className="config-table__kind">{entry.event_type}</span>
										</td>
										<td className="config-table__truncate">{entry.actor}</td>
										<td className="mono config-table__truncate">{entry.run_id ?? "—"}</td>
										<td className="mono config-table__truncate">{entry.job_id ?? "—"}</td>
										<td className="config-table__truncate" title={entry.detail}>
											{detailSummary(entry.detail)}
										</td>
									</tr>
								))}
						</tbody>
					</table>

					<div className="audit-screen__pager">
						<span>
							{totalCount === 0 ? "0 results" : `${rangeStart}–${rangeEnd} of ${totalCount}`}
						</span>
						<div className="audit-screen__pager-actions">
							<button type="button" disabled={!hasPrevPage || loading} onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}>
								Previous
							</button>
							<button type="button" disabled={!hasNextPage || loading} onClick={() => setOffset(offset + PAGE_SIZE)}>
								Next
							</button>
						</div>
					</div>
				</div>
			</div>
		</div>
	);
}
