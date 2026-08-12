-- Issue #443 (ADR-0013 §1): a minimal database-persisted signal the control plane can
-- report through GET /system to distinguish API health from runner availability, now
-- that the API process itself hosts no dispatcher. Runners already write job
-- claim/lease/event state directly to Postgres (JobQueueRepository), but nothing
-- previously recorded "a compliance-runner or download-runner process is alive right
-- now and can claim these job types" independent of any in-flight job -- a runner idle
-- between jobs left no trace at all. This table is that trace: each runner process
-- upserts its own row on a periodic cadence (ReadinessReportingHostedService /
-- RunnerHealthReportingHostedService, issue #443's addition to both), and the control
-- plane reads it back as a simple "how long since last_seen_at" staleness check --
-- no cross-process locking or coordination, matching the job-lease heartbeat's own
-- shape (0016's expired-lease recovery uses the identical "no update in N seconds
-- means the process is gone" reasoning).
--
-- worker_id is the stable identity a single row keys on -- one row per logical worker
-- process, upserted in place rather than appended, so staleness is "how old is this
-- row" rather than "which of many rows for this domain is newest". job_types is the
-- worker's allowlist at the JobCapabilities granularity (compliance-runner and
-- download-runner today; ADR-0013 does not preclude more workers of the same domain
-- later, each with its own worker_id), stored as a JSONB array rather than a normalized
-- join table because there is exactly one small, closed, infrequently-changing list per
-- row and no cross-worker query ever needs to join through it (mirrors
-- attestation_snapshots.metadata and depot_artifacts.metadata's "don't invent structure
-- ahead of the real shape" reasoning, ADR-0002).
CREATE TABLE IF NOT EXISTS worker_registry (
    worker_id TEXT PRIMARY KEY,
    job_types JSONB NOT NULL,
    ready BOOLEAN NOT NULL DEFAULT true,
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- GET /system groups workers by the job types they serve; an index on the JSONB column
-- is not worth adding at this table's expected cardinality (a handful of rows, one per
-- runner process) -- every read is a full-table scan by design, matching
-- appliance_state's own single/few-row read pattern.

COMMENT ON TABLE worker_registry IS
    'Issue #443: one row per live runner process, upserted on a periodic heartbeat. '
    'last_seen_at older than the configured staleness window means the control plane '
    'reports that domain unavailable, independent of whether any job is currently claimed.';
