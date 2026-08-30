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
-- BACKFILL (issue #1144 review round 1, finding 1): `component_results` rows are
-- immutable (migration 0063), so a bare `NOT NULL DEFAULT 0` would leave every run
-- already recorded asserting "zero execution errors" -- a FABRICATED clean value, and
-- exactly the bug this issue reports, now stated with false confidence. Unlike
-- migration 0079's `instance_uuid` (genuinely unreconstructable, hence `TEXT NULL`),
-- the truth here IS derivable: `component_result_findings` (0063) already carries the
-- per-control `status` from the same closed vocabulary, keyed by `component_result_id`,
-- and those rows are immutable too. So the count is recomputed from the findings
-- themselves -- the same aggregate `ComponentResultRecord.ExecutionErrorCount` computes
-- in C# -- rather than defaulted. A row whose findings contain no `execution_error`
-- legitimately backfills to 0; a row whose controls all errored comes back with the
-- real number, and AC1 ("a component whose controls all errored is visible in the
-- rollup counts, not silently all-zero") holds for history, not only for future runs.
--
-- The backfill is the ONE sanctioned exception to migration 0066's append-only UPDATE
-- trigger on `component_results`, and it is taken the narrowest way available: the
-- trigger is disabled and re-enabled around this single statement, inside this
-- migration's own transaction (NpgsqlSchemaMigrator runs each migration file in one
-- transaction, and DISABLE TRIGGER takes an ACCESS EXCLUSIVE lock held to commit -- so
-- no other session can write through the gap, and a failure rolls the whole thing back
-- with the trigger intact). This is deliberately NOT a permanent carve-out in the
-- trigger function like purge's `waypoint.purge_run_id` GUC: no application code may
-- ever UPDATE this table, only this one-time schema migration, so the exception must not
-- outlive it. The row's CONTENT is unchanged either way -- the backfill writes the value
-- the row's own immutable findings already imply, it does not revise history.
--
-- Idempotent by construction (ADD COLUMN IF NOT EXISTS + DROP/re-ADD CHECK + a backfill
-- that recomputes the same derived value from immutable rows every time it runs, and
-- touches only rows whose stored value differs), matching every prior migration in this
-- directory.
ALTER TABLE component_results ADD COLUMN IF NOT EXISTS execution_error_count INTEGER NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_component_results_block_update') THEN
        ALTER TABLE component_results DISABLE TRIGGER trg_component_results_block_update;
    END IF;
END $$;

UPDATE component_results
SET execution_error_count = derived.execution_error_count
FROM (
    SELECT r.id AS component_result_id,
        (SELECT count(*) FROM component_result_findings f
         WHERE f.component_result_id = r.id AND f.status = 'execution_error') AS execution_error_count
    FROM component_results r
) AS derived
WHERE component_results.id = derived.component_result_id
    AND component_results.execution_error_count IS DISTINCT FROM derived.execution_error_count;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_component_results_block_update') THEN
        ALTER TABLE component_results ENABLE TRIGGER trg_component_results_block_update;
    END IF;
END $$;

ALTER TABLE component_results DROP CONSTRAINT IF EXISTS component_results_counts_non_negative_check;
ALTER TABLE component_results ADD CONSTRAINT component_results_counts_non_negative_check CHECK (
    cat_i_open >= 0 AND cat_ii_open >= 0 AND cat_iii_open >= 0
    AND passed_count >= 0 AND not_applicable_count >= 0
    AND not_reviewed_count >= 0 AND skipped_count >= 0
    AND execution_error_count >= 0
);
