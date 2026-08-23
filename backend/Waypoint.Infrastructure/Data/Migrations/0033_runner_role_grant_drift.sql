-- Issue #556: closes a live grant-drift bug against the least-privilege
-- waypoint_compliance_runner role (migration 0025), found on 2026-08-23 when a
-- fresh Compose stack's compliance runner actually claimed queued work for the
-- first time and hit `42501: permission denied` twice in a row -- proof that
-- 0025's grant matrix had already drifted from what the runner's own repository
-- code does, undetected because every prior repository test ran through the
-- fixture owner connection (full privilege), never the deployed runner role.
--
-- Three narrowly column-scoped gaps, all compliance-runner-only:
--
--   1. run_secrets: RunSecretStore.DecryptAsync's sliding-expiry write
--      (`UPDATE run_secrets SET expires_at = now() + $2 WHERE run_id = $1`,
--      issue #469) needs UPDATE on expires_at. 0025 granted only SELECT, DELETE --
--      reasoned as "ad hoc decrypt + same-transaction delete on completion", never
--      accounting for the sliding window landing after that comment was written.
--      Column-scoped to expires_at only: no other run_secrets column is ever
--      written by a runner (ciphertext/username/etc. are INSERT-once, API-only).
--
--   2. targets: TargetRepository.SetDiscoveryStatusAsync, driven by
--      DiscoverJobHandler on every discover job, needs
--      `UPDATE targets SET discovery_status = $2, last_refreshed = ... WHERE id = $1`.
--      0025 granted only SELECT on targets ("M2's site/target CRUD stays
--      API-only" -- true for the create/rename/connection-JSON/credential-id
--      surface TargetsController owns, but SetDiscoveryStatusAsync is a distinct,
--      narrower, runner-owned mutation the original comment did not carve out).
--      Column-scoped to discovery_status, last_refreshed only: name, connection,
--      credential_id, kind stay API-only exactly as 0025 intended.
--
--   3. config_docs, config_versions: ScanJobHandler's attest stage resolves
--      attestation config docs via ConfigDocRepository.FindWithLatestVersionAsync
--      (global/site/target layers, issue #266) before applying attestations to a
--      scan's HDF report. 0025 never granted either table to any runner role at
--      all -- not a narrowing bug like the two above, a total omission. Read-only:
--      ConfigDocRepository's own doc comment states runners never write these
--      tables (doc/version authoring is POST /config-docs, API-only), so SELECT
--      only, matching the existing sites/stigman_connections/credential_secrets
--      read-only precedent elsewhere in 0025.
--
-- download-runner audit: DownloadRepository/DepotArtifactRepository's SELECT,
-- INSERT, UPDATE column sets already cover every statement DownloadJobHandler/
-- CatalogIndexJobHandler issue (upsert-shaped INSERT ... ON CONFLICT DO UPDATE
-- needs whole-row SELECT+INSERT+UPDATE, already granted in 0025) -- no
-- download-runner drift found in this audit.
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

GRANT UPDATE (expires_at) ON run_secrets TO waypoint_compliance_runner;
GRANT UPDATE (discovery_status, last_refreshed) ON targets TO waypoint_compliance_runner;
GRANT SELECT ON config_docs, config_versions TO waypoint_compliance_runner;
