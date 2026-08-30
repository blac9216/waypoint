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
-- the point applies even more directly). Item 2's own new grants are below.
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
-- Idempotent by construction (DROP/re-ADD CHECK + a backfill that only touches rows
-- whose status differs from the derived value), matching every prior migration in
-- this directory.
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

ALTER TABLE component_results DROP CONSTRAINT IF EXISTS component_results_status_check;
ALTER TABLE component_results ADD CONSTRAINT component_results_status_check
    CHECK (status IN ('completed', 'execution_error', 'skipped', 'completed_zero_controls'));

-- Issue #1140 item 2: `JobQueueRepository.RunSummaryProjectionSql` (shared by
-- `GetRunAsync`/`ListRunsAsync`/`ListRunHistoryAsync`) now bulk-computes
-- `coverage_incomplete` by LEFT JOINing `scan_plans` and a `component_results`
-- aggregate -- and that ONE query is also what `JobDispatcherHostedService` and every
-- other runner-side caller runs under EITHER runner role's restricted DB grants (proven
-- by `RunnerRoleGrantDriftTests.RunnerRole_ReadsCredentialAttributionSnapshotColumns_WithoutPermissionDenied`,
-- which calls this exact method as both roles). `scan_plans` had never been granted to
-- ANY runner role before this (migration 0057 only granted its write path, API-side);
-- `component_results` was granted to `waypoint_compliance_runner` by migration 0063 but
-- never to `waypoint_download_runner`, which never touched compliance evidence before
-- this query started reading it in every run-read call. Read-only, table-level (a
-- table-level GRANT already covers a column added later, per every prior migration's
-- own reasoning in this directory).
GRANT SELECT ON scan_plans TO waypoint_compliance_runner, waypoint_download_runner;
GRANT SELECT ON component_results TO waypoint_download_runner;
