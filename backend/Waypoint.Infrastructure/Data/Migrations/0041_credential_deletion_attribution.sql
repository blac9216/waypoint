-- Issue #593 (epic #577): today runs.credential_id/jobs.credential_id are plain
-- RESTRICT FKs (CredentialRepository.DeleteAsync's comment) -- ANY historical
-- reference, terminal or not, blocks credential deletion with 409
-- credential_in_use. That is wrong for terminal work: a completed/aborted run or
-- a job in one of JobTerminalStates is history, not active use of the secret --
-- it should not force an operator to erase evidence just to rotate/remove a
-- credential (epic #577's motivation).
--
-- This migration does two things:
--   1. Adds three nullable, non-secret attribution snapshot columns to runs and
--      jobs -- credential_name, credential_type, credential_username -- captured
--      at the moment a terminal-only reference is detached (CredentialRepository
--      .DeleteAsync, PR for #593). NEVER populated with secret material: no
--      password/token/ciphertext/wrapped-key field exists on credentials to begin
--      with, so there is nothing to accidentally snapshot here (epic #577 Risks).
--      Deliberately named/shaped to describe the credential ITSELF (display
--      fields only), not the binding/purpose that used it -- epic #577's
--      trajectory log flags that #582 will later redesign run-scoped secret
--      storage and credential bindings, so this snapshot must stay
--      binding-agnostic to avoid rework.
--   2. Relaxes runs.credential_id and jobs.credential_id from the implicit
--      RESTRICT default to ON DELETE SET NULL -- the same "historical record
--      outlives its non-essential FK" precedent 0006 (audit_log) and 0032
--      (runs.schedule_id) already established. This alone does not change
--      deletion behavior: the repository still detaches terminal-only rows and
--      snapshots them INSIDE the same transaction as the DELETE (never relying on
--      the FK's ON DELETE action to fire on a still-active reference), and a
--      credential referenced by non-terminal work, a target, a schedule, or the
--      stigman_connections/global config is still rejected in application code
--      before the DELETE statement ever runs. SET NULL is a safety net for the
--      DELETE statement itself (matches migration 0006/0032's reasoning) and
--      documents intent, not the enforcement point.
--
-- No runner grant changes needed: migration 0025 already granted
-- "SELECT, UPDATE ON runs" and "SELECT, INSERT, UPDATE ON jobs" to both runner
-- roles as whole-table (not column-scoped) grants, so the three new columns are
-- already covered by the existing SELECT/UPDATE privilege shape. Runners never
-- write these columns in practice (only CredentialRepository.DeleteAsync does,
-- through the API's connection) -- RunnerRoleGrantDriftTests gets a companion
-- assertion that a runner-role UPDATE naming the run_id/job_id credential_id FK
-- still succeeds unchanged, proving this migration did not narrow anything.
ALTER TABLE runs
    ADD COLUMN IF NOT EXISTS credential_name TEXT NULL,
    ADD COLUMN IF NOT EXISTS credential_type TEXT NULL,
    ADD COLUMN IF NOT EXISTS credential_username TEXT NULL;

ALTER TABLE jobs
    ADD COLUMN IF NOT EXISTS credential_name TEXT NULL,
    ADD COLUMN IF NOT EXISTS credential_type TEXT NULL,
    ADD COLUMN IF NOT EXISTS credential_username TEXT NULL;

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_credential_id_fkey;

ALTER TABLE runs
    ADD CONSTRAINT runs_credential_id_fkey
    FOREIGN KEY (credential_id) REFERENCES credentials (id) ON DELETE SET NULL;

ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_credential_id_fkey;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_credential_id_fkey
    FOREIGN KEY (credential_id) REFERENCES credentials (id) ON DELETE SET NULL;
