-- Issue #1436 (epic #1182 "Subscriptions, retention & scheduling", split from
-- design record #1047; blocked by #1406/migration 0107; slot 0127 -- the next free
-- slot named on epic #1182's 2026-08-30 decision thread at the time this migration
-- was authored): reserves the 'retention-sweep' job/run type this issue's
-- RetentionSweepJobHandler claims, and grants waypoint_download_runner exactly the
-- two operations it performs against 0107's tables -- no more.
--
-- This is the grant migration 0107's own header promised: "#1436, as filed, is a
-- genuine waypoint_download_runner-claimed job ... it must ship its own GRANT
-- migration when it lands (0100/#1484 precedent)." The scope is deliberately
-- narrow: SELECT + UPDATE on download_retained_content_state (the sweep reads a
-- row's current state under FOR UPDATE and then transitions it -- see
-- RetainedContentStateRepository.TransitionAsync/LoadForUpdateAsync -- it never
-- inserts a new row itself; RetentionSweepService's own doc comment explains why
-- EnsureTrackedAsync, an INSERT, is never called from the runner-executed sweep),
-- and SELECT only on download_retention_policies (the sweep resolves a policy's
-- grace_period_days to decide whether a grace-state row is due; it never writes a
-- policy). Neither INSERT nor DELETE is granted on either table -- proven negative
-- by RetentionSweepRunnerRoleGrantTests, this repo's #556 convention (assert the
-- grant exists AND assert what must still fail).
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep'
    ));

GRANT SELECT, UPDATE ON download_retained_content_state TO waypoint_download_runner;
GRANT SELECT ON download_retention_policies TO waypoint_download_runner;
