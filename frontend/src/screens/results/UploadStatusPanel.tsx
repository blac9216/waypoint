/**
 * STIG Manager upload-status sidebar panel — extracted from
 * `ResultsScreen.tsx` (issue #416 decomposition, no behavior change). Retry
 * is a visible-but-disabled stub until the STIG Manager integration (issue
 * #25) lands.
 */
import { stigManagerStatusLabel, type RunJobItem } from "./results";

export function UploadStatusPanel({ jobs }: { jobs: RunJobItem[] }) {
	const uploaded = jobs.filter((j) => stigManagerStatusLabel(j.state) === "uploaded").length;
	const notUploaded = jobs.filter((j) => stigManagerStatusLabel(j.state) === "not-uploaded").length;
	return (
		<div className="results__panel results__panel--sidebar">
			<div className="results__panel-title">UPLOAD STATUS</div>
			<div className="results__stat-row">
				<span className="results__stat-label">Uploaded</span>
				<span className="mono results__stat-value results__stat-value--ok">
					{uploaded} / {jobs.length}
				</span>
			</div>
			<div className="results__stat-row">
				<span className="results__stat-label">Not uploaded</span>
				<span className="mono results__stat-value results__stat-value--bad">{notUploaded}</span>
			</div>
			<button
				type="button"
				className="results__retry-btn"
				disabled
				title="Retry is stubbed until the STIG Manager integration (issue #25) lands"
			>
				Retry failed uploads
			</button>
		</div>
	);
}
