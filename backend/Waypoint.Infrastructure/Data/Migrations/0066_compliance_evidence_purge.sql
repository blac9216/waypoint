-- Issue #745 remainder (epic #726, ADR-0019 decision 5 / docs/domain-model.md's
-- "operational vs. domain retention ownership" classification table): wires
-- migration 0062's upload_attempts and migration 0063's component_results/
-- component_result_findings/component_result_artifacts into RunPurgeService's
-- existing database purge phase.
--
-- ADR-0019 decision 4 names "Compliance Results owns scan/remediation findings,
-- attestations, waivers, and artifacts"; decision 5 requires "a domain purge
-- enumerates its owned projections and artifacts". docs/domain-model.md's
-- retention table already lists "findings ... CKL/HDF artifact files" as
-- Compliance Results domain output retained under RunPurgeService, alongside
-- attestation_snapshots -- but PR #952/#961 shipped the tables one wave before
-- this purge wiring landed, and PR #961's body called out the resulting gap
-- verbatim: "purge currently RESTRICTs". Concretely: component_results.run_id/
-- job_id/scan_plan_item_id are all `ON DELETE RESTRICT` (matching every other FK
-- onto jobs/scan_plan_items in this schema, per 0063's own header) and nothing
-- ever deletes a component_results/component_result_findings/
-- component_result_artifacts/upload_attempts row for a purged run -- these
-- immutable evidence rows would silently outlive `runs.purged_at`, which is
-- exactly the "readers never observe retained rows pointing to missing graph
-- members" (docs/domain-model.md's planned ComplianceEvidenceGraph section)
-- failure mode purge exists to prevent, just inverted: here the run is marked
-- purged while its findings/artifacts/upload-receipts remain, contradicting
-- "domain purge enumerates its owned projections".
--
-- Fix: extend the SAME append-only-with-narrow-carve-out pattern migration 0021/
-- 0042 already established for attestation_snapshots to these four tables, and
-- extend RunPurgeService.RunDatabasePhaseAsync (API-side, same transaction as the
-- existing attestation_snapshots/run_secrets/schedules cleanup) to delete them.
-- Purge remains the ONLY writer ever permitted to remove a row from any of these
-- tables -- live recording (ComponentResultRepository/ScanUploadCoordinator) is
-- completely unaffected, and every other DELETE/UPDATE attempt still raises
-- exactly as before.
--
-- Ordering (child-before-parent, required by the `ON DELETE RESTRICT` FKs this
-- migration deliberately does NOT relax -- 0063's header explains why a result
-- row must never be silently orphaned by an unrelated cascade): the repository
-- deletes component_result_findings and component_result_artifacts (by their
-- owning component_results.id) before component_results itself (by run_id), all
-- inside one transaction with one `waypoint.purge_run_id` GUC set.
--
-- upload_attempts has no run_id column at all (0062's header: deliberately not
-- FK'd to jobs, so a jobs retention sweep never needs to cascade through it) --
-- purge resolves the affected job ids from the SAME `IJobControlRepository.
-- GetJobsForRunAsync` call RunPurgeService already makes to find scan job ids for
-- the artifact-deletion phase, and deletes upload_attempts rows by job_id IN
-- (...) under its own carve-out GUC (`waypoint.purge_job_ids`, a comma-separated
-- uuid list -- a single GUC only ever holds one scalar, and a run's scan jobs are
-- a bounded, already-computed set at the point purge runs, not an arbitrary
-- growing list).
CREATE OR REPLACE FUNCTION component_results_block_mutation()
RETURNS TRIGGER AS $$
DECLARE
    purge_run_id TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        purge_run_id := current_setting('waypoint.purge_run_id', true);
        IF purge_run_id IS NOT NULL AND purge_run_id <> '' AND OLD.run_id = purge_run_id::uuid THEN
            RETURN OLD;
        END IF;
    END IF;

    RAISE EXCEPTION 'component_results is append-only: % is not permitted (id=%)',
        TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_component_results_block_update
    BEFORE UPDATE ON component_results
    FOR EACH ROW EXECUTE FUNCTION component_results_block_mutation();

CREATE OR REPLACE TRIGGER trg_component_results_block_delete
    BEFORE DELETE ON component_results
    FOR EACH ROW EXECUTE FUNCTION component_results_block_mutation();

-- component_result_findings/component_result_artifacts have no run_id column of
-- their own (they hang off component_result_id) -- the carve-out resolves the
-- owning component_results row's run_id via the same session-local GUC, joined
-- through the FK the repository is about to delete anyway (child rows are always
-- deleted first, immediately before their parent, inside the same purge
-- transaction -- see RunPurgeService).
CREATE OR REPLACE FUNCTION component_result_children_block_mutation()
RETURNS TRIGGER AS $$
DECLARE
    purge_run_id TEXT;
    owning_run_id UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN
        purge_run_id := current_setting('waypoint.purge_run_id', true);
        IF purge_run_id IS NOT NULL AND purge_run_id <> '' THEN
            SELECT run_id INTO owning_run_id FROM component_results WHERE id = OLD.component_result_id;
            IF owning_run_id IS NOT NULL AND owning_run_id = purge_run_id::uuid THEN
                RETURN OLD;
            END IF;
        END IF;
    END IF;

    RAISE EXCEPTION '% is append-only: % is not permitted (id=%)',
        TG_TABLE_NAME, TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_component_result_findings_block_update
    BEFORE UPDATE ON component_result_findings
    FOR EACH ROW EXECUTE FUNCTION component_result_children_block_mutation();

CREATE OR REPLACE TRIGGER trg_component_result_findings_block_delete
    BEFORE DELETE ON component_result_findings
    FOR EACH ROW EXECUTE FUNCTION component_result_children_block_mutation();

CREATE OR REPLACE TRIGGER trg_component_result_artifacts_block_update
    BEFORE UPDATE ON component_result_artifacts
    FOR EACH ROW EXECUTE FUNCTION component_result_children_block_mutation();

CREATE OR REPLACE TRIGGER trg_component_result_artifacts_block_delete
    BEFORE DELETE ON component_result_artifacts
    FOR EACH ROW EXECUTE FUNCTION component_result_children_block_mutation();

-- upload_attempts's carve-out keys off job_id (no run_id column, see header) --
-- `waypoint.purge_job_ids` holds a comma-separated uuid list set once per purge
-- database phase; membership is checked with a plain string search over that
-- bounded, already-computed list (never attacker-controlled -- the GUC is set
-- API-side from the run's own resolved scan job ids, not from request input).
CREATE OR REPLACE FUNCTION upload_attempts_block_mutation()
RETURNS TRIGGER AS $$
DECLARE
    purge_job_ids TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        purge_job_ids := current_setting('waypoint.purge_job_ids', true);
        IF purge_job_ids IS NOT NULL AND purge_job_ids <> ''
            AND (',' || purge_job_ids || ',') LIKE ('%,' || OLD.job_id::text || ',%')
        THEN
            RETURN OLD;
        END IF;
    END IF;

    RAISE EXCEPTION 'upload_attempts is append-only: % is not permitted (id=%)',
        TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_upload_attempts_block_update
    BEFORE UPDATE ON upload_attempts
    FOR EACH ROW EXECUTE FUNCTION upload_attempts_block_mutation();

CREATE OR REPLACE TRIGGER trg_upload_attempts_block_delete
    BEFORE DELETE ON upload_attempts
    FOR EACH ROW EXECUTE FUNCTION upload_attempts_block_mutation();

-- No new runner grants -- deletion is exclusively API-side (RunPurgeService,
-- same owner-privileged connection every other purge step already uses); the
-- compliance-runner's existing SELECT/INSERT grants on all four tables
-- (migrations 0062/0063) are untouched and still cannot perform any DELETE, with
-- or without the GUC (the runner never sets `waypoint.purge_run_id`/
-- `waypoint.purge_job_ids`, and even if it did, only the API owner role reaches
-- this migration's triggers via RunPurgeService's connection in practice --
-- RunnerRoleGrantDriftTests already pins UPDATE/DELETE as 42501 for the runner
-- role on 0062/0063's tables independent of this carve-out).
