-- Issue #594 (epic #577): Admin-only, crash-safe purge for terminal compliance runs
-- and every explicitly owned result artifact. Cancellation (abort) is not deletion --
-- there was no orchestrated cleanup lifecycle for scan artifacts, attestation
-- snapshots, run-scoped secrets, or the database projections a scan run produces.
--
-- DESIGN DECISION -- runs/jobs rows are RETAINED, not deleted. The alternative
-- ("delete runs/jobs outright, tombstone is the only surviving record") was
-- considered and rejected: job_events is append-only by DB trigger (0020) and its
-- job_id column is a plain (RESTRICT-by-default) FK to jobs -- see 0001's comment
-- "job_events and audit_log are append-only and deliberately have no updated_at
-- column". The epic's design section explicitly warns not to fight that trigger
-- without a documented reason, and relaxing job_events.job_id to SET NULL would
-- corrupt job_events_scope_check's job-scoped tiers (job.state/job.log/
-- download.progress all REQUIRE job_id NOT NULL for their row to make sense as an
-- SSE replay entry) -- nulling it on purge would leave unscoped, unreplayable
-- ghost rows in an otherwise-immutable ledger. Deleting the owning jobs/runs rows
-- is therefore not achievable without either corrupting that ledger or deleting
-- it too, and the epic's "inventories database projections" language does not
-- extend to job_events specifically for this reason.
--
-- Instead: runs.purged_at (nullable) marks a run as purged in place. GET
-- /runs/{id} and GET /runs continue to return the row (id, run_type, timestamps,
-- job counts) so a purged run's row remains a valid FK target for job_events,
-- audit_log, and schedules.last_run_id -- nothing referencing it needs to change
-- except the one FK the issue's Risk note calls out by name
-- (schedules.last_run_id, relaxed below purely as a safety net -- the service
-- layer nulls it explicitly in the same transaction it marks the run purged,
-- exactly like 0037/0041's "FK is a backstop, not the enforcement point" idiom).
-- What purge actually deletes: attestation_snapshots rows for the run (the
-- at-scan-time attestations-applied ledger -- see the carve-out below; this one
-- IS append-only by trigger, migration 0021, but the AC explicitly names
-- "compliance projections" and this table is the flagship example, so it gets the
-- same narrow, structural carve-out 0020 already established for audit_log's
-- credential_id nulling rather than being left out of scope), any leftover
-- run_secrets row (normally already deleted by TryCompleteRunAsync's terminal
-- transition -- see JobQueueRepository -- this is a defensive sweep for a run
-- that crashed before reaching a clean terminal state), and the three
-- ScanArtifactPaths files per job (raw HDF, attested HDF, CKL) on the
-- compliance-runner's artifact volume. jobs.state/runs.state are left exactly as
-- they were; only jobs.payload is NOT touched either (no reason to -- payload
-- carries scan target scope, not artifact content).
--
-- attestation_snapshots carve-out --------------------------------------------
-- 0021's trg_attestation_snapshots_block_delete blocks EVERY delete
-- unconditionally (no FK-driven exception like audit_log's). Rather than relax
-- that generally (which would silently re-open the table to any future caller's
-- accidental DELETE), the carve-out is scoped to a single explicit signal: a
-- session-local GUC (`waypoint.purge_run_id`) that RunPurgeService sets with
-- `SET LOCAL` immediately before its DELETE, inside the same transaction, and
-- that Postgres automatically discards at COMMIT/ROLLBACK -- so the escape hatch
-- cannot leak into any other statement, connection, or pooled-connection reuse.
-- The trigger only permits a DELETE whose OLD.run_id matches that GUC's value
-- (parsed as a uuid); every other DELETE, and every UPDATE, still raises exactly
-- as before. This keeps the append-only guarantee structural (the trigger, not
-- caller trust) while giving purge -- and only purge, one run at a time -- the
-- one legitimate reason this table's own migration didn't anticipate.
CREATE OR REPLACE FUNCTION attestation_snapshots_block_mutation()
RETURNS TRIGGER AS $$
DECLARE
    purge_run_id TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        purge_run_id := current_setting('waypoint.purge_run_id', true);
        IF purge_run_id IS NOT NULL AND purge_run_id <> '' AND OLD.run_id = purge_run_id::uuid THEN
            RETURN OLD;
        END IF;
    END IF;

    RAISE EXCEPTION 'attestation_snapshots is append-only: % is not permitted (id=%)',
        TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;
--
-- run_purges -----------------------------------------------------------------
-- Durable, retryable per-run purge lifecycle. One row per run (not append-only --
-- purge is inherently re-invocable and its own state IS the retry state, unlike
-- managed_tool_installs' per-attempt ledger). db_phase_done tracks the API-side
-- synchronous work (attestation_snapshots delete, run_secrets sweep, schedule FK
-- null) that happens inline in the orchestration call; artifact_job_id is the
-- enqueued compliance-runner 'purge' job that deletes the on-disk files (the API
-- process has no write access to the artifacts volume -- see
-- deploy/docker-compose.yml's ":ro" backend mount / ADR-0014 §7); artifacts_phase
-- mirrors that job's own outcome back so a re-invocation can tell "still running",
-- "done", or "failed, retry" apart without re-deriving it from jobs.state. A
-- fully-completed row (both phases done) is what flips runs.purged_at and writes
-- the tombstone -- see RunPurgeService.
CREATE TABLE IF NOT EXISTS run_purges (
    run_id UUID PRIMARY KEY REFERENCES runs (id),
    requested_by TEXT NOT NULL,
    requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    prior_state TEXT NOT NULL,
    db_phase_done BOOLEAN NOT NULL DEFAULT false,
    artifact_job_id UUID NULL REFERENCES jobs (id) ON DELETE SET NULL,
    artifacts_phase TEXT NOT NULL DEFAULT 'pending',
    artifacts_total INT NOT NULL DEFAULT 0,
    artifacts_deleted INT NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    completed_at TIMESTAMPTZ NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT run_purges_artifacts_phase_check CHECK (artifacts_phase IN (
        'pending', 'running', 'done', 'failed'
    ))
);

CREATE OR REPLACE TRIGGER trg_run_purges_updated_at
    BEFORE UPDATE ON run_purges
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE run_purges IS
    'Issue #594: durable per-run purge lifecycle so a filesystem/database partial failure is retryable rather than re-run from scratch or silently reported successful while artifacts remain.';
COMMENT ON COLUMN run_purges.db_phase_done IS
    'True once the API-side synchronous work (attestation_snapshots delete, defensive run_secrets delete, schedules.last_run_id null) has committed for this run.';
COMMENT ON COLUMN run_purges.artifact_job_id IS
    'The compliance-runner "purge" job enqueued to delete this run''s on-disk scan artifacts. NULL before the first enqueue, or after the job row itself is later garbage-collected (ON DELETE SET NULL) -- artifacts_phase is the authoritative status either way, not the presence of this reference.';
COMMENT ON COLUMN run_purges.artifacts_phase IS
    'pending: not yet enqueued. running: job claimed, not yet reported terminal. done: every artifact file confirmed deleted or already absent. failed: at least one file could not be deleted -- retryable by re-invoking POST /runs/{id}/purge.';

-- run_purge_tombstones ---------------------------------------------------------
-- Append-only, non-secret audit record (epic #577: "preserves a minimal
-- append-only audit tombstone"). Deliberately separate from run_purges (which is
-- mutable retry-state bookkeeping, not the audit record) and from audit_log
-- (whose event_type vocabulary is open-ended free text with no query surface
-- dedicated to purge outcomes -- a first-class table gives GET
-- /runs/{id}/purge a stable, directly-queryable shape). One row is written only
-- once, at the moment run_purges reaches full completion (both phases done) --
-- see the append-only trigger below, matching managed_tool_installs' (0037)
-- append-only discipline. NEVER carries secret material: run_type/prior_state
-- are the same closed small vocabularies runs_run_type_check/runs_state_check
-- already constrain, and detail is a small non-secret JSON summary (counts only).
CREATE TABLE IF NOT EXISTS run_purge_tombstones (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL,
    run_type TEXT NOT NULL,
    prior_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    outcome TEXT NOT NULL,
    detail JSONB NOT NULL DEFAULT '{}'::jsonb,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT run_purge_tombstones_outcome_check CHECK (outcome IN ('completed')),
    CONSTRAINT run_purge_tombstones_run_id_key UNIQUE (run_id)
);

CREATE INDEX IF NOT EXISTS idx_run_purge_tombstones_occurred_at ON run_purge_tombstones (occurred_at DESC);

COMMENT ON TABLE run_purge_tombstones IS
    'Issue #594: append-only non-secret audit record of a completed run purge -- actor, time, prior state, and outcome. Written exactly once per run, the instant RunPurgeService confirms both the database and artifact-deletion phases are done.';

CREATE OR REPLACE FUNCTION run_purge_tombstones_block_mutation() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'run_purge_tombstones is append-only: % is not permitted', TG_OP;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_run_purge_tombstones_block_update
    BEFORE UPDATE ON run_purge_tombstones
    FOR EACH ROW EXECUTE FUNCTION run_purge_tombstones_block_mutation();

CREATE OR REPLACE TRIGGER trg_run_purge_tombstones_block_delete
    BEFORE DELETE ON run_purge_tombstones
    FOR EACH ROW EXECUTE FUNCTION run_purge_tombstones_block_mutation();

-- runs.purged_at ----------------------------------------------------------------
-- The in-place "purged" marker (see design decision above). Nullable; set exactly
-- once, in the same transaction as the run_purge_tombstones INSERT.
ALTER TABLE runs ADD COLUMN IF NOT EXISTS purged_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN runs.purged_at IS
    'Issue #594: set once purge completes (database projections and artifact files both confirmed gone). The run row itself, and its jobs, are retained -- only compliance-owned projections/artifacts are removed. NULL for a never-purged run.';

-- jobs.job_type / runs.run_type: widen the closed sets with the new purge job
-- type (compliance-runner domain -- it needs artifact-volume write access,
-- ADR-0014 §7, exactly like scan). RunPurgeService enqueues the artifact-deletion
-- job via the same CreateRunAsync + FanOutJobsAsync pattern every other job type
-- in this codebase actually uses (tool-install, catalog-index, content-pull --
-- "standalone" jobs.run_id NULL is a forward-looking design 0001 describes that no
-- real caller exercises), so runs_run_type_check needs 'purge' too, not only
-- jobs_job_type_check. This purge-wrapper run is a distinct, internal run from the
-- TARGET run being purged -- it exists only to carry the artifact-deletion job
-- through the ordinary claim/lease/completion machinery, and its own initiator is
-- recorded as "purge:<original actor>" so it is identifiable in run history
-- without being confused for an operator-initiated run.
ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_job_type_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_job_type_check CHECK (job_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test',
    'tool-install', 'purge'
));

ALTER TABLE runs DROP CONSTRAINT IF EXISTS runs_run_type_check;
ALTER TABLE runs ADD CONSTRAINT runs_run_type_check CHECK (run_type IN (
    'scan', 'remediate', 'discover', 'download', 'catalog-index',
    'bundle-export', 'bundle-import', 'content-library-sync',
    'content-pull', 'content-import', 'update', 'credential-test',
    'tool-install', 'purge'
));

-- NEVER SCHEDULABLE -- 'purge' is deliberately absent from schedules_job_type_check
-- (0030) and from Waypoint.Core.Scheduling.Schedule's ScheduleJobTypes allowlist,
-- which SchedulesController validates against (ValidateJobType), NOT against
-- jobs_job_type_check/runs_run_type_check above. No migration change is needed to
-- keep purge unschedulable -- the schedule surface's allowlist is closed and
-- separate by construction, the same guarantee CLAUDE.md's "remediation is never
-- schedulable" constraint already relies on for 'remediate'. See
-- ScheduleNeverAllowsPurgeTests (backend test) for the assertion that proves this
-- rather than just asserting it in a comment.

-- schedules.last_run_id: relax RESTRICT -> SET NULL, same rationale 0032 already
-- applied when the FK was first added (a schedule's most-recent-run pointer must
-- not block/be blocked by that run's own lifecycle) -- purge nulls this column
-- explicitly in RunPurgeService's own transaction as the primary enforcement
-- point (issue's Risk note: "Scheduled last_run_id references need safe
-- nulling"); this FK relaxation is the same defense-in-depth backstop 0037/0041
-- already documented this repository's convention as being, not the mechanism
-- purge relies on.
ALTER TABLE schedules DROP CONSTRAINT IF EXISTS schedules_last_run_id_fkey;
ALTER TABLE schedules ADD CONSTRAINT schedules_last_run_id_fkey
    FOREIGN KEY (last_run_id) REFERENCES runs (id) ON DELETE SET NULL;

-- audit_log.run_id / audit_log.job_id: NOT relaxed here. Purge never deletes a
-- runs or jobs row (see design decision above), so audit_log's existing FKs are
-- never exercised by this feature -- 0006's precedent (relax on delete) only
-- applies to a column purge actually deletes through. audit_log.credential_id
-- was relaxed in 0006 because CredentialRepository.DeleteAsync deletes the
-- credentials row itself; nothing here deletes runs or jobs.

-- Compliance-runner grants -------------------------------------------------------
-- The 'purge' job handler (compliance-runner only, see JobCapabilities.Compliance)
-- needs: SELECT on run_purges to read the job's own artifact inventory context (it
-- re-derives job ids for the run from jobs, not from a payload field, so no new
-- read path on jobs/runs beyond what 0025 already grants), and UPDATE to report
-- per-file progress back (artifacts_phase, artifacts_total, artifacts_deleted,
-- last_error, completed_at) as it deletes each file -- the same
-- "runner reports its own progress into durable state" shape RunSecretStore's
-- sliding-expiry UPDATE (0033) already established. INSERT/DELETE stay API-only
-- (RunPurgeService creates the row when it enqueues the job; nothing runner-side
-- ever removes a run_purges row). attestation_snapshots and run_secrets deletion
-- both happen API-side (RunPurgeService has full owner-connection access, per the
-- issue's own API-side-vs-runner-job split) -- no new runner grant needed for
-- either table.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, UPDATE (artifacts_phase, artifacts_total, artifacts_deleted, last_error, completed_at)
    ON run_purges TO waypoint_compliance_runner;
