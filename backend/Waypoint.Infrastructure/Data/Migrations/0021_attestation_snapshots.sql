-- Issue #306 (concern:security, epic #13): the real fix for the gap PR #305 could only
-- disclose. `GET /runs/{id}/attestations-applied` (RunsController.GetAttestationsApplied)
-- has re-resolved each scanned target's attestation config-doc LIVE, at request time,
-- since #299/#305 -- so editing a waiver after a run silently rewrites what that
-- endpoint reports about a HISTORICAL run. For a DoD compliance-history surface that is
-- an integrity gap, not a cosmetic one.
--
-- This table is the persisted, at-scan-time snapshot ScanJobHandler.ExecuteAttestStageAsync
-- (#275/#302) writes ONE row into per (job, target) the instant it resolves the
-- attestation for that target -- before the resolution result can ever be affected by a
-- LATER edit to the config-doc. RunsController then reads this table instead of calling
-- ConfigDocResolver.Resolve again, so a historical run's answer is immutable regardless
-- of any waiver edited afterward.
--
-- Granularity is PER-TARGET, not per-control (issue #306's AC: "ideally per-control, once
-- a control catalog exists" -- no control catalog exists in this codebase yet, matching
-- the live-resolution endpoint's own existing per-target granularity being replaced
-- here). Per-control is future work once a control-enumeration catalog exists.
--
-- Append-only like job_events/audit_log (0020/0006): a scan-time fact, once recorded,
-- must never be edited to match a later doc change -- that would silently recreate the
-- exact bug this table exists to close. No UPDATE/DELETE path is exposed by
-- AttestationSnapshotRepository (INSERT-only), the same repository-layer discipline
-- config_versions relies on; this migration also blocks both statements at the DB level
-- via trigger, matching 0020's stricter enforcement for a security-load-bearing table.
CREATE TABLE IF NOT EXISTS attestation_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL REFERENCES runs (id),
    job_id UUID NOT NULL REFERENCES jobs (id),
    target_id UUID NOT NULL,
    profile TEXT NOT NULL,
    scope TEXT NOT NULL,
    doc_id UUID NULL,
    doc_version INTEGER NULL,
    doc_author TEXT NULL,
    doc_version_created_at TIMESTAMPTZ NULL,
    applied BOOLEAN NOT NULL,
    expired BOOLEAN NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT attestation_snapshots_doc_fields_check CHECK (
        (doc_id IS NULL AND doc_version IS NULL AND doc_author IS NULL AND doc_version_created_at IS NULL)
        OR (doc_id IS NOT NULL AND doc_version IS NOT NULL AND doc_author IS NOT NULL AND doc_version_created_at IS NOT NULL)
    ),
    -- A lease-recovered/resumed attest stage (ADR-0012) re-runs the whole stage body,
    -- including config-doc resolution, on each claim -- ExecuteAttestStageAsync has no
    -- other idempotency guard today. Without this, a resumed stage would insert a
    -- second, possibly different, "at-scan-time" row for the same (job, target),
    -- corrupting the very immutability this table exists to provide. First write wins:
    -- AttestationSnapshotRepository.RecordAsync uses ON CONFLICT DO NOTHING against this
    -- constraint, so only the first genuine resolution for a job/target is recorded.
    CONSTRAINT attestation_snapshots_job_target_key UNIQUE (job_id, target_id)
);

-- The endpoint's access pattern is per-run; idx_attestation_snapshots_job_target_key
-- (backing the UNIQUE constraint above) already covers per-job lookups.
CREATE INDEX IF NOT EXISTS idx_attestation_snapshots_run_id ON attestation_snapshots (run_id);

CREATE OR REPLACE FUNCTION attestation_snapshots_block_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'attestation_snapshots is append-only: % is not permitted (id=%)',
        TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_attestation_snapshots_block_update
    BEFORE UPDATE ON attestation_snapshots
    FOR EACH ROW EXECUTE FUNCTION attestation_snapshots_block_mutation();

CREATE OR REPLACE TRIGGER trg_attestation_snapshots_block_delete
    BEFORE DELETE ON attestation_snapshots
    FOR EACH ROW EXECUTE FUNCTION attestation_snapshots_block_mutation();
