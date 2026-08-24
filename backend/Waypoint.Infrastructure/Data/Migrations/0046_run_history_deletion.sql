-- Issue #592 (epic #588, last child): Admin-only, audited, idempotent deletion of
-- TERMINAL runs' operational records, kept structurally separate from #594's
-- compliance-domain purge (migration 0042, RunPurgeService). Epic #588's Design
-- section: "Deleting operational history must not implicitly delete domain state;
-- destructive domain cleanup is explicit and separately authorized" -- and, in the
-- other direction, generic history cleanup must DEFER to that domain purge when
-- compliance-owned artifacts are involved rather than reimplement or bypass it.
--
-- SAME "runs/jobs rows are RETAINED" design decision 0042 already established, for
-- the identical structural reason: job_events is append-only-by-trigger (0020) and
-- its job_id column is a plain FK to jobs with no ON DELETE action (defaults to
-- RESTRICT), so deleting a jobs row a job_events row still references is not
-- achievable without corrupting that immutable SSE/history-replay ledger or
-- deleting it too -- and this feature has even less license to touch job_events
-- than purge did, since #592's own AC (c) explicitly calls out "respects
-- append-only triggers per the #657 precedent". runs.history_deleted_at (nullable)
-- therefore marks a run's OPERATIONAL record as deleted in place, mirroring
-- runs.purged_at exactly: GET /runs/{id} and GET /runs continue to return the row
-- (so it remains a valid FK target for job_events/audit_log/schedules.last_run_id),
-- but the row is excluded from the default (undeleted-only) list view once this is
-- set -- see RunHistoryDeletionService/RunsController for the read-side filter.
--
-- What this migration actually deletes, once a run qualifies (see the compliance
-- gate below): nothing at the database-row level beyond what's already gone. There
-- are no "orphaned operational projections" tables today that are NOT already
-- covered by an existing lifecycle (run_secrets is swept at terminal transition by
-- JobQueueRepository.TryCompleteRunAsync; job_credential_bindings CASCADEs from
-- jobs, which are never deleted). This deliberately keeps the deletion mechanism
-- honest about what "operational record" deletion means today: marking the run (and
-- transitively its jobs, via the same history_deleted_at read-side filter keyed off
-- run_id) as no longer part of the operator-facing history list/UI, severing the
-- one cross-domain reference the issue names (schedules.last_run_id), while never
-- touching job_events (the actual log/diagnostic ledger) or any domain table.
ALTER TABLE runs ADD COLUMN IF NOT EXISTS history_deleted_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN runs.history_deleted_at IS
    'Issue #592: set once generic operational-history deletion completes for this TERMINAL run. The run/job rows and job_events ledger are retained (same rationale as runs.purged_at, migration 0042) -- this only marks the row excluded from the default history list. NULL for a never-deleted run. Mutually exclusive in practice with purged_at being NULL for scan/remediate runs: RunHistoryDeletionService refuses (409) to set this for a compliance run whose purged_at is still NULL.';

-- run_history_deletion_tombstones ------------------------------------------------
-- Append-only, non-secret audit record -- same shape and same reasoning as
-- run_purge_tombstones (migration 0042): a first-class table gives a stable,
-- directly-queryable shape rather than overloading audit_log's open-ended
-- event_type vocabulary, and AC (d) requires "reuse/extend that table or a
-- sibling -- justify". A SIBLING table (not a reuse of run_purge_tombstones) is
-- the right shape here, not an extension: run_purge_tombstones' outcome CHECK
-- constraint is closed to 'completed' and its detail JSON documents artifact
-- counts specific to the compliance purge; conflating the two would force every
-- future reader of either table to branch on "which kind of tombstone is this"
-- instead of the table name already saying so, and would let a future migration
-- accidentally relax one feature's append-only trigger while touching the other's
-- rows. One row per run, written exactly once, the instant the deletion completes.
CREATE TABLE IF NOT EXISTS run_history_deletion_tombstones (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL,
    run_type TEXT NOT NULL,
    prior_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    outcome TEXT NOT NULL,
    detail JSONB NOT NULL DEFAULT '{}'::jsonb,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT run_history_deletion_tombstones_outcome_check CHECK (outcome IN ('completed')),
    CONSTRAINT run_history_deletion_tombstones_run_id_key UNIQUE (run_id)
);

CREATE INDEX IF NOT EXISTS idx_run_history_deletion_tombstones_occurred_at ON run_history_deletion_tombstones (occurred_at DESC);

COMMENT ON TABLE run_history_deletion_tombstones IS
    'Issue #592: append-only non-secret audit record of a completed generic operational-history deletion -- actor, time, prior state, and outcome. Written exactly once per run. Deliberately a sibling of run_purge_tombstones (0042), not a shared table -- see this migration''s header comment for why.';

CREATE OR REPLACE FUNCTION run_history_deletion_tombstones_block_mutation() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'run_history_deletion_tombstones is append-only: % is not permitted', TG_OP;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_run_history_deletion_tombstones_block_update
    BEFORE UPDATE ON run_history_deletion_tombstones
    FOR EACH ROW EXECUTE FUNCTION run_history_deletion_tombstones_block_mutation();

CREATE OR REPLACE TRIGGER trg_run_history_deletion_tombstones_block_delete
    BEFORE DELETE ON run_history_deletion_tombstones
    FOR EACH ROW EXECUTE FUNCTION run_history_deletion_tombstones_block_mutation();

-- No GUC carve-out, no runner grants: unlike 0042's attestation_snapshots carve-out
-- (needed because that table's own append-only trigger blocked a legitimate
-- API-side DELETE), this feature never deletes a row any append-only trigger
-- protects -- it only sets runs.history_deleted_at (an ordinary nullable column,
-- no trigger guards it beyond the pre-existing trg_runs_updated_at) and inserts
-- into its own new append-only table. Entirely API-side (RunHistoryDeletionService
-- runs on the same owner-privileged connection every other controller-invoked
-- service already uses) -- no runner-executed job, so no compliance-runner or
-- download-runner grant is needed on either the new column or the new table.
