-- Issue #245 (epic #13): the real `/credentials/{id}/test` connectivity job.
-- Adds `credential-test` to the closed jobs_job_type set (0001's
-- jobs_job_type_check) -- CredentialsController.Test now dispatches this job
-- type instead of running its old synchronous decrypt-only check.
-- JobShapes.ForJobType/ForJob both default any type outside 'scan' to
-- JobShape.Simple (queued -> running -> done|failed), which is exactly this
-- job's shape -- no shape-table change needed.
ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_job_type_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_job_type_check CHECK (job_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test'
));

-- runs.run_type carries the same closed set (0001's runs_run_type_check) --
-- CredentialsController.Test creates its run with run_type = 'credential-test'
-- exactly like DiscoveryController.Discover does for 'discover'.
ALTER TABLE runs DROP CONSTRAINT IF EXISTS runs_run_type_check;
ALTER TABLE runs ADD CONSTRAINT runs_run_type_check CHECK (run_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test'
));
