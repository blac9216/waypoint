/**
 * Per-target artifacts table panel — extracted from `ResultsScreen.tsx`
 * (issue #416 decomposition, no behavior change). See that file's module doc
 * for the data-source breakdown.
 *
 * Severity labels (AC1, design-brief "Layout Rules Learned the Hard Way" #4):
 * always rendered as the full "CAT I"/"CAT II"/"CAT III" text, in a pill wide
 * enough for the longest label with no `text-overflow: ellipsis` — never
 * abbreviated to a bare Roman numeral. See ResultsScreen.test.tsx for the
 * non-truncation assertion.
 */
import { controlsEvaluatedLabel, controlsUnderEvaluated, SEVERITIES, type RunArtifactRow, type RunJobItem, type Severity } from "./results";

const SEVERITY_CLASS: Record<Severity, string> = {
	"CAT I": "results__severity--1",
	"CAT II": "results__severity--2",
	"CAT III": "results__severity--3",
};

/** Full-text severity pill (AC1 / layout rule 4): the label is always the
 * complete "CAT I"/"CAT II"/"CAT III" string, never a bare numeral, and the
 * pill has no `overflow:hidden`/`text-overflow:ellipsis` — see
 * ResultsScreen.test.tsx for the assertion this backs.
 *
 * `count` is `undefined` when the row's `counts_available` is `false` (HDF
 * absent/unparseable) — rendered as an explicit "n/a", never a bare `0`.
 * Collapsing "could not count" into `0` would read as a clean, compliant
 * target on a corrupt scan (issue #307 / #299 round-1 review blocker). */
export function SeverityPill({ severity, count }: { severity: Severity; count: number | undefined }) {
	const display = count === undefined ? "n/a" : String(count);
	const title = count === undefined ? `${severity}: not available (could not count)` : `${severity} open: ${count}`;
	return (
		<span
			className={`results__severity ${count === undefined ? "results__severity--na" : SEVERITY_CLASS[severity]}`}
			title={title}
		>
			{severity} <span className="mono">{display}</span>
		</span>
	);
}

export function UploadStatusPill({ status }: { status: RunArtifactRow["upload_status"] }) {
	const label = status === "not-uploaded" ? "not uploaded" : status;
	return <span className={`results__upload-pill results__upload-pill--${status}`}>{label}</span>;
}

/** Evaluated-controls denominator (issue #1132/#1140) — rendered next to the
 * CAT severity pills so a reader cannot mistake an all-zero `0/0/0` open row
 * for a clean scan without checking how many controls were actually
 * evaluated. `controlsEvaluatedLabel`/`controlsUnderEvaluated` already gate
 * on `counts_available` and render "n/a" rather than a fabricated `0/0` —
 * see results.ts for that logic. */
export function EvaluatedDenominator({ row }: { row: RunArtifactRow }) {
	const label = controlsEvaluatedLabel(row);
	const underEvaluated = controlsUnderEvaluated(row);
	const title = underEvaluated
		? `Evaluated ${label} controls — this scan did not evaluate every control it reported, so an all-zero CAT count above is not necessarily clean.`
		: label === "n/a"
			? "Evaluated controls not available (could not count)."
			: `Evaluated ${label} controls.`;
	return (
		<span className={`results__evaluated ${underEvaluated ? "results__evaluated--warn" : ""}`} title={title}>
			{label}
		</span>
	);
}

/** `controls_execution_error` pill (issue #1144/#1247) — deliberately its own
 * column, never merged into the CAT open-finding counts: an errored control
 * never produced a genuine compliance verdict, so it is not "open". */
export function ExecutionErrorCount({ row }: { row: RunArtifactRow }) {
	if (!row.counts_available || row.controls_execution_error == null) {
		return (
			<span className="results__exec-err results__exec-err--na" title="Execution-error count not available (could not count).">
				n/a
			</span>
		);
	}
	const count = row.controls_execution_error;
	return (
		<span
			className={`results__exec-err ${count > 0 ? "results__exec-err--warn" : ""}`}
			title={`${count} control${count === 1 ? "" : "s"} produced no genuine compliance verdict (execution error) — distinct from an open finding.`}
		>
			{count}
		</span>
	);
}

export function ArtifactsTable({
	loading,
	artifacts,
	unavailable,
	jobs,
}: {
	loading: boolean;
	artifacts: RunArtifactRow[] | null;
	unavailable: boolean;
	jobs: RunJobItem[];
}) {
	return (
		<div className="results__panel">
			<div className="results__panel-header">
				<div className="results__panel-title">PER-TARGET ARTIFACTS</div>
				<div className="results__spacer" />
				<div className="mono results__panel-meta">{jobs.length} targets</div>
			</div>
			<table className="results__table">
				<thead>
					<tr>
						<th className="results__col-target">TARGET</th>
						<th className="results__col-bench">BENCHMARK</th>
						<th className="results__col-sev">{SEVERITIES[0]}</th>
						<th className="results__col-sev">{SEVERITIES[1]}</th>
						<th className="results__col-sev">{SEVERITIES[2]}</th>
						<th className="results__col-evaluated">EVALUATED</th>
						<th className="results__col-exec-err">EXEC ERR</th>
						<th className="results__col-artifacts">ARTIFACTS</th>
						<th className="results__col-stigman">STIG MANAGER</th>
					</tr>
				</thead>
				<tbody>
					{loading && (
						<tr>
							<td colSpan={9} className="results__empty">
								Loading artifacts…
							</td>
						</tr>
					)}
					{!loading && unavailable && (
						<tr>
							<td colSpan={9} className="results__empty">
								Per-target artifacts could not be loaded for this run — GET /runs/{"{id}"}/artifacts failed or returned
								no data.
							</td>
						</tr>
					)}
					{!loading &&
						!unavailable &&
						artifacts?.map((row) => (
							<tr key={row.job_id}>
								<td className="results__col-target mono">{row.target}</td>
								<td className="results__col-bench mono">{row.benchmark ?? "—"}</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT I" count={row.counts_available ? row.cat_i_open : undefined} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT II" count={row.counts_available ? row.cat_ii_open : undefined} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT III" count={row.counts_available ? row.cat_iii_open : undefined} />
								</td>
								<td className="results__col-evaluated">
									<EvaluatedDenominator row={row} />
								</td>
								<td className="results__col-exec-err">
									<ExecutionErrorCount row={row} />
								</td>
								<td className="results__col-artifacts mono">{row.artifact_kinds.join(" · ").toUpperCase()}</td>
								<td className="results__col-stigman">
									<UploadStatusPill status={row.upload_status} />
								</td>
							</tr>
						))}
					{!loading && !unavailable && artifacts?.length === 0 && (
						<tr>
							<td colSpan={9} className="results__empty">
								No artifacts for this run.
							</td>
						</tr>
					)}
				</tbody>
			</table>
		</div>
	);
}
