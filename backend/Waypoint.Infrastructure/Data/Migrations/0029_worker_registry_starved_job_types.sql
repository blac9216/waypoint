-- Issue #467: extends worker_registry (0026) with a per-worker snapshot of job types
-- currently denied resource admission (Waypoint.Runner.Resources.ResourceAdmissionController
-- .StarvedJobTypes), so GET /system can surface "these job types cannot currently be
-- admitted on this runner" without reaching into a runner container's filesystem or logs.
-- Mirrors job_types' own "small, closed, infrequently-changing list, no cross-worker join
-- ever needed" reasoning (0026) -- stored as a JSONB array of {job_type, permanent} pairs
-- rather than a normalized table.
--
-- NOT NULL DEFAULT '[]'::jsonb: existing rows (and any heartbeat writer not yet updated
-- to pass this column) read back as "nothing starved" rather than NULL, matching
-- job_types' own non-nullable shape.
ALTER TABLE worker_registry
    ADD COLUMN IF NOT EXISTS starved_job_types JSONB NOT NULL DEFAULT '[]'::jsonb;

COMMENT ON COLUMN worker_registry.starved_job_types IS
    'Issue #467: JSONB array of {"JobType": "...", "Permanent": bool} entries (default '
    'System.Text.Json PascalCase, matching job_types'' own writer) -- job types this '
    'worker is currently denying resource admission to, distinguishing a permanent '
    'misconfiguration (profile exceeds the total effective budget) from transient '
    'contention (would fit once running jobs release budget). Empty array when nothing '
    'is starved.';
