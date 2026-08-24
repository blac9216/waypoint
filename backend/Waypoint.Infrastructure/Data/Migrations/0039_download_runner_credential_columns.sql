-- Issue #622: closes a third instance of the #556 grant-drift class, this time
-- against waypoint_download_runner. Live end-to-end validation of epic #558
-- (Workflow B, "run catalog-index against the local depot") hit
-- `permission denied for table credentials` (42501) from
-- CredentialRepository.FindByTypeAsync, called by CatalogIndexJobHandler to resolve
-- the stored depot-token credential before indexing.
--
-- Root cause: migration 0025's shared column-scoped SELECT grant on credentials
-- (both runner roles) enumerates id, name, credential_type, owner, health,
-- rotated_at, created_by, created_at, updated_at, sudo_enabled, username,
-- queue_halted, queue_halted_reason, queue_halted_at. Migration 0034 added
-- credentials.last_tested_at/expires_at to CredentialRepository.ProjectionSql (the
-- query FindByTypeAsync and GetAsync both build on), and migration 0034 (issue #560)
-- extended the runner SELECT/UPDATE grant for those two columns -- but only to
-- waypoint_compliance_runner, because at that time the only known caller was
-- CredentialTestJobHandler (compliance-only, JobCapabilities.Compliance). Nobody
-- re-checked FindByTypeAsync's callers, which share the same ProjectionSql and are
-- download-runner code: CatalogIndexJobHandler (catalog-index) and
-- ManagedToolInstallJobHandler's depot-fetch path (tool-install, ADR-0015 source
-- "depot"). Both now trip 42501 the same way #556's compliance examples did.
--
-- Audited surface for waypoint_download_runner (every credentials-adjacent call on
-- its two job types, catalog-index and tool-install):
--   * CredentialRepository.FindByTypeAsync -> ProjectionSql, SELECT only, needs
--     last_tested_at, expires_at added (this migration).
--   * ICredentialSecretStore.DecryptAsync -> SELECT on credential_secrets (0025,
--     already granted) + INSERT on audit_log (0025, already granted). No other table.
--   * Neither handler calls CredentialRepository.MarkTestOutcomeAsync,
--     StampRotatedAsync, RenameAsync, UpdateSudoAsync, UpdateUsernameAsync, or
--     DeleteAsync -- the download-runner path is read-only against credentials.
--     last_tested_at is therefore SELECT-only for waypoint_download_runner; no
--     UPDATE grant is added, unlike 0035's compliance-runner grant (that handler's
--     MarkTestOutcomeAsync write has no download-runner equivalent).
--
-- Least-privilege boundary preserved: expires_at and last_tested_at stay read-only
-- for waypoint_download_runner (never fabricated, never runner-written on this
-- path), and every other credentials column (name, owner, credential_type, etc.)
-- remains outside both the SELECT and UPDATE grant exactly as 0025 intended.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT (last_tested_at, expires_at) ON credentials TO waypoint_download_runner;
