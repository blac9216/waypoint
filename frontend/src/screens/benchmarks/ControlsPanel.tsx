/**
 * Per-control inspection panel (issue #598) — renders the control inventory
 * `GET /profiles/{id}/controls` returns: id, severity, title, and, once a
 * target is selected, the profile-level effective input/attest status (see
 * benchmarks.ts's module doc for why those two fields are profile-level, not
 * truly per-control). Slots in additively next to `LayerDocPanel`'s
 * whole-document editor (#559's Benchmarks detail pane), never replacing it.
 */
import { useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchProfileControls, type ProfileControlSummary } from "./benchmarks";
import "./BenchmarksScreen.css";

const ATTEST_STATUS_LABEL: Record<ProfileControlSummary["attest_status"], string> = {
	applied: "Applied",
	expired: "Expired",
	none: "None",
};

export function ControlsPanel({
	profileId,
	targetId,
	initialSearch,
}: {
	profileId: string;
	targetId: string | null;
	/** Pre-fills the search box — issue #598's forward-compatible deep-link
	 * slot for a future Results row that carries a real control id (see
	 * `BenchmarksScreen`'s `useBenchmarksDeepLink` doc comment). */
	initialSearch?: string | null;
}) {
	const [controls, setControls] = useState<ProfileControlSummary[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [search, setSearch] = useState(initialSearch ?? "");

	useEffect(() => {
		setLoading(true);
		setLoadError(null);
		fetchProfileControls(profileId, targetId)
			.then(setControls)
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load this profile's controls.");
			})
			.finally(() => setLoading(false));
	}, [profileId, targetId]);

	const filtered = useMemo(() => {
		const q = search.trim().toLowerCase();
		if (!q) {
			return controls;
		}
		return controls.filter(
			(c) => c.control_id.toLowerCase().includes(q) || (c.title ?? "").toLowerCase().includes(q) || (c.severity ?? "").toLowerCase().includes(q),
		);
	}, [controls, search]);

	return (
		<div className="bench-controls">
			<div className="bench-controls__header">
				<div className="bench-panel__title">CONTROLS</div>
				<input
					className="bench-search"
					type="search"
					placeholder="Search controls…"
					value={search}
					onChange={(event) => setSearch(event.target.value)}
				/>
			</div>

			{loading && <div className="bench-layer__status" aria-live="polite">Loading controls…</div>}
			{loadError && <div className="bench-layer__error" aria-live="polite">{loadError}</div>}

			{!loading && !loadError && controls.length === 0 && (
				<div className="bench-panel__empty">No controls were parsed for this profile.</div>
			)}
			{!loading && !loadError && controls.length > 0 && filtered.length === 0 && (
				<div className="bench-panel__empty">No controls match "{search}".</div>
			)}

			{!loading && !loadError && filtered.length > 0 && (
				<table className="bench-controls__table">
					<thead>
						<tr>
							<th>Control</th>
							<th>Severity</th>
							<th>Title</th>
							{targetId && <th>Effective input</th>}
							{targetId && <th>Attest status</th>}
						</tr>
					</thead>
					<tbody>
						{filtered.map((c) => (
							<tr key={c.id}>
								<td className="mono">{c.control_id}</td>
								<td>{c.severity ?? "—"}</td>
								<td>{c.title ?? "—"}</td>
								{targetId && <td className="mono">{c.effective_input ?? "—"}</td>}
								{targetId && (
									<td>
										<span className={`bench-controls__attest-pill bench-controls__attest-pill--${c.attest_status}`}>
											{ATTEST_STATUS_LABEL[c.attest_status]}
										</span>
									</td>
								)}
							</tr>
						))}
					</tbody>
				</table>
			)}
		</div>
	);
}
