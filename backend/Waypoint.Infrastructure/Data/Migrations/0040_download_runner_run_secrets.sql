-- Issue #642: closes a fourth instance of the #556 grant-drift class, and the root
-- cause of #640. Live end-to-end validation of epic #558 (Workflow B) found that
-- EVERY download-runner job -- not just tool-install -- fails its terminal
-- completion / lease-recovery path with `42501: permission denied for table
-- run_secrets`, then loops forever via lease recovery (producing #640's duplicate
-- managed_tool_installs ledger rows along the way).
--
-- Root cause: migration 0025 granted `SELECT, DELETE ON run_secrets` to
-- waypoint_compliance_runner only (and migration 0033 added a column-scoped
-- `UPDATE (expires_at)` for the same role, issue #556). Both grants were reasoned
-- about as "ad hoc 'my credentials' decrypt (ADR-0011 personal tier)" -- a
-- compliance-runner-only feature at the time -- but the code paths that actually
-- touch run_secrets are NOT compliance-domain-specific: they live in the *shared*
-- job-engine surface both runner roles execute identically:
--
--   * JobQueueRepository.TryCompleteRunAsync -> DeleteRunSecretIfPresentAsync
--     (`DELETE FROM run_secrets WHERE run_id = $1`) runs inside EVERY
--     AdvanceStateAsync call that lands a run's last job on a terminal state, and
--     inside EVERY RecoverExpiredLeasesAsync sweep that does the same via lease
--     exhaustion -- regardless of which runner claimed the job. A no-op delete
--     (zero rows, the common case for a job with no run secret) still executes the
--     DELETE statement and requires the grant even though it affects nothing.
--   * RunSecretStore.DecryptAsync's sliding-expiry write
--     (`UPDATE run_secrets SET expires_at = ...`, issue #469) and its SELECT read
--     are reachable by any job with has_run_secret = true, regardless of runner
--     domain -- ADR-0011's personal-credential tier was never restricted to
--     compliance jobs at the persistence layer, only under-exercised by download
--     jobs in practice until #558's live validation.
--
-- Because DeleteRunSecretIfPresentAsync runs unconditionally inside the shared
-- completion transaction, a download-runner job cannot reach ANY terminal state
-- (done, failed, uploaded, auth-failed, cancelled) without this grant -- it is not
-- limited to jobs that actually registered a run secret. This is the direct cause
-- of #640: ManagedToolInstallJobHandler's tool-install job runs to completion,
-- returns Failed, AdvanceStateAsync's terminal UPDATE on jobs commits, but the
-- in-transaction TryCompleteRunAsync -> DeleteRunSecretIfPresentAsync throws 42501,
-- rolling back the ENTIRE transaction (including the jobs UPDATE) -- so the job
-- silently stays 'running' with no terminal job.state event, then lease recovery
-- requeues it, the handler re-executes, and a duplicate managed_tool_installs row
-- lands each cycle.
--
-- Audited surface: exhaustively re-walked every statement JobDispatcherHostedService
-- (per-job completion) and LeaseRecoveryHostedService (RecoverExpiredLeasesAsync)
-- execute in JobQueueRepository, for both runner domains -- jobs, job_events, runs,
-- credentials, credential_secrets, audit_log, run_secrets, capacity_pool/
-- capacity_leases (0036). All of those already carry matching grants (0025/0033/
-- 0036) for both roles except run_secrets, which is the only gap: this migration
-- closes it by mirroring 0025 + 0033's compliance-runner run_secrets grant for
-- waypoint_download_runner, same three privileges (SELECT, DELETE, and column-scoped
-- UPDATE (expires_at)), same least-privilege shape -- INSERT stays API-only
-- (RunsController registers the secret at run-creation time) for both roles.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, DELETE ON run_secrets TO waypoint_download_runner;
GRANT UPDATE (expires_at) ON run_secrets TO waypoint_download_runner;
