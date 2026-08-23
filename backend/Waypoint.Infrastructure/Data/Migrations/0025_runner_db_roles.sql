-- Issue #442 (ADR-0014 SS7, ADR-0013 SS1): least-privilege database grants for the
-- two dedicated runner services now wired into deploy/docker-compose.yml, distinct
-- from the control-plane (Waypoint.Api) connection this migration itself runs
-- under. The roles this migration grants to (waypoint_compliance_runner,
-- waypoint_download_runner) are created separately by
-- deploy/postgres/initdb/01-runner-roles.sh, which runs once against a fresh
-- Postgres data directory and is the only place that ever sees their passwords
-- (CLAUDE.md: no secret belongs in a committed .sql file). This migration only
-- grants privileges on already-existing objects to already-existing role names --
-- if the roles do not exist yet (an operator applying this migration against an
-- pre-#442 pgdata volume without also having re-run initdb), each GRANT below
-- fails loudly (undefined role) rather than silently granting nothing, which is
-- the right failure mode: a missing role is a deployment ordering bug, not a
-- state this migration should paper over.
--
-- What each role gets and why (mirrors what backend/Waypoint.Infrastructure's
-- composition root actually touches for each runner's AddWaypointInfrastructure/
-- ExecutionOnly wiring -- see JobQueueRepository, CredentialRepository,
-- CredentialSecretStore, RunSecretStore, and each domain repository):
--
--   Both runners (queue/lease/event/credential-decrypt mechanics, ADR-0014 SS1/SS2/
--   SS3/SS6, shared by JobDispatcherHostedService regardless of domain):
--     jobs             SELECT, INSERT, UPDATE  -- claim, lease heartbeat, stage/
--                                                  terminal transitions, cancel flag
--     job_events        SELECT, INSERT          -- append-only event log; SELECT is
--                                                  needed for the seq-assignment
--                                                  trigger's own reads, never an
--                                                  application-level read path here
--     runs              SELECT, UPDATE          -- run state/paused/blocked transitions
--                                                  a runner's dispatcher performs
--                                                  in-line with job completion
--     credentials        SELECT, UPDATE          -- read the target credential to
--                                                  decrypt (ADR-0014 SS6), and the
--                                                  consecutive-auth-failure queue-halt
--                                                  columns (queue_halted,
--                                                  queue_halted_reason,
--                                                  queue_halted_at, health) the
--                                                  dispatcher writes on repeated auth
--                                                  failure -- never name/owner/rotate,
--                                                  and never DELETE (API-only,
--                                                  CredentialsController)
--     credential_secrets SELECT                  -- ciphertext read for local decrypt;
--                                                  runners never write this table --
--                                                  writes are the API's
--                                                  POST/PUT /credentials path only
--     audit_log          INSERT                  -- decrypt-audit trail (ADR-0014 SS6
--                                                  "records the decrypt audit before
--                                                  use") and job/run audit events;
--                                                  append-only by design (0020's
--                                                  trigger already blocks UPDATE/
--                                                  DELETE for every role, but no
--                                                  runner gets those grants either)
--
--   compliance-runner only (discover/credential-test/scan -- ADR-0014 SS7 "compliance
--   runner: compliance-content read access and scan-artifact write access" is a
--   filesystem-mount statement; the matching DB-side statement is these tables):
--     sites, targets       SELECT                -- discover/scan read target
--                                                    inventory; M2's site/target CRUD
--                                                    stays API-only (SiteRepository/
--                                                    TargetRepository writes are only
--                                                    ever called from
--                                                    Waypoint.Api.Controllers)
--                                                  -- CORRECTION (migration 0033, issue
--                                                    #556): targets also gets
--                                                    UPDATE (discovery_status,
--                                                    last_refreshed) -- SELECT alone
--                                                    never covered
--                                                    TargetRepository.SetDiscoveryStatusAsync,
--                                                    which DiscoverJobHandler calls on
--                                                    every discover job. The API-only
--                                                    claim above still holds for every
--                                                    other targets column (name,
--                                                    connection, credential_id, kind).
--     inventory_items       SELECT, INSERT, UPDATE, DELETE
--                                                  -- InventoryRepository's discover-job
--                                                    upsert/replace-per-target shape
--                                                    needs all four; this is the one
--                                                    domain table a runner fully owns
--     stigman_connections   SELECT                -- ScanUploadCoordinator reads the
--                                                    singleton connection row; writing
--                                                    it is an API-only admin action
--                                                    (StigManagerController)
--     config_docs, config_versions SELECT         -- CORRECTION (migration 0033, issue
--                                                    #556): omitted entirely here --
--                                                    ScanJobHandler's attest stage
--                                                    resolves attestation config docs
--                                                    via ConfigDocRepository before
--                                                    applying them to a scan's HDF
--                                                    report (issue #266's global/
--                                                    site/target layer resolution).
--                                                    Read-only: doc/version authoring
--                                                    is POST /config-docs, API-only.
--     attestation_snapshots SELECT, INSERT        -- scan/convert stage persists the
--                                                    at-scan-time attestations-applied
--                                                    ledger; never updated or deleted
--                                                    once written
--     run_secrets            SELECT, DELETE       -- ad hoc "my credentials" decrypt
--                                                    (ADR-0011 personal tier) and the
--                                                    same-transaction delete on job
--                                                    completion (JobQueueRepository);
--                                                    INSERT stays API-only
--                                                    (RunsController registers the
--                                                    secret at run-creation time,
--                                                    before any job is claimed)
--                                                  -- CORRECTION (migration 0033, issue
--                                                    #556): also gets
--                                                    UPDATE (expires_at) -- SELECT,
--                                                    DELETE alone never covered
--                                                    RunSecretStore.DecryptAsync's
--                                                    sliding-expiry write (issue #469),
--                                                    which lands in every successful
--                                                    decrypt, not just cleanup/delete.
--
--   download-runner only (catalog-index/download -- ADR-0014 SS7 "download runner:
--   managed tool/depot/content write access" is the filesystem-mount statement; the
--   matching DB-side statement is these tables):
--     depot_artifacts SELECT, INSERT, UPDATE      -- catalog-index upserts the depot
--                                                     catalog; download updates
--                                                     status/sha256 on verify
--     downloads        SELECT, INSERT, UPDATE     -- download-job progress/state
--                                                     machine (queued -> downloading
--                                                     -> verifying -> verified/failed)
--
-- Deliberately withheld from BOTH runner roles: appliance_state (API/Settings-only
-- singleton), schema_migrations (migration bookkeeping -- only the migrator
-- connection touches it), credential_secrets INSERT/UPDATE/DELETE (only the API
-- writes secrets, ADR-0005 "write-only through the API"), and any DDL privilege
-- (CREATE/ALTER/DROP) on any object -- both roles are NOSUPERUSER NOCREATEDB
-- NOCREATEROLE (enforced in 01-runner-roles.sh) and this migration never grants
-- schema-level CREATE.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

-- Shared queue/lease/event/credential-decrypt surface -----------------------------
GRANT SELECT, INSERT, UPDATE ON jobs TO waypoint_compliance_runner, waypoint_download_runner;
GRANT SELECT, INSERT ON job_events TO waypoint_compliance_runner, waypoint_download_runner;
GRANT SELECT, UPDATE ON runs TO waypoint_compliance_runner, waypoint_download_runner;
GRANT SELECT (id, name, credential_type, owner, health, rotated_at, created_by, created_at, updated_at, sudo_enabled, username, queue_halted, queue_halted_reason, queue_halted_at),
      UPDATE (queue_halted, queue_halted_reason, queue_halted_at, health)
    ON credentials TO waypoint_compliance_runner, waypoint_download_runner;
GRANT SELECT ON credential_secrets TO waypoint_compliance_runner, waypoint_download_runner;
GRANT INSERT ON audit_log TO waypoint_compliance_runner, waypoint_download_runner;

-- job_events.seq is populated by trg_job_events_assign_seq via a sequence, not a
-- client-supplied value, but the trigger runs AS the inserting role -- it still
-- needs USAGE on the backing sequence.
GRANT USAGE ON SEQUENCE job_events_seq_seq TO waypoint_compliance_runner, waypoint_download_runner;

-- compliance-runner domain surface -------------------------------------------------
GRANT SELECT ON sites, targets TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE, DELETE ON inventory_items TO waypoint_compliance_runner;
GRANT SELECT ON stigman_connections TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON attestation_snapshots TO waypoint_compliance_runner;
GRANT SELECT, DELETE ON run_secrets TO waypoint_compliance_runner;

-- download-runner domain surface ---------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON depot_artifacts TO waypoint_download_runner;
GRANT SELECT, INSERT, UPDATE ON downloads TO waypoint_download_runner;
