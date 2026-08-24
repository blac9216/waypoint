-- Issue #687 (epic #667): the connected vendor catalog-pull job type and its own
-- singleton progress/outcome tracking table.
--
-- catalog_pull_state is a SINGLETON, mirroring appliance_state (migration 0001) and
-- depot_enrollment (migration 0048): there is exactly one connected download-catalog
-- pull lifecycle per appliance, not a per-run or per-credential one.
--
-- Deliberately separate from depot_artifacts (the indexed catalog rows themselves,
-- migration 0001) and from depot_enrollment (the tool/Activation Code readiness gate,
-- migration 0048):
--   - last_success_at / last_success_item_count give the frontend an honest "last
--     successful REMOTE refresh" fact independent of depot_artifacts.indexed_at,
--     which also advances on a purely local, credential-free re-index
--     (CatalogIndexJobHandler, issue #690 AC). A connected pull's success is a
--     distinct, stronger claim -- "the authenticated vendor catalog was reached and
--     its metadata authenticated" -- that a local filesystem walk can never make.
--   - last_attempt_at / last_outcome / last_failure_reason report the most recent
--     pull's result even when it failed, so the UI can show "last attempt failed:
--     <reason>" without that failure clobbering the last known-good
--     last_success_at/last_success_item_count (prior-good preservation, issue #687
--     AC: "prior good catalog remains on failure" applies to the reported state too,
--     not only the on-disk metadata).
--   - A zero-item success is recorded exactly like any other success
--     (last_success_item_count = 0 is a legitimate value) -- issue #687 AC: "a
--     zero-item result is not reported as a successful remote refresh unless the
--     authenticated vendor catalog is genuinely empty" is enforced by the job
--     handler only ever calling the success path after catalog authentication, not
--     by this table rejecting zero.
CREATE TABLE IF NOT EXISTS catalog_pull_state (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    last_attempt_at TIMESTAMPTZ NULL,
    last_outcome TEXT NULL,
    last_failure_reason TEXT NULL,
    last_success_at TIMESTAMPTZ NULL,
    last_success_item_count INTEGER NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_pull_state_singleton_check CHECK (id = 1),
    CONSTRAINT catalog_pull_state_outcome_check CHECK (last_outcome IS NULL OR last_outcome IN (
        'succeeded', 'failed', 'auth_failed'
    ))
);

INSERT INTO catalog_pull_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_catalog_pull_state_updated_at
    BEFORE UPDATE ON catalog_pull_state
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- 'catalog-pull' is a new Simple-shape job type (ADR-0008/0015 pattern, mirrors
-- 'catalog-index' and 'depot-enrollment'): the download-runner invokes the installed
-- vcf-download-tool's noninteractive 'metadata download' using the stored Activation
-- Code, authenticates and atomically promotes the result, then indexes it. Runs
-- through the ordinary jobs/job_events pipeline, distinct from the credential-free
-- local 'catalog-index' re-index (issue #690 AC).
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull'
    ));

-- Download-runner grant: the catalog-pull handler reads/writes catalog_pull_state
-- (its own dedicated table), resolves the stored Activation Code credential the same
-- way ManagedToolInstallJobHandler's depot-fetch path and DepotEnrollmentJobHandler's
-- validate-code path already do (migration 0025/0039 grants cover that), and
-- upserts depot_artifacts (migration 0025 already grants SELECT/INSERT/UPDATE there).
-- No additional credentials/depot_artifacts grant is needed here.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE ON catalog_pull_state TO waypoint_download_runner;
