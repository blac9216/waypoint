-- Issue #434 (epic #433): replaces the process-memory-only
-- IEphemeralCredentialCache handoff (ADR-0011 personal tier) with encrypted,
-- run-scoped Postgres state so a dedicated compliance runner -- which does
-- not share memory with the API process (ADR-0013/0014) -- can decrypt an ad
-- hoc "my credentials" secret, and so an API restart between run creation and
-- job claim no longer forces credential re-entry.
--
-- Deliberately NOT a row in credential_secrets / credentials: ADR-0011's "no
-- personal rows, ever" rule is about the REUSABLE credential store, not about
-- persistence as such -- this table is a separate, run-scoped, terminal- and
-- expiry-bounded blob with its own envelope (same AES-256-GCM primitives,
-- ADR-0005) and its own lifecycle, never surfaced through any
-- credentials/* API. One row per run (a run's ad hoc secret is the same
-- value for every target job in that run's scan fan-out -- see
-- RunsController.CreateScanRunAsync), referenced by jobs via
-- jobs.run_id -- no separate job-level foreign key is needed because a job's
-- run_id already identifies which run_secrets row (if any) applies; jobs
-- that use it are marked with jobs.has_run_secret so a handler need not probe
-- run_secrets speculatively for every job.
CREATE TABLE IF NOT EXISTS run_secrets (
    run_id UUID PRIMARY KEY REFERENCES runs (id) ON DELETE CASCADE,
    username TEXT NOT NULL,
    ciphertext BYTEA NOT NULL,
    data_key_wrapped BYTEA NOT NULL,
    master_key_id TEXT NOT NULL,
    algorithm TEXT NOT NULL DEFAULT 'AES-256-GCM',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Expiry bound for the cleanup sweep (RunSecretCleanupHostedService): an
    -- abandoned run (crashed before any job claimed it, or a runner that
    -- never reports terminal) must not leave its secret in the database
    -- forever. Set at creation, not renewed by activity -- a long-running
    -- scan is expected to reach a terminal state (which deletes the row
    -- outright, see below) well inside this window; see
    -- RunSecretOptions.ExpiryFor the C# default.
    expires_at TIMESTAMPTZ NOT NULL
);

-- The cleanup sweep's query shape: "expired rows whose run is not still
-- actively running" -- see RunSecretCleanupHostedService's doc comment for
-- why the sweep also re-checks runs.state rather than trusting expires_at
-- alone.
CREATE INDEX IF NOT EXISTS idx_run_secrets_expires_at ON run_secrets (expires_at);

-- jobs.has_run_secret ------------------------------------------------------
-- Mirrors the pre-existing in-memory JobSpec.HasEphemeralCredential signal
-- (issue #276) as a persisted column: true when this job's ad hoc "my
-- credentials" secret lives in run_secrets under the job's own run_id, never
-- a stored credentials row. Mutually exclusive with credential_id, enforced
-- by jobs_credential_exclusivity_check below (same mutual exclusivity
-- RunsController.ValidateEphemeralCredentialRequest already enforces at the
-- API layer -- this is the schema-level backstop).
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS has_run_secret BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_credential_exclusivity_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_credential_exclusivity_check
    CHECK (NOT (has_run_secret AND credential_id IS NOT NULL));

-- secret.* audit event types (secret.run_registered / secret.run_decrypted /
-- secret.run_deleted / secret.run_expired) join the existing
-- secret.stored / secret.decrypted / secret.deleted vocabulary
-- CredentialSecretStore already writes -- audit_log.event_type has no CHECK
-- constraint (see 0001), so no schema change is needed for the values
-- themselves; this comment exists so the vocabulary is discoverable from the
-- migration history the way 0020's header documents job_events'.
