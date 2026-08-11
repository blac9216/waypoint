-- Issue #435 (ADR-0014): the runner claim SQL now filters on a caller-supplied
-- job-type allowlist (WHERE state = 'queued' AND job_type = ANY($n)), so the
-- claim's supporting index must lead with job_type or that predicate falls back
-- to scanning every claimable row and filtering in memory. job_type replaces
-- priority as the leading column; priority, created_at stay in the same order
-- so the per-job-type slice this predicate selects is still index-ordered for
-- the claim's `ORDER BY priority, created_at` clause.
DROP INDEX IF EXISTS idx_jobs_queue_claim;
CREATE INDEX IF NOT EXISTS idx_jobs_queue_claim ON jobs (job_type, priority, created_at) WHERE state = 'queued';
