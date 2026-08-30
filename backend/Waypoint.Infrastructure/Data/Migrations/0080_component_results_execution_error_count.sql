-- Issue #1144 (epic #1177): a component whose controls all mapped to
-- `execution_error` (HdfFindingsParser.MapStatus: any control with a `status: "error"`
-- result, or any unrecognized/mixed result shape) contributed to NO count column on
-- `component_results` -- `cat_i_open`/`cat_ii_open`/`cat_iii_open` only count
-- `ComponentFindingStatuses.Failed` findings (`ComponentFindingStatuses.IsOpen`), and
-- `passed_count`/`not_applicable_count`/`not_reviewed_count`/`skipped_count` cover the
-- remaining vocabulary. An all-`execution_error` component therefore read as
-- ALL-ZERO on `GET /runs/{id}/component-results/summary` -- invisible in the rollup,
-- not merely unflagged (verified against real Postgres while reviewing PR #1139).
--
-- Adds `execution_error_count`, the sixth and final per-finding-status count column
-- (mirroring `not_reviewed_count`/`skipped_count`'s own "sum of that status's findings
-- on this attempt" shape) so every value in `ComponentFindingStatuses.All` now lands
-- in exactly one `component_results` count column: `Failed` -> a CAT column,
-- `Passed`/`NotApplicable`/`NotReviewed`/`Skipped`/`ExecutionError` -> their own
-- named column. `ComponentResultRecord.ExecutionErrorCount` (Waypoint.Core.Scans)
-- computes it the same way the existing five computed properties do.
--
-- No new runner grant required: migration 0063 already grants
-- `waypoint_compliance_runner` INSERT (and SELECT) on the whole `component_results`
-- table -- a Postgres table-level GRANT covers every column, including one added
-- after the GRANT ran (same reasoning migration 0079 already recorded for
-- `inventory_items`).
--
-- Idempotent by construction (ADD COLUMN IF NOT EXISTS + DROP/re-ADD CHECK), matching
-- every prior migration in this directory.
ALTER TABLE component_results ADD COLUMN IF NOT EXISTS execution_error_count INTEGER NOT NULL DEFAULT 0;

ALTER TABLE component_results DROP CONSTRAINT IF EXISTS component_results_counts_non_negative_check;
ALTER TABLE component_results ADD CONSTRAINT component_results_counts_non_negative_check CHECK (
    cat_i_open >= 0 AND cat_ii_open >= 0 AND cat_iii_open >= 0
    AND passed_count >= 0 AND not_applicable_count >= 0
    AND not_reviewed_count >= 0 AND skipped_count >= 0
    AND execution_error_count >= 0
);
