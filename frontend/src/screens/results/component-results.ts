/**
 * Component-results data layer (issue #745 remainder): typed clients for the
 * two backend read surfaces this slice consumes —
 *
 *   - `GET /runs/{id}/component-results/summary` (PR #952): a pure SQL
 *     GROUP BY rollup of the LATEST attempt per `scan_plan_items` row,
 *     bucketed by the six-status vocabulary
 *     (`completed`/`execution_error`/`skipped` at the component-result
 *     level; `NotReviewedCount`/`PassedCount`/etc. are per-finding sums
 *     within each bucket — see `ComponentResultRollupStatus` below for the
 *     exact shape). `planned_component_count` is the plan's total accepted
 *     item count, so coverage (planned vs. resulted, including plan
 *     omissions/skips) is `sum(by_status[].component_count)` vs. that
 *     number — a plan item with no result row at all (still queued/running,
 *     or a pre-#745 run) is coverage that is simply not yet reflected,
 *     never fabricated into any bucket.
 *
 *   - `GET /jobs/{id}/upload-attempts` (issue #744 remainder): the
 *     append-only STIG Manager upload-attempt history for one job
 *     (migration 0062), oldest-first.
 *
 * The six-status vocabulary rendered "honestly" per the design brief's
 * Results map: `execution_error` and `not_reviewed` are visually distinct
 * from `completed`/`failed`/`passed` — never collapsed into a single
 * pass/fail read. `component_results.status` (`completed`/`execution_error`/
 * `skipped`) answers "did the job attempt finish", while
 * `component_result_findings.status` (`passed`/`failed`/`not_applicable`/
 * `not_reviewed`/`execution_error`/`skipped`) answers "what did each control
 * resolve to" — the rollup response mixes both because CAT/passed/etc.
 * counts are finding-level sums grouped by the component-level status
 * bucket they occurred under.
 *
 * There is no per-component finding-list or artifact-list read endpoint
 * yet (PR #952's stated remainder) — this module only covers what the
 * backend actually serves today. `ComponentResultRollupStatus` intentionally
 * has no findings/artifacts array.
 */
import { apiGet } from "../../lib/api";

/** The closed component-result-attempt status vocabulary (migration 0063's
 * `component_results_status_check`, `ComponentResultStatuses` in
 * ComponentResultModels.cs). */
export type ComponentResultStatus = "completed" | "execution_error" | "skipped";

/** One status bucket of `GET /runs/{id}/component-results/summary`
 * (`RunResultRollupStatusResponse`, RunContracts.cs). `component_count` is a
 * count of COMPONENTS (scan_plan_items), not findings; the CAT/passed/etc.
 * fields are the SUM across those components' latest attempts' findings. */
export interface ComponentResultRollupStatus {
	status: ComponentResultStatus | string;
	component_count: number;
	cat_i_open: number;
	cat_ii_open: number;
	cat_iii_open: number;
	passed_count: number;
	not_applicable_count: number;
	not_reviewed_count: number;
	skipped_count: number;
}

/** `RunResultRollupResponse` (RunContracts.cs). */
export interface ComponentResultRollup {
	run_id: string;
	planned_component_count: number;
	by_status: ComponentResultRollupStatus[];
}

/** `GET /runs/{id}/component-results/summary` — Viewer+. A run with no
 * component_results rows at all (pre-#745 run, purged run, or one whose
 * jobs have not yet completed) returns `by_status: []`, not a 404 — the run
 * itself existing is the only precondition. Callers must render that as "no
 * results yet", never as an error. */
export function fetchComponentResultsSummary(runId: string): Promise<ComponentResultRollup> {
	return apiGet<ComponentResultRollup>(`/runs/${runId}/component-results/summary`);
}

/** Sums every bucket's `component_count` — the "resulted" half of the
 * planned-vs-resulted coverage ledger. */
export function totalResultedComponents(rollup: ComponentResultRollup): number {
	return rollup.by_status.reduce((sum, row) => sum + row.component_count, 0);
}

/** Components planned but with no result row at all yet — queued/running,
 * or (for a historical run) never claimed. Never negative: a rollup whose
 * resulted count exceeds planned (should not happen) clamps to 0 rather
 * than rendering a nonsensical negative coverage gap. */
export function unresultedComponentCount(rollup: ComponentResultRollup): number {
	return Math.max(0, rollup.planned_component_count - totalResultedComponents(rollup));
}

/** Total CAT I/II/III open findings across every status bucket — used for
 * the run-level KPI tiles alongside the existing per-target artifact sums. */
export function totalOpenBySeverity(rollup: ComponentResultRollup): { catI: number; catII: number; catIII: number } {
	return rollup.by_status.reduce(
		(totals, row) => ({
			catI: totals.catI + row.cat_i_open,
			catII: totals.catII + row.cat_ii_open,
			catIII: totals.catIII + row.cat_iii_open,
		}),
		{ catI: 0, catII: 0, catIII: 0 },
	);
}

/** One immutable row of `GET /jobs/{id}/upload-attempts`
 * (`UploadAttemptResponse`, RunContracts.cs — issue #744 remainder).
 * Oldest-first, matching the server's `ORDER BY attempt_number`. */
export interface UploadAttempt {
	attempt_number: number;
	endpoint: string | null;
	collection: string | null;
	status: string;
	error_detail: string | null;
	attempted_at: string;
}

/** `GET /jobs/{id}/upload-attempts` — Viewer+. A job with no recorded
 * attempts (never uploaded, or a non-scan job) returns `[]`, not a 404. */
export function fetchUploadAttempts(jobId: string): Promise<UploadAttempt[]> {
	return apiGet<UploadAttempt[]>(`/jobs/${jobId}/upload-attempts`);
}

/** Display label for a component-result status — never a bare code, and
 * `execution_error`/`skipped` never read as a plain "failed"/"done" (design
 * brief: the six-status vocabulary rendered honestly). */
export function componentResultStatusLabel(status: string): string {
	switch (status) {
		case "completed":
			return "Completed";
		case "execution_error":
			return "Execution error";
		case "skipped":
			return "Skipped";
		default:
			return status;
	}
}

/** CSS-suffix-safe class modifier per status — distinct visual treatment for
 * every bucket (never collapsing execution_error/skipped into the same
 * "bad"/"ok" binary as completed). */
export function componentResultStatusClass(status: string): string {
	switch (status) {
		case "completed":
			return "results__cresult-status--completed";
		case "execution_error":
			return "results__cresult-status--error";
		case "skipped":
			return "results__cresult-status--skipped";
		default:
			return "results__cresult-status--unknown";
	}
}
