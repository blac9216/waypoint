-- Issue #586 (epic #582, fourth sub-issue, building on ADR-0021 and #585/PR #663's
-- job_credential_bindings resolution ledger): re-keys the ad hoc ("my credentials",
-- ADR-0011) run-secret store from one flat row per RUN to one row per
-- (run, target, purpose) -- the same generalization #584/#585 already made for
-- stored-credential bindings, applied to the encrypted ad hoc tier.
--
-- DESIGN DECISION -- evolve run_secrets in place, not a successor table:
--
--   RunSecretStore's whole contract (envelope encryption, sliding-expiry decrypt,
--   fail-closed audit, the unconditional per-terminal-completion DELETE) is keyed
--   off run_id today and none of that machinery changes shape -- only the identity
--   of "which row(s) belong to this run" gains two more coordinates. A successor
--   table would duplicate every column (ciphertext, wrapped key, master key id,
--   algorithm, expires_at) and force every caller -- StoreAsync, DecryptAsync,
--   DeleteAsync, DeleteExpiredAsync, the completion-transaction delete, the runner
--   grants -- to know which of two tables a given run's secret lives in. Evolving
--   in place keeps exactly one run-secret table, one set of grants, and one
--   unconditional `DELETE FROM run_secrets WHERE run_id = $1` that already covers
--   every row shape a run can have (see point 3).
--
--   1. target_id UUID NULL REFERENCES targets (id) ON DELETE CASCADE: the target
--      this credential applies to. NULL is the LEGACY shape -- a pre-#586 run-wide
--      "my credentials" row that has no per-target scoping (issue #434's original
--      one-row-per-run contract). A row fanned out under this migration's new
--      write path (RunCreationService, issue #586) always sets a real target_id.
--      ON DELETE CASCADE mirrors job_credential_bindings' job_id and
--      target_credential_bindings' target_id: a run secret naming a target that no
--      longer exists is meaningless, and targets are not deletable while
--      referenced by a live (non-terminal) run in the first place.
--
--   2. purpose TEXT NOT NULL DEFAULT '_legacy': the credential-purpose identifier
--      (CredentialPurposes.All) this row satisfies for its target. The sentinel
--      '_legacy' -- deliberately NOT a member of CredentialPurposes.All, so
--      CredentialPurposeMatrix.IsCompatible/ApplicablePurposes can never
--      accidentally match it -- marks a pre-#586 row exactly like target_id NULL
--      does; the two are set together (both-or-neither) and DEFAULT lets the
--      backfill-free column add stay purely additive (no existing row is rewritten
--      by this migration; only new columns with defaults are added).
--
--   3. Primary key: run_id ALONE cannot be the key of a multi-row-per-run table
--      anymore, so it is dropped in favor of a surrogate id, with the actual
--      identity enforced by two indexes:
--        - a UNIQUE index on (run_id, target_id, purpose) for the new shape (one
--          row per target/purpose pair within a run -- multiple ad hoc credentials
--          coexist safely, each addressed by its own (target, purpose));
--        - a PARTIAL UNIQUE index on (run_id) WHERE target_id IS NULL, preserving
--          #434's original "at most one legacy row per run" invariant for the
--          NULL/'_legacy' shape (a plain (run_id, target_id, purpose) unique index
--          would not by itself forbid two NULL-target rows for the same run --
--          Postgres treats NULLs as distinct in a multi-column unique index --
--          hence the dedicated partial index).
--      Crucially, EVERY row -- legacy or per-target/per-purpose -- still carries
--      run_id, so the unconditional completion-transaction delete
--      (`DELETE FROM run_secrets WHERE run_id = $1`,
--      JobQueueRepository.DeleteRunSecretIfPresentAsync, issue #434/#642's lesson
--      that this delete runs in EVERY terminal completion regardless of whether a
--      row exists) needs no change at all to keep covering both shapes: it was
--      never scoped to target/purpose and deletes every row for the run either
--      way. The same is true of DeleteExpiredAsync's sweep query, which already
--      operates on run_id-grouped rows.
--
--   4. No backfill, no rewrite of existing rows: an in-flight run's legacy row
--      (target_id NULL, purpose '_legacy', written by the pre-#586 one-row-per-run
--      StoreAsync) is untouched by this migration and continues to decrypt exactly
--      as it did -- IRunSecretStore's back-compat overloads (StoreAsync/DecryptAsync
--      with no target/purpose arguments) read and write precisely that shape. This
--      is the same "no backfill; the old shape keeps working forever" contract
--      migration 0044 used for job_credential_bindings.
--
--   5. Grants: unchanged. waypoint_compliance_runner and waypoint_download_runner
--      already hold table-level SELECT, DELETE, and UPDATE (expires_at) on
--      run_secrets (migrations 0025/0033/0040) -- table-level grants apply to every
--      column and every row shape automatically; adding target_id/purpose requires
--      no new GRANT statement. RunnerRoleGrantDriftTests' existing positive/negative
--      run_secrets cases are extended (not duplicated) to exercise the new columns
--      through the same real repository methods, proving the drift class (#556) has
--      not reopened for the widened shape.
ALTER TABLE run_secrets ADD COLUMN IF NOT EXISTS target_id UUID NULL REFERENCES targets (id) ON DELETE CASCADE;
ALTER TABLE run_secrets ADD COLUMN IF NOT EXISTS purpose TEXT NOT NULL DEFAULT '_legacy';

ALTER TABLE run_secrets DROP CONSTRAINT IF EXISTS run_secrets_pkey;
ALTER TABLE run_secrets ADD COLUMN IF NOT EXISTS id UUID NOT NULL DEFAULT gen_random_uuid();
ALTER TABLE run_secrets ADD CONSTRAINT run_secrets_pkey PRIMARY KEY (id);

ALTER TABLE run_secrets DROP CONSTRAINT IF EXISTS run_secrets_purpose_check;
ALTER TABLE run_secrets ADD CONSTRAINT run_secrets_purpose_check CHECK (
    purpose IN ('vsphere-api', 'vcsa-ssh', 'nsx-api', 'srg-ssh', '_legacy')
);

-- Both-or-neither: a row is either the legacy run-wide shape (target_id NULL,
-- purpose '_legacy') or a fully-scoped per-target/per-purpose row (target_id set,
-- purpose a real CredentialPurposes member) -- never a NULL target_id paired with a
-- real purpose or vice versa, which would silently fall into neither uniqueness
-- index below and defeat the whole re-keying.
ALTER TABLE run_secrets DROP CONSTRAINT IF EXISTS run_secrets_legacy_shape_check;
ALTER TABLE run_secrets ADD CONSTRAINT run_secrets_legacy_shape_check CHECK (
    (target_id IS NULL AND purpose = '_legacy') OR (target_id IS NOT NULL AND purpose <> '_legacy')
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_run_secrets_run_target_purpose ON run_secrets (run_id, target_id, purpose);
CREATE UNIQUE INDEX IF NOT EXISTS uq_run_secrets_run_legacy ON run_secrets (run_id) WHERE target_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_run_secrets_target_id ON run_secrets (target_id) WHERE target_id IS NOT NULL;

-- job_credential_bindings.is_run_secret ------------------------------------
-- Extends migration 0044's per-job purpose snapshot to represent an AD HOC
-- (run_secrets-backed) purpose alongside a saved-credential one, so a single
-- job's snapshot can name a mix of stored and ad hoc sources across its
-- purposes (e.g. a vsphere target scanned with a saved vsphere-api credential
-- but an ad hoc vcsa-ssh override). is_run_secret = true means "resolve this
-- purpose's secret from run_secrets keyed by (job's run_id, this row's own
-- target_id [carried on the job, not this table -- see ScanJobHandler's
-- resolution], purpose)", NOT from credential_id.
--
-- The CHECK below enforces only ONE direction -- is_run_secret = true implies
-- credential_id IS NULL (an ad hoc row is never insertable naming a stored
-- credential; there is no credential row to name) -- NOT the converse. A
-- stricter "is_run_secret = false implies credential_id IS NOT NULL" would
-- break #593's pre-existing terminal-history detach
-- (CredentialRepository.DeleteAsync), which legitimately NULLs a STORED row's
-- credential_id after snapshotting its attribution while leaving is_run_secret
-- false (it was never an ad hoc row) -- that UPDATE must keep succeeding
-- exactly as it did before this migration. This is what "the
-- job_credential_bindings rows for ad-hoc entries reference the run-secret
-- row, not a credential id" (issue #586) means concretely: no FK to
-- run_secrets is added here (its key is (run_id, target_id, purpose), which
-- this table + the owning job row already carry in full -- a redundant FK
-- would just be a second place the identity could drift), so the "reference"
-- is structural (the shared key), not a literal foreign key column.
ALTER TABLE job_credential_bindings ADD COLUMN IF NOT EXISTS is_run_secret BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE job_credential_bindings DROP CONSTRAINT IF EXISTS run_secrets_binding_shape_check;
ALTER TABLE job_credential_bindings ADD CONSTRAINT run_secrets_binding_shape_check CHECK (
    NOT (is_run_secret = true AND credential_id IS NOT NULL)
);
