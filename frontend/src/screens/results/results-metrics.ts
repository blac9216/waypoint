/**
 * KPI tile derivation for `ResultsScreen` — extracted from
 * `ResultsScreen.tsx` (issue #416 decomposition, no behavior change). Pure
 * functions over `RunListItem`/`RunArtifactRow`, no React.
 */
import type { RunArtifactRow, RunListItem } from "./results";

/** Sums a CAT column across rows that have `counts_available:true`, and
 * separately reports whether every row was countable. A KPI tile summed
 * across a mix of countable and uncountable rows would silently understate
 * the true open-finding count as if the uncountable rows were zero — the
 * same "never fabricate 0" rule `SeverityPill` enforces per-row applies to
 * the aggregate too, so the tile falls back to "n/a" rather than a
 * misleadingly precise-looking partial sum whenever any row is uncountable. */
export function sumCatColumn(artifacts: RunArtifactRow[] | null, field: "cat_i_open" | "cat_ii_open" | "cat_iii_open"): number | null {
	if (!artifacts || artifacts.length === 0) {
		return null;
	}
	if (artifacts.some((a) => !a.counts_available)) {
		return null;
	}
	return artifacts.reduce((sum, a) => sum + (a[field] ?? 0), 0);
}

export interface KpiTile {
	label: string;
	value: string;
	className: string;
}

export function kpiTiles(run: RunListItem | null, artifacts: RunArtifactRow[] | null): KpiTile[] {
	const jobTotal = run?.job_count ?? 0;
	const jobDone = run?.job_count_completed ?? 0;
	const compliancePercent = jobTotal > 0 ? Math.round((jobDone / jobTotal) * 1000) / 10 : 0;
	const catI = sumCatColumn(artifacts, "cat_i_open");
	const catII = sumCatColumn(artifacts, "cat_ii_open");
	const catIII = sumCatColumn(artifacts, "cat_iii_open");
	return [
		{ label: "COMPLIANCE", value: `${compliancePercent}%`, className: "results__kpi--ok" },
		{ label: "CAT I OPEN", value: catI === null ? "n/a" : String(catI), className: catI === null ? "results__kpi--na" : "results__kpi--bad" },
		{
			label: "CAT II OPEN",
			value: catII === null ? "n/a" : String(catII),
			className: catII === null ? "results__kpi--na" : "results__kpi--warn",
		},
		{
			label: "CAT III OPEN",
			value: catIII === null ? "n/a" : String(catIII),
			className: catIII === null ? "results__kpi--na" : "results__kpi--muted",
		},
		{ label: "ATTESTED N/A", value: "—", className: "results__kpi--na" },
	];
}
