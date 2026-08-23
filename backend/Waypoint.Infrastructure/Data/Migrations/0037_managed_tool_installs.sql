-- Issue #39 (epic #558), backend part 1: managed-tool install history. ADR-0015
-- decision 3: the account-gated vcf-download-tool is never bundled -- an operator
-- installs it through the appliance, and the installed binary lives in persistent
-- managed state (the ManagedTool:ToolStatePath volume, ManagedToolPresenceChecker)
-- rather than an immutable runner image.
--
-- NOTE ON NUMBERING: this migration was authored while #569 (capacity lease pool)
-- was in flight and expected to claim 0036. If #569 lands first and also claims
-- 0037, whoever merges second must renumber (this file's header, plus
-- SchemaMigrationTests.ExpectedMigrationCount) before merging.
--
-- managed_tool_installs ---------------------------------------------------------------
-- Append-only ledger of every install ATTEMPT against the managed-tool volume --
-- including rejected ones (bad signature). This is deliberately not a single
-- "current tool" row: issue #39's acceptance criteria require "install history
-- (who/when/source-path/version/outcome incl. rejected attempts)", and the currently
-- active install is derivable as the most recent row with outcome = 'installed'
-- (ManagedToolInstallRepository.GetCurrentAsync) without a second table to keep in
-- sync. Rows are never updated or deleted (0020's append-only trigger convention
-- applies here too -- see the trigger below), matching audit_log/job_events.
CREATE TABLE IF NOT EXISTS managed_tool_installs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source TEXT NOT NULL,
    source_path TEXT NOT NULL,
    version TEXT NULL,
    sha256 TEXT NULL,
    outcome TEXT NOT NULL,
    rejected_reason TEXT NULL,
    initiated_by TEXT NOT NULL,
    job_id UUID NULL REFERENCES jobs(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT managed_tool_installs_source_check CHECK (source IN ('local-repository', 'depot', 'upload')),
    CONSTRAINT managed_tool_installs_outcome_check CHECK (outcome IN ('installed', 'rejected', 'failed'))
);

COMMENT ON TABLE managed_tool_installs IS
    'Issue #39: append-only ledger of every vcf-download-tool install attempt (local repository, depot fetch, or manual upload), including signature-rejected attempts. The active install is the newest outcome=installed row.';
COMMENT ON COLUMN managed_tool_installs.source IS
    'Which of the three ADR-0015 install paths produced this attempt.';
COMMENT ON COLUMN managed_tool_installs.source_path IS
    'Where the candidate artifact was read from: the indexed local-repository path, the depot URL, or the staged upload path. Never a credential value.';
COMMENT ON COLUMN managed_tool_installs.version IS
    'Tool version parsed from the candidate artifact, when the install path can determine one. NULL when unknown (e.g. a rejected attempt never got far enough to parse it).';
COMMENT ON COLUMN managed_tool_installs.sha256 IS
    'SHA-256 of the candidate artifact bytes, recorded regardless of outcome so a rejected attempt is still forensically identifiable.';
COMMENT ON COLUMN managed_tool_installs.rejected_reason IS
    'Set only when outcome = rejected -- the signature-verification failure reason. NULL for installed/failed outcomes.';
COMMENT ON COLUMN managed_tool_installs.initiated_by IS
    'The operator identity (or "system") that triggered this attempt -- the "who" in issue #39''s install-history acceptance criterion.';
COMMENT ON COLUMN managed_tool_installs.job_id IS
    'The download-runner tool-install job that performed this attempt, when the attempt was queued as a job. NULL for a synchronous/inline attempt path, if any.';

CREATE INDEX IF NOT EXISTS idx_managed_tool_installs_created_at ON managed_tool_installs (created_at DESC);

-- Append-only: no UPDATE or DELETE, ever (same discipline as audit_log/job_events,
-- migration 0020's job_events_block_mutation).
CREATE OR REPLACE FUNCTION managed_tool_installs_block_mutation() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'managed_tool_installs is append-only: % is not permitted', TG_OP;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_managed_tool_installs_block_update
    BEFORE UPDATE ON managed_tool_installs
    FOR EACH ROW EXECUTE FUNCTION managed_tool_installs_block_mutation();

CREATE OR REPLACE TRIGGER trg_managed_tool_installs_block_delete
    BEFORE DELETE ON managed_tool_installs
    FOR EACH ROW EXECUTE FUNCTION managed_tool_installs_block_mutation();

-- jobs.job_type / runs.run_type: widen the closed sets (0001, widened by 0019) with
-- the new tool-install job type this migration's handler registers under
-- JobCapabilities.Download.
ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_job_type_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_job_type_check CHECK (job_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test',
    'tool-install'
));

ALTER TABLE runs DROP CONSTRAINT IF EXISTS runs_run_type_check;
ALTER TABLE runs ADD CONSTRAINT runs_run_type_check CHECK (run_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test',
    'tool-install'
));

-- Runner grants -------------------------------------------------------------------
-- download-runner only (ADR-0013 SS2: the managed-tool volume is a download-domain
-- concern -- ManagedToolOptions/ManagedToolPresenceChecker are already
-- Waypoint.Core.Downloads types). INSERT-only, matching audit_log/job_events'
-- append-only grant shape (migration 0025) -- the API never writes this table
-- directly (no controller INSERT path), only the tool-install job handler does, and
-- SELECT is needed too because the handler and GET /downloads/tool/installs both
-- read it back (the API connects with the owner role for reads exactly like every
-- other controller; only the runner's own write needs the narrower grant here).
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT ON managed_tool_installs TO waypoint_download_runner;
