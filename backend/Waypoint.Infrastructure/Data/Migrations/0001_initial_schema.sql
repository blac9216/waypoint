-- Waypoint M1 schema (issue #4).
--
-- Tables: credentials, credential_secrets, runs, jobs, job_events,
-- depot_artifacts, downloads, audit_log, appliance_state. See
-- docs/api-contract.md "Postgres schema sketch" (the contract this
-- implements) and ADR-0002 (Postgres), ADR-0005 (envelope-encrypted
-- secrets), ADR-0008 (job engine). Deviations from the contract sketch are
-- called out inline and summarized in backend/README.md.
--
-- Idempotent by construction, not only via the schema_migrations tracking
-- table that gates re-application (see SchemaMigrator): every statement
-- uses IF NOT EXISTS / OR REPLACE / ON CONFLICT, so running this file a
-- second time directly against an already-migrated database is a no-op,
-- never an error. gen_random_uuid() and CREATE OR REPLACE TRIGGER are both
-- built into PostgreSQL 16 (ADR-0002 pins 16+) — no extensions required.

-- Shared trigger: every mutable table below stamps updated_at on UPDATE.
-- job_events and audit_log are append-only and deliberately have no
-- updated_at column, so they get no trigger.
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- credentials ----------------------------------------------------------
-- Metadata only. ADR-0005 scope is service/shared credentials; owner
-- defaults to 'shared' but is not CHECK-restricted to it, so a later
-- ADR-0011 "personal credential storage" feature is an application change,
-- not a schema migration.
CREATE TABLE IF NOT EXISTS credentials (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    credential_type TEXT NOT NULL,
    owner TEXT NOT NULL DEFAULT 'shared',
    health TEXT NOT NULL DEFAULT 'unknown',
    rotated_at TIMESTAMPTZ NULL,
    created_by TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT credentials_name_key UNIQUE (name),
    CONSTRAINT credentials_health_check CHECK (health IN ('unknown', 'valid', 'auth_failing'))
);

CREATE OR REPLACE TRIGGER trg_credentials_updated_at
    BEFORE UPDATE ON credentials
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- credential_secrets -----------------------------------------------------
-- Envelope encryption (ADR-0005, AWX pattern): ciphertext and the wrapped
-- data key are separate columns; master_key_id makes rotation an online
-- re-wrap rather than a schema change. One secret blob per credential —
-- ADR-0005's "write-only through the API: overwrite/delete, never read
-- back" means rotation replaces this row in place, it does not version it.
CREATE TABLE IF NOT EXISTS credential_secrets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    credential_id UUID NOT NULL REFERENCES credentials (id) ON DELETE CASCADE,
    ciphertext BYTEA NOT NULL,
    data_key_wrapped BYTEA NOT NULL,
    master_key_id TEXT NOT NULL,
    algorithm TEXT NOT NULL DEFAULT 'AES-256-GCM',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT credential_secrets_credential_id_key UNIQUE (credential_id)
);

CREATE OR REPLACE TRIGGER trg_credential_secrets_updated_at
    BEFORE UPDATE ON credential_secrets
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- runs -------------------------------------------------------------------
-- A user-initiated Run fans out into one Job per target/component
-- (ADR-0008). Only the scan/remediate job types are ever wrapped in a Run;
-- the M1 download vertical slice's job types (download, catalog-index,
-- discover, ...) are queued as standalone jobs per docs/api-contract.md's
-- per-endpoint "202 -> <type> job" notes, so jobs.run_id is nullable (see
-- jobs table below) rather than required as the contract sketch's flat
-- field list might suggest.
--
-- site_id is a forward reference: sites (M2, issue #13) do not exist yet
-- in this schema, so there is deliberately no FK — see backend/README.md.
CREATE TABLE IF NOT EXISTS runs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_type TEXT NOT NULL,
    site_id UUID NULL,
    state TEXT NOT NULL DEFAULT 'pending',
    paused BOOLEAN NOT NULL DEFAULT false,
    blocked BOOLEAN NOT NULL DEFAULT false,
    blocked_reason TEXT NULL,
    scope JSONB NOT NULL DEFAULT '{}'::jsonb,
    credential_id UUID NULL REFERENCES credentials (id),
    initiated_by TEXT NULL,
    schedule_id UUID NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at TIMESTAMPTZ NULL,
    completed_at TIMESTAMPTZ NULL,
    CONSTRAINT runs_run_type_check CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update'
    )),
    CONSTRAINT runs_state_check CHECK (state IN (
        'pending', 'running', 'completed', 'completed_with_failures', 'aborted'
    ))
);

CREATE INDEX IF NOT EXISTS idx_runs_state ON runs (state);
CREATE INDEX IF NOT EXISTS idx_runs_site_id ON runs (site_id) WHERE site_id IS NOT NULL;

CREATE OR REPLACE TRIGGER trg_runs_updated_at
    BEFORE UPDATE ON runs
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- jobs ---------------------------------------------------------------
-- The queue (ADR-0008): claimed with
--   SELECT id FROM jobs WHERE state = 'queued'
--   ORDER BY priority, created_at FOR UPDATE SKIP LOCKED LIMIT 1
-- idx_jobs_queue_claim below is a partial index on exactly that predicate
-- and ordering, so the claim is an index scan over only the claimable
-- rows, not a scan of the whole (eventually large) table.
--
-- Priority is six-valued (NSX=1, VCSA=2, vCenter=3, ESXi=4, VM=5, SRG=6 —
-- ADR-0008 / domain-model.md); other job types (download, catalog-index,
-- ...) declare their own priority within the same 1-6 range.
--
-- Lease/heartbeat columns are here from day one (claimed_by, claimed_at,
-- lease_expires_at, heartbeat_at) so dead-job recovery (a worker dying
-- mid-job) is a query against idx_jobs_lease_recovery, not a future
-- migration against live rows.
--
-- run_id and target_id are both nullable — see the runs table comment
-- above for run_id; target_id is a forward reference to the M2 targets
-- table (no FK yet), with target_name carrying a human-readable label for
-- job types that have no target row at all (e.g. a depot artifact name for
-- a download job).
CREATE TABLE IF NOT EXISTS jobs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NULL REFERENCES runs (id),
    job_type TEXT NOT NULL,
    target_id UUID NULL,
    target_name TEXT NULL,
    credential_id UUID NULL REFERENCES credentials (id),
    priority SMALLINT NOT NULL,
    state TEXT NOT NULL DEFAULT 'queued',
    stage TEXT NULL,
    counts JSONB NOT NULL DEFAULT '{}'::jsonb,
    payload JSONB NOT NULL DEFAULT '{}'::jsonb,
    note TEXT NULL,
    claimed_by TEXT NULL,
    claimed_at TIMESTAMPTZ NULL,
    lease_expires_at TIMESTAMPTZ NULL,
    heartbeat_at TIMESTAMPTZ NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    max_attempts INT NOT NULL DEFAULT 3,
    created_by TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at TIMESTAMPTZ NULL,
    finished_at TIMESTAMPTZ NULL,
    CONSTRAINT jobs_job_type_check CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update'
    )),
    CONSTRAINT jobs_priority_check CHECK (priority BETWEEN 1 AND 6),
    CONSTRAINT jobs_state_check CHECK (state IN (
        'queued', 'running', 'attesting', 'converting', 'uploaded', 'done',
        'failed', 'auth-failed', 'blocked', 'cancelled'
    ))
);

CREATE INDEX IF NOT EXISTS idx_jobs_queue_claim ON jobs (priority, created_at) WHERE state = 'queued';
CREATE INDEX IF NOT EXISTS idx_jobs_lease_recovery ON jobs (lease_expires_at) WHERE state = 'running';
CREATE INDEX IF NOT EXISTS idx_jobs_run_id ON jobs (run_id) WHERE run_id IS NOT NULL;
-- Consecutive-auth-failure queue halt (ADR-0008): most recent jobs for a
-- given credential, newest first.
CREATE INDEX IF NOT EXISTS idx_jobs_credential_recent ON jobs (credential_id, created_at DESC) WHERE credential_id IS NOT NULL;

CREATE OR REPLACE TRIGGER trg_jobs_updated_at
    BEFORE UPDATE ON jobs
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- job_events ---------------------------------------------------------
-- Append-only SSE replay source for both stream scopes in
-- docs/api-contract.md: the global stream (every event) and the per-run
-- stream (run_id set).
--
-- Scope. The contract's six event types are NOT all job-scoped, so neither
-- job_id nor run_id can be NOT NULL for the whole table. Three tiers,
-- enforced by job_events_scope_check below:
--   - job-scoped     (job_id required): job.state, job.log,
--                    download.progress — they describe one job.
--   - run-scoped     (run_id required, job_id NULL): run.progress,
--                    queue.state. A "queue" is a run's priority queue
--                    (ADR-0008 / domain-model.md "halt that priority
--                    queue"), so a queue.state event names a run, not a job.
--   - appliance-wide (both NULL): system.notice — it has no job and no run
--                    at all, and must still be carriable on the global
--                    stream.
-- The ELSE false arm is deliberate: if a seventh event type is ever added to
-- job_events_event_type_check without being classified here, it fails closed
-- rather than entering the table unscoped.
--
-- Ordering. seq is the SSE Last-Event-ID and the primary key, so both replay
-- scopes are a forward btree range scan, never a sort:
--   - global replay:  SELECT ... WHERE seq > $lastSeq ORDER BY seq
--   - per-run replay: the same shape scoped by run_id, served by
--                     idx_job_events_run_seq below.
--
-- The guarantee that makes that replay safe is COMMIT-ORDERED ASSIGNMENT,
-- not the ordering of an identity column. A plain GENERATED ALWAYS AS
-- IDENTITY assigns at INSERT time while rows become visible at COMMIT time,
-- so a slow writer holding the lower seq can commit *after* a faster writer
-- holding the higher one. A reader that recorded the higher value as its
-- Last-Event-ID would then never see the lower one again — a committed,
-- durable, permanently unreachable event. See
-- trg_job_events_assign_seq below for how that is closed, and
-- JobEventsSeqTests for the test that fails without it.
--
-- run_id is nullable for job-scoped events too: standalone jobs (download,
-- catalog-index, ...) have no run to scope events to; they still appear on
-- the global stream.

-- seq is drawn from this sequence by trg_job_events_assign_seq rather than
-- by an identity column, because the trigger must take the ordering lock
-- *before* the value is drawn — column defaults (identity included) are
-- evaluated before BEFORE-row triggers run, which would leave exactly the
-- assignment/commit race described above. CACHE 1 is load-bearing: a cached
-- sequence hands out per-session blocks, which would reintroduce the same
-- inversion between sessions.
CREATE SEQUENCE IF NOT EXISTS job_events_seq_seq AS BIGINT CACHE 1;

CREATE TABLE IF NOT EXISTS job_events (
    seq BIGINT PRIMARY KEY,
    run_id UUID NULL,
    job_id UUID NULL REFERENCES jobs (id),
    event_type TEXT NOT NULL,
    payload JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT job_events_event_type_check CHECK (event_type IN (
        'job.state', 'job.log', 'run.progress', 'queue.state',
        'download.progress', 'system.notice'
    )),
    CONSTRAINT job_events_scope_check CHECK (
        CASE event_type
            WHEN 'job.state' THEN job_id IS NOT NULL
            WHEN 'job.log' THEN job_id IS NOT NULL
            WHEN 'download.progress' THEN job_id IS NOT NULL
            WHEN 'run.progress' THEN job_id IS NULL AND run_id IS NOT NULL
            WHEN 'queue.state' THEN job_id IS NULL AND run_id IS NOT NULL
            WHEN 'system.notice' THEN job_id IS NULL AND run_id IS NULL
            ELSE false
        END
    )
);

ALTER SEQUENCE job_events_seq_seq OWNED BY job_events.seq;

-- Commit-ordered seq assignment.
--
-- pg_advisory_xact_lock is held from here until the inserting transaction
-- COMMITs or ROLLBACKs, and the sequence value is drawn while holding it.
-- So for any two events A and B on any stream: if seq(A) < seq(B), then A's
-- transaction had already committed before B's value was even drawn.
-- Therefore any reader that can see B can already see A, and advancing
-- Last-Event-ID to the highest observed seq can never strand a committed
-- event. This is what issue #6's SSE reader is entitled to assume.
--
-- The lock is global rather than per-run on purpose: the API contract's
-- global stream is itself a stream, so per-run serialization alone would
-- still let a global reader skip an event committed out of order by a
-- different run.
--
-- Cost, stated plainly: every job_events INSERT serializes against every
-- other one, and the lock is held for the remainder of the inserting
-- transaction. Write events in short transactions, and as late in a
-- transaction as possible — a writer that emits an event and then does
-- seconds of other work inside the same transaction stalls the whole event
-- stream for that long. Holding this lock across other row locks is also a
-- deadlock surface: emit-then-update-row in one transaction against
-- update-same-row-then-emit in another deadlocks, and Postgres resolves it by
-- aborting one of them. "Emit last, in a short transaction" avoids both
-- problems at once. seq is also not gap-free (a rolled-back insert burns its
-- value); that is acceptable because the guarantee above makes gap detection
-- unnecessary for replay safety.
CREATE OR REPLACE FUNCTION job_events_assign_seq()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(875190002);
    NEW.seq = nextval('job_events_seq_seq');
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_job_events_assign_seq
    BEFORE INSERT ON job_events
    FOR EACH ROW EXECUTE FUNCTION job_events_assign_seq();

CREATE INDEX IF NOT EXISTS idx_job_events_run_seq ON job_events (run_id, seq) WHERE run_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_job_events_job_id ON job_events (job_id) WHERE job_id IS NOT NULL;

-- depot_artifacts ------------------------------------------------------
-- Vendor catalog shapes are not ours to normalise (ADR-0002) — everything
-- vendor-specific lives in metadata JSONB. external_id is the depot's own
-- stable catalog identifier (unique per artifact); sha256 is promoted to a
-- real column because download/checksum verification queries on it.
CREATE TABLE IF NOT EXISTS depot_artifacts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    external_id TEXT NOT NULL,
    sha256 TEXT NULL,
    status TEXT NOT NULL DEFAULT 'indexed',
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    indexed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT depot_artifacts_external_id_key UNIQUE (external_id),
    CONSTRAINT depot_artifacts_status_check CHECK (status IN ('indexed', 'downloading', 'present', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_depot_artifacts_status ON depot_artifacts (status);
CREATE INDEX IF NOT EXISTS idx_depot_artifacts_metadata ON depot_artifacts USING GIN (metadata);

CREATE OR REPLACE TRIGGER trg_depot_artifacts_updated_at
    BEFORE UPDATE ON depot_artifacts
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- downloads ------------------------------------------------------------
-- Download state machine (docs/api-contract.md):
--   queued -> downloading -> verifying -> verified | failed
-- (checksum mismatch => failed, artifact quarantined). job_id links to the
-- jobs-queue row that actually executes the download; nullable because a
-- download can be queued before the job engine claims/creates its worker
-- row.
CREATE TABLE IF NOT EXISTS downloads (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    depot_artifact_id UUID NOT NULL REFERENCES depot_artifacts (id),
    job_id UUID NULL REFERENCES jobs (id),
    state TEXT NOT NULL DEFAULT 'queued',
    bytes_total BIGINT NULL,
    bytes_downloaded BIGINT NOT NULL DEFAULT 0,
    download_rate_bps BIGINT NULL,
    eta_seconds INT NULL,
    retry_count INT NOT NULL DEFAULT 0,
    max_retries INT NOT NULL DEFAULT 3,
    failure_reason TEXT NULL,
    requested_by TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ NULL,
    CONSTRAINT downloads_state_check CHECK (state IN (
        'queued', 'downloading', 'verifying', 'verified', 'failed', 'cancelled'
    ))
);

CREATE INDEX IF NOT EXISTS idx_downloads_depot_artifact_id ON downloads (depot_artifact_id);
CREATE INDEX IF NOT EXISTS idx_downloads_state ON downloads (state);

CREATE OR REPLACE TRIGGER trg_downloads_updated_at
    BEFORE UPDATE ON downloads
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- audit_log --------------------------------------------------------------
-- Append-only (security.md control 4: decrypt audit trail; also run
-- initiations, config versions, imports/updates as those land). event_type
-- is deliberately unconstrained (no CHECK) — the taxonomy grows across
-- many future issues and milestones, unlike the small closed vocabularies
-- above.
CREATE TABLE IF NOT EXISTS audit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    credential_id UUID NULL REFERENCES credentials (id),
    job_id UUID NULL REFERENCES jobs (id),
    run_id UUID NULL REFERENCES runs (id),
    detail JSONB NOT NULL DEFAULT '{}'::jsonb,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_audit_log_occurred_at ON audit_log (occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_log_credential_id ON audit_log (credential_id) WHERE credential_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_audit_log_run_id ON audit_log (run_id) WHERE run_id IS NOT NULL;

-- appliance_state ------------------------------------------------------
-- Singleton (version, mode, update status). id is pinned to 1 by the CHECK
-- constraint; the seed row below is inserted idempotently.
CREATE TABLE IF NOT EXISTS appliance_state (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    version TEXT NOT NULL DEFAULT '0.0.0-dev',
    build_sha TEXT NULL,
    mode TEXT NOT NULL DEFAULT 'connected',
    update_status TEXT NOT NULL DEFAULT 'idle',
    update_available_version TEXT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT appliance_state_singleton_check CHECK (id = 1),
    CONSTRAINT appliance_state_mode_check CHECK (mode IN ('connected', 'disconnected')),
    CONSTRAINT appliance_state_update_status_check CHECK (update_status IN (
        'idle', 'checking', 'downloading', 'ready', 'applying', 'failed'
    ))
);

INSERT INTO appliance_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_appliance_state_updated_at
    BEFORE UPDATE ON appliance_state
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
