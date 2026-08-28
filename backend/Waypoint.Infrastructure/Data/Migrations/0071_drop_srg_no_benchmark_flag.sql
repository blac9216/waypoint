-- Issue #1002 (epic #726; owner-decided benchmark-mapping lifecycle, 2026-08-28):
-- removes migration 0052's admin-stated `is_srg_no_benchmark` flag from
-- benchmark_component_mappings. SRG participation in benchmark mapping is now a
-- DERIVED read-state computed from the catalog's closed content-kind vocabulary
-- (a component bound to an `srg`-kind catalog_content_release never has a benchmark
-- concept at all -- ADR-0022 "An SRG has no XCCDF or CKL") -- never stored, never
-- admin-settable, never auto-suggested. Waypoint.Api's BenchmarksController stops
-- accepting the field entirely (see BenchmarkMappingOverrideRequest); the API layer,
-- not schema, is where a caller still sending it gets a pointed rejection (this
-- repo's `validation_failed` idiom -- BenchmarksController.SetMapping already
-- follows it for every other removed/invalid shape on this same endpoint).
--
-- Slot 0071 (0070 reserved for issue #998's catalog seed/matcher work per epic #726
-- coordination) -- verified free against both the migrations directory and open PRs
-- at this migration's own commit time.
--
-- Historical-retention protection (matching this tree's own migration 0052 idiom,
-- "every FK ... ON DELETE RESTRICT so a ... mapping ... can never be silently
-- orphaned"): dropping a column is unlike dropping a row, so the least-lossy shape
-- for a superseded row that had is_srg_no_benchmark = true is to fold that fact into
-- the row's own free-text `reason` column BEFORE the column disappears, rather than
-- letting the drop silently discard it. A row with an already-informative reason (the
-- overwhelming common case -- migration 0052's own convention is that every
-- SetMappingAsync caller supplies a `reason`) keeps that text unchanged; only a row
-- with a null/blank reason gets a synthesized note, so history/audit review of an old
-- mapping decision still explains why it was recorded the way it was, without
-- resurrecting a column this migration intentionally removes.
--
-- Re-apply safety (this test suite's raw-SQL re-apply idiom, e.g. migration 0069's
-- own "IF EXISTS ... makes this statement itself idempotent" note): the UPDATE above
-- can only run while the column still exists, so it is guarded by an explicit
-- information_schema check inside a DO block -- a second raw re-run, after the column
-- is already gone, skips the backfill entirely instead of erroring with an undefined
-- column.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'benchmark_component_mappings' AND column_name = 'is_srg_no_benchmark'
    ) THEN
        UPDATE benchmark_component_mappings
        SET reason = COALESCE(NULLIF(TRIM(reason), ''), '')
            || CASE WHEN COALESCE(NULLIF(TRIM(reason), ''), '') = '' THEN '' ELSE ' ' END
            || '[historical: recorded is_srg_no_benchmark=true before issue #1002 made SRG mapping status a derived read-state]'
        WHERE is_srg_no_benchmark;
    END IF;
END $$;

ALTER TABLE benchmark_component_mappings
    DROP CONSTRAINT IF EXISTS benchmark_component_mappings_srg_exclusive_check;

ALTER TABLE benchmark_component_mappings
    DROP COLUMN IF EXISTS is_srg_no_benchmark;
