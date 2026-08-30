-- Issue #1140 (epic #1177, remainder 1 of PR #1139's own body): a component whose
-- attempt finished (the HDF parsed successfully -- `ComponentResultStatuses.Completed`)
-- but evaluated ZERO controls (all findings `not_reviewed`/`skipped`, a mix of those
-- with `execution_error`, or no findings at all) was, before this migration,
-- indistinguishable from a genuinely evaluated, healthy `completed` row by `status`
-- alone -- the false-clean gap was visible only via the ADJACENT
-- `evaluated_zero_component_count`/`evaluated_zero_controls` derived read-time signal
-- (migration 0080, issue #1132/#1144), never on the row's own `status` column. A caller
-- reading `component_results.status` in isolation (rather than joining the rollup's
-- derived count) still saw a plain "completed".
--
-- Adds `completed_zero_controls` to the closed `component_results_status_check`
-- vocabulary -- the same "did the attempt finish, not what it evaluated" grain as
-- `completed`/`execution_error`/`skipped`. `execution_error` already distinguishes
-- itself (a malformed/unreadable HDF, or a component that never executed at all); the
-- ambiguity only ever existed inside the `completed` bucket, so this is the one new
-- value that closes it: `ComponentResultRecordingService.RecordCompletedAsync` now
-- writes `completed_zero_controls` instead of `completed` when the parsed findings
-- satisfy the EXACT same zero-verdict predicate
-- `ComponentResultRepository.GetRunRollupAsync`'s `evaluated_zero_component_count`
-- FILTER already uses (zero passed/open findings, and at least one `not_reviewed`,
-- `skipped`, or `execution_error` finding, or none at all) -- one predicate, defined
-- once in `ComponentResultRecord.EvaluatedZeroControls` (Waypoint.Core.Scans), read at
-- WRITE time here and re-used at READ time by the rollup unchanged. A genuinely
-- all-`not_applicable` component is deliberately NOT reclassified: N/A is a determinate
-- outcome, not a failure to evaluate, matching the rollup's own carve-out.
--
-- No new runner grant for the STATUS WIDENING itself: migration 0063 already grants
-- `waypoint_compliance_runner` INSERT/SELECT on the whole `component_results` table,
-- which covers every value a CHECK-constrained column may hold, not only the ones
-- enumerated when the grant ran (same reasoning migrations 0079/0080 already
-- recorded for a new column; this migration adds a new value, not a new column, so
-- the point applies even more directly).
--
-- NO NEW GRANTS (issue #1303, review of PR #1300): an earlier revision of this
-- migration granted `SELECT ON scan_plans` to both runner roles and `SELECT ON
-- component_results` to `waypoint_download_runner`, on the stated grounds that
-- `JobDispatcherHostedService` runs `JobQueueRepository.RunSummaryProjectionSql`.
-- It does not -- it calls only `AbortRunAsync`/`PauseRunAsync`/`ResumeRunAsync`. The
-- projection is reached solely through `GetRunAsync`/`ListRunsAsync`/`ListRunHistoryAsync`,
-- and every production caller of those is API-side (`RunsController`,
-- `DashboardAggregateService`, `RunPurgeService`, `RunHistoryDeletionService`,
-- `RunRetentionHoldService`); nothing under `Waypoint.Runner`/`Waypoint.ComplianceRunner`/
-- `Waypoint.DownloadRunner` calls them. Granting the download runner read access to all
-- compliance evidence purely to keep a drift test green is a least-privilege regression,
-- so the grants are gone and `RunnerRoleGrantDriftTests` no longer runs the run
-- projection under a runner role.
--
-- BACKFILL: `component_results` rows are immutable (migration 0066's append-only
-- UPDATE trigger), so a historical `completed` row that in fact evaluated zero
-- controls would otherwise keep reading as the honest-looking `completed` forever.
-- The correct value is derivable from the row's own immutable counts (the same six
-- count columns the rollup already sums), so it is backfilled rather than left
-- stale -- the same "recompute from immutable evidence, never fabricate" discipline
-- migration 0080's `execution_error_count` backfill used. Only genuine `completed`
-- rows are touched; `execution_error`/`skipped` rows are untouched (the ambiguity
-- never existed there). This is migration 0080's own sanctioned exception to the
-- append-only trigger, taken the same narrowest way: disabled and re-enabled around
-- this one statement, inside this migration's own transaction (NpgsqlSchemaMigrator
-- runs each migration file in one transaction; DISABLE TRIGGER takes an ACCESS
-- EXCLUSIVE lock held to commit, so no other session can write through the gap, and a
-- failure rolls the whole thing back with the trigger intact). The row's CONTENT is
-- unchanged -- only the status label is corrected to match what the row's own
-- immutable findings already imply.
--
-- ORDERING (review of PR #1300, finding F1): the CHECK widening MUST precede the
-- backfill. Postgres CHECK constraints are never deferrable, so an `UPDATE ... SET
-- status = 'completed_zero_controls'` run while migration 0063's narrower constraint
-- is still in force is rejected outright, aborting this migration's single transaction
-- and blocking application start -- and it fires only where at least one historical
-- zero-verdict `completed` row exists, i.e. exactly the population the backfill was
-- written for, which is never a freshly-created CI/test database. Pinned by
-- `SchemaMigrationTests.Migration0081_PreExistingZeroVerdictCompletedRow_IsBackfilledAfterTheCheckWidens`,
-- which restores migration 0063's narrow constraint, seeds such a row, and then runs
-- this migration's own text.
--
-- Idempotent by construction (DROP/re-ADD CHECK + a backfill that only touches rows
-- whose status differs from the derived value), matching every prior migration in
-- this directory.
ALTER TABLE component_results DROP CONSTRAINT IF EXISTS component_results_status_check;
ALTER TABLE component_results ADD CONSTRAINT component_results_status_check
    CHECK (status IN ('completed', 'execution_error', 'skipped', 'completed_zero_controls'));

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_component_results_block_update') THEN
        ALTER TABLE component_results DISABLE TRIGGER trg_component_results_block_update;
    END IF;
END $$;

UPDATE component_results
SET status = 'completed_zero_controls'
WHERE status = 'completed'
    AND passed_count + cat_i_open + cat_ii_open + cat_iii_open = 0
    AND (not_reviewed_count > 0 OR skipped_count > 0 OR execution_error_count > 0 OR not_applicable_count = 0);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_component_results_block_update') THEN
        ALTER TABLE component_results ENABLE TRIGGER trg_component_results_block_update;
    END IF;
END $$;
