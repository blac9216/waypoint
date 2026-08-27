-- Issue #744 (epic #726 Wave 4): persists a retryable, per-attempt audit trail for a
-- scan job's STIG Manager CKL upload, extending #311/migration 0018's single
-- jobs.upload_status/upload_detail pair (the CURRENT-outcome-only summary) with the
-- full attempt history epic #726 SS6 requires ("persist attempts/receipts/status;
-- retry from retained artifacts without rescanning"). jobs.upload_status/upload_detail
-- are left exactly as they are -- this table is additive, not a replacement: it never
-- gates ScanJobHandler's "upload failure must never fail the scan run" contract
-- (issue #311 AC, unchanged), it only makes each individual attempt (first pass or a
-- later stigman-upload-retry call) independently auditable instead of overwritten by
-- the next one.
--
-- One row per attempt, append-only (never updated or deleted in place) -- an attempt
-- is a historical fact, not mutable state. `job_id` is NOT a foreign key to `jobs`:
-- this mirrors migration 0044's job_credential_bindings convention of not
-- foreign-keying job-scoped audit/history rows back to the (potentially pruned) jobs
-- table, so an eventual jobs retention sweep never needs to cascade through this
-- table first. `attempt_number` is 1-based and monotonic per job, assigned by the
-- application (COUNT(*) + 1 under the same connection as the INSERT -- benign
-- races only produce a harmless gap or duplicate ordinal under concurrent retries,
-- never a lost row, since there is no uniqueness constraint on the pair forcing a
-- serialization failure).
CREATE TABLE IF NOT EXISTS upload_attempts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id UUID NOT NULL,
    attempt_number INTEGER NOT NULL,
    endpoint TEXT NULL,
    collection TEXT NULL,
    status TEXT NOT NULL,
    error_detail TEXT NULL,
    attempted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT upload_attempts_attempt_number_check CHECK (attempt_number >= 1),
    -- Mirrors JobUploadStatuses (Waypoint.Core.Jobs) -- the same closed vocabulary
    -- jobs.upload_status already constrains (migration 0018), duplicated here rather
    -- than referenced because this table intentionally has no FK back to jobs.
    CONSTRAINT upload_attempts_status_check CHECK (status IN ('pending', 'uploaded', 'conflict', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_upload_attempts_job_id ON upload_attempts (job_id, attempt_number);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

-- The compliance-runner is the only process that ever records an attempt (both the
-- first convert-stage upload and JobsController's stigman-upload-retry route call
-- through ScanUploadCoordinator, which runs in-process in the runner) -- and it only
-- ever appends, never reads its own history back (the API's own future read surface,
-- if any, is a stated remainder; the runner needs SELECT here only to compute the
-- next attempt_number under its own connection).
GRANT SELECT, INSERT ON upload_attempts TO waypoint_compliance_runner;
