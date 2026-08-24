-- Issue #585 (epic #582, third sub-issue, building on ADR-0021 (#583) and the
-- target_credential_bindings persistence slice (#584, migration 0043)): the
-- execution-side, per-job credential-purpose ledger. At run creation,
-- RunCreationService resolves every purpose a selected target's scan requires
-- (per the shared CredentialPurposeMatrix) from that target's
-- target_credential_bindings rows plus any validated per-target/per-purpose
-- overrides on the request, and SNAPSHOTS the result here -- one row per
-- (job, purpose) -- inside the same fan-out transaction that creates the job
-- rows. This table is the immutable record of what each job will execute with:
-- a later edit to a target's bindings (or the target/credential themselves)
-- never changes an in-flight or already-created run (ADR-0021 SS5), exactly
-- the property jobs.credential_id's single-value snapshot has always had,
-- generalized to N purposes.
--
--   1. job_id CASCADEs from jobs -- a binding row is meaningless without its
--      job, and run purge (migration 0042) deletes jobs wholesale; their
--      binding rows must go with them, like job_events.
--
--   2. credential_id is nullable with ON DELETE SET NULL plus the same three
--      non-secret attribution snapshot columns migration 0041 gave runs/jobs
--      (issue #593): a credential referenced only by TERMINAL jobs' binding
--      rows must not block credential deletion -- CredentialRepository
--      .DeleteAsync detaches those rows (snapshot first, then NULL) in the
--      same transaction as the delete, and counts a NON-terminal job's binding
--      reference as an active_jobs blocker even when jobs.credential_id names
--      a different (or no) credential. At insert time credential_id is always
--      set; it only becomes NULL through that detach path. SET NULL (not
--      RESTRICT) matches 0041's runs/jobs reasoning: these are point-in-time
--      history once the job is terminal, and the application-level blocker
--      count is the enforcement point for live rows, not the FK action.
--
--   3. LEGACY / DUAL-WRITE CONTRACT (the execution-side counterpart of
--      0043's): jobs.credential_id is NOT retired. For every job fanned out
--      after this migration, jobs.credential_id continues to carry the
--      resolved credential for the job's EXECUTION purpose (the kind's
--      default purpose per CredentialPurposeMatrix.DefaultPurposeByTargetKind
--      -- the one purpose today's wrappers actually authenticate with), and
--      this table carries that same reference PLUS any additional resolved
--      purposes (e.g. a vsphere target's vcsa-ssh binding, which has no
--      jobs-column slot). Handlers (ScanJobHandler, DiscoverJobHandler)
--      prefer this table's row for their execution purpose and fall back to
--      jobs.credential_id when a job has NO rows here -- i.e. any job row
--      created before this migration, or a run-secret ("my credentials") job,
--      which deliberately has neither (ADR-0011; per-purpose ad hoc secrets
--      are issue #586). The halt trigger (migration 0005) and the
--      consecutive-auth-failure halt/unblock/swap queries continue to key off
--      jobs.credential_id -- complete for this slice because the execution
--      purpose IS the mirrored column; SwapAndResumeBlockedCredentialAsync
--      additionally updates the swapped jobs' binding rows so the ledger and
--      the column can never disagree about what a resumed job executes with.
--
--   4. No backfill: a pre-existing job row simply has no rows here, which is
--      exactly the handler fallback condition above -- backfilling would
--      fabricate purpose attributions for jobs that were never resolved
--      per-purpose.
--
--   5. Grants: the compliance runner claims scan/discover jobs and must read
--      its claimed job's snapshot to resolve which credential to decrypt per
--      purpose -- SELECT only. It never writes rows (fan-out is API-side, in
--      RunCreationService's transaction) and never updates them (the swap and
--      the #593 detach are both API-side too). The download runner's job
--      types (JobCapabilities.Download) carry no purpose bindings at all --
--      no grant, proven by RunnerRoleGrantDriftTests' negative cases.
CREATE TABLE IF NOT EXISTS job_credential_bindings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id UUID NOT NULL REFERENCES jobs (id) ON DELETE CASCADE,
    purpose TEXT NOT NULL,
    credential_id UUID REFERENCES credentials (id) ON DELETE SET NULL,
    credential_name TEXT NULL,
    credential_type TEXT NULL,
    credential_username TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT job_credential_bindings_purpose_check CHECK (
        purpose IN ('vsphere-api', 'vcsa-ssh', 'nsx-api', 'srg-ssh')
    ),
    CONSTRAINT job_credential_bindings_job_id_purpose_key UNIQUE (job_id, purpose)
);

CREATE INDEX IF NOT EXISTS idx_job_credential_bindings_job_id ON job_credential_bindings (job_id);
CREATE INDEX IF NOT EXISTS idx_job_credential_bindings_credential_id ON job_credential_bindings (credential_id);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        GRANT SELECT ON job_credential_bindings TO waypoint_compliance_runner;
    END IF;
END
$$;
