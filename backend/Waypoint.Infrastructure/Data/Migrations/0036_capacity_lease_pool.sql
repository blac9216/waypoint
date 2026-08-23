-- Issue #569 (epic #558): shared capacity lease pool for multi-runner admission
-- (ADR-0018 Option B, owner ruling on #555, 2026-08-23; protocol/fairness decisions
-- recorded in ADR-0020).
--
-- NOTE ON NUMBERING: this branch was cut while PR #566 (compliance-content tables)
-- still held the 0035 slot open, so this migration took 0036 from the start; #566
-- has since merged as 0035 and the sequence is contiguous again -- same coordination
-- protocol as 0034's own numbering note.
--
-- Two tables:
--
--   1. capacity_pool -- the appliance's total shareable CPU/memory capacity, one
--      singleton row (id = 1, CHECK-enforced, same pattern as appliance_state).
--      source distinguishes 'operator' (explicit CapacityPool:PoolCpuCores/
--      PoolMemoryBytes configuration -- always authoritative, never overwritten by a
--      derived report) from 'derived' (runners report their host-derived capacity at
--      startup; concurrent reports converge via GREATEST since replicas on one host
--      all see the same hardware, and a container-capped replica must never shrink
--      the pool below what an uncapped sibling measured). reported_by records which
--      runner/operator config last wrote the row -- diagnostics only, never identity.
--
--   2. capacity_leases -- one row per job currently holding (or, when reserved=true,
--      waiting for) a slice of the pool. Keyed on job_id, not runner_id: the job's
--      queue lease (jobs.claimed_by, ADR-0014) already guarantees single ownership of
--      the job itself, so a capacity lease follows the job -- a re-claimed job after
--      worker loss takes over its existing capacity row rather than double-counting.
--      reserved=true rows are the ADR-0020 anti-starvation mechanism: a job denied
--      capacity past the starvation threshold parks a reservation that counts against
--      the pool for OTHER jobs' admission (so freed capacity accumulates for it)
--      while it waits to convert to an active lease. heartbeat_at/expires_at mirror
--      the jobs-table lease columns; rows whose expires_at has passed stop counting
--      against the pool immediately (every claim filters on expires_at > now()) and
--      are physically deleted by LeaseRecoveryHostedService's sweep, consistent with
--      existing job-lease recovery semantics.
--
-- Grants: BOTH runner roles get the full claim/heartbeat/release/reap surface on
-- capacity_leases (the pool exists precisely so the two runner domains share one
-- capacity budget) and SELECT/INSERT/UPDATE on capacity_pool (each runner upserts its
-- derived capacity at startup). DELETE on capacity_pool is deliberately withheld from
-- both roles -- nothing in runner code ever deletes the pool row, and losing it would
-- deny all admission appliance-wide (the fail-safe default), so destroying it stays an
-- owner/migration-connection action. The API reads both tables for GET /system through
-- the owner connection; no API-side grant is needed.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS capacity_pool (
    id            SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    cpu_cores     DOUBLE PRECISION NOT NULL CHECK (cpu_cores > 0),
    memory_bytes  BIGINT NOT NULL CHECK (memory_bytes > 0),
    source        TEXT NOT NULL CHECK (source IN ('operator', 'derived')),
    reported_by   TEXT NOT NULL,
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE capacity_pool IS 'Issue #569 (ADR-0018/0019): singleton appliance capacity the shared lease pool admits against. Absent row = no admission (fail safe).';
COMMENT ON COLUMN capacity_pool.source IS 'operator = explicit CapacityPool configuration, authoritative; derived = runner host-derived report, GREATEST-converged, never overwrites operator.';
COMMENT ON COLUMN capacity_pool.reported_by IS 'Which runner (worker id) or operator config last wrote this row. Diagnostics only.';

CREATE TABLE IF NOT EXISTS capacity_leases (
    job_id        UUID PRIMARY KEY REFERENCES jobs(id) ON DELETE CASCADE,
    runner_id     TEXT NOT NULL,
    job_type      TEXT NOT NULL,
    cpu_cores     DOUBLE PRECISION NOT NULL CHECK (cpu_cores >= 0),
    memory_bytes  BIGINT NOT NULL CHECK (memory_bytes >= 0),
    reserved      BOOLEAN NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    heartbeat_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at    TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE capacity_leases IS 'Issue #569 (ADR-0020): active (reserved=false) and starvation-reservation (reserved=true) claims against capacity_pool. Expired rows stop counting immediately and are swept by lease recovery.';
COMMENT ON COLUMN capacity_leases.reserved IS 'TRUE = anti-starvation reservation (counts against the pool for other jobs, not yet running); FALSE = active lease held by a running job.';

CREATE INDEX IF NOT EXISTS idx_capacity_leases_expiry ON capacity_leases (expires_at);

-- Shared claim/heartbeat/release/reap surface (both execution domains draw from one pool).
GRANT SELECT, INSERT, UPDATE, DELETE ON capacity_leases TO waypoint_compliance_runner, waypoint_download_runner;

-- Startup derived-capacity upsert + admission reads. No DELETE (see header).
GRANT SELECT, INSERT, UPDATE ON capacity_pool TO waypoint_compliance_runner, waypoint_download_runner;
