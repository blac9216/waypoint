/**
 * Fixed-layout artifact table — docs/ui/prototype/README.md screen 6:
 *
 *   "Table `table-layout:fixed`: checkbox 7% (`7px 6px 7px 14px` padding —
 *   a 5% column cannot hold a 13px box plus 26px of padding) / ARTIFACT 30%
 *   with sha256 subline / PRODUCT 15% / VERSION 12% / SIZE 12% / STATUS 24%.
 *   Status is `not downloaded` `--txt3` · `queued` `--warn` ·
 *   `downloading 43%` `--acc` with an inline progress bar · `verified`
 *   `--ok` · `failed — checksum mismatch` `--bad`. Selected rows get
 *   `--accd`."
 *
 * Plus "Layout Rules Learned the Hard Way": percentage columns sum to 100%
 * on a `table-layout:fixed` table; nowrap cells get `overflow:hidden`;
 * status text must never truncate silently (abbreviate deliberately, full
 * value in `title`).
 */
import type { DownloadQueueItem } from "./catalog";
import type { CatalogArtifact } from "./catalog";
import { formatBytes } from "./catalog";
import "./ArtifactTable.css";

export interface ArtifactTableProps {
	artifacts: CatalogArtifact[];
	loading: boolean;
	selected: Set<string>;
	onToggle: (id: string) => void;
	onToggleAll: () => void;
	byArtifact: Map<string, DownloadQueueItem>;
	onRetry: (id: string) => void;
	canQueue: boolean;
}

function statusLabel(artifact: CatalogArtifact, live: DownloadQueueItem | undefined): { text: string; tone: string } {
	// Live SSE state (when present) wins over the last REST snapshot — the
	// contract's whole point is that progress never needs a re-fetch.
	if (live) {
		if (live.state === "downloading" || live.state === "verifying") {
			return { text: `downloading ${live.progress_percent}%`, tone: "acc" };
		}
		if (live.state === "verified") {
			return { text: "verified", tone: "ok" };
		}
		if (live.state === "failed") {
			return { text: "failed — checksum mismatch", tone: "bad" };
		}
		if (live.state === "queued") {
			return { text: "queued", tone: "warn" };
		}
	}
	switch (artifact.status) {
		case "queued":
			return { text: "queued", tone: "warn" };
		case "downloading":
			return { text: `downloading ${artifact.progress_percent ?? 0}%`, tone: "acc" };
		case "verified":
			return { text: "verified", tone: "ok" };
		case "failed":
			return { text: `failed — ${artifact.failure_reason ?? "checksum mismatch"}`, tone: "bad" };
		default:
			return { text: "not downloaded", tone: "txt3" };
	}
}

export function ArtifactTable({
	artifacts,
	loading,
	selected,
	onToggle,
	onToggleAll,
	byArtifact,
	onRetry,
	canQueue,
}: ArtifactTableProps) {
	// Membership check, not a size comparison: `selected` is shared across
	// every product group's table (issue #796), so a size match against just
	// this group's artifact count would be wrong whenever other groups also
	// have selected rows.
	const allSelected = artifacts.length > 0 && artifacts.every((a) => selected.has(a.id));

	return (
		<div className="artifact-table__scroll">
			<table className="artifact-table">
				<colgroup>
					<col style={{ width: "7%" }} />
					<col style={{ width: "30%" }} />
					<col style={{ width: "15%" }} />
					<col style={{ width: "12%" }} />
					<col style={{ width: "12%" }} />
					<col style={{ width: "24%" }} />
				</colgroup>
				<thead>
					<tr>
						<th className="artifact-table__checkbox-cell">
							<input
								type="checkbox"
								checked={allSelected}
								onChange={onToggleAll}
								aria-label="Select all artifacts"
								disabled={artifacts.length === 0}
							/>
						</th>
						<th>ARTIFACT</th>
						<th>PRODUCT</th>
						<th>VERSION</th>
						<th>SIZE</th>
						<th>STATUS</th>
					</tr>
				</thead>
				<tbody>
					{loading && (
						<tr>
							<td colSpan={6} className="artifact-table__empty">
								Loading catalog…
							</td>
						</tr>
					)}
					{!loading && artifacts.length === 0 && (
						<tr>
							<td colSpan={6} className="artifact-table__empty">
								No artifacts match the current filters.
							</td>
						</tr>
					)}
					{!loading &&
						artifacts.map((artifact) => {
							const live = byArtifact.get(artifact.id);
							const status = statusLabel(artifact, live);
							const isSelected = selected.has(artifact.id);
							const isFailed = status.tone === "bad";
							return (
								<tr
									key={artifact.id}
									className={`artifact-table__row ${isSelected ? "is-selected" : ""} ${isFailed ? "is-failed" : ""}`}
								>
									<td className="artifact-table__checkbox-cell">
										<input
											type="checkbox"
											checked={isSelected}
											onChange={() => onToggle(artifact.id)}
											aria-label={`Select ${artifact.name}`}
										/>
									</td>
									<td className="artifact-table__artifact-cell">
										<div className="artifact-table__name" title={artifact.name}>
											{artifact.name}
										</div>
										<div className="artifact-table__sha mono" title={artifact.sha256}>
											{artifact.sha256}
										</div>
									</td>
									<td className="artifact-table__truncate" title={artifact.product}>
										{artifact.product}
									</td>
									<td className="artifact-table__truncate mono" title={artifact.version}>
										{artifact.version}
									</td>
									<td className="artifact-table__truncate mono">{formatBytes(artifact.size_bytes)}</td>
									<td className="artifact-table__status-cell">
										<span
											className={`artifact-table__status artifact-table__status--${status.tone}`}
											title={status.text}
										>
											{status.text}
										</span>
										{status.tone === "acc" && (
											<div className="artifact-table__progress">
												<div
													className="artifact-table__progress-fill"
													style={{
														width: `${Math.min(100, Math.max(0, live?.progress_percent ?? artifact.progress_percent ?? 0))}%`,
													}}
												/>
											</div>
										)}
										{isFailed && canQueue && (
											<button
												type="button"
												className="artifact-table__retry"
												onClick={() => onRetry(artifact.id)}
											>
												Retry
											</button>
										)}
									</td>
								</tr>
							);
						})}
				</tbody>
			</table>
		</div>
	);
}
