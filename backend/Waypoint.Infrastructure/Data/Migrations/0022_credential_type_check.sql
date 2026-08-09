-- Issue #252 (concern:refactor, epic #13): 0010 deliberately left credential_type as
-- free TEXT at the database layer because pre-existing test fixtures across ~8 files
-- seeded placeholder types ('service' in generic job-engine/repository seeding,
-- 'depot-token' in epic #10's catalog-index slice) that a strict CHECK would have
-- broken. That has now been resolved:
--
--   - 'depot-token' turned out to be a genuine operational type, not a placeholder --
--     CatalogIndexJobHandler resolves it from the credential store via
--     ICredentialSecretStore.FindByTypeAsync(CatalogOptions.DepotTokenCredentialType)
--     to authenticate catalog-index runs to the Broadcom depot. It has been folded
--     into Waypoint.Core.Secrets.CredentialTypes.All (now 5 values) rather than
--     renamed away.
--   - 'service' had no production consumer anywhere -- every occurrence was generic
--     test-fixture seeding unrelated to type-specific behavior (sudo_enabled,
--     depot-token resolution, etc). Those ~8 files were updated to seed 'token'
--     instead, a real CredentialTypes value.
--
-- With every seeded fixture now inside the closed set, the DB CHECK this migration
-- adds mirrors CredentialTypes.All the same way 0009's targets_kind_check mirrors
-- TargetKinds.All -- defense-in-depth so a bug that bypasses CredentialsController's
-- API-layer validation (e.g. a future direct-INSERT code path, a migration-importer,
-- or a bad manual fixup) cannot leave a row with a type the app doesn't recognize.
ALTER TABLE credentials
    DROP CONSTRAINT IF EXISTS credentials_credential_type_check;

ALTER TABLE credentials
    ADD CONSTRAINT credentials_credential_type_check
    CHECK (credential_type IN ('vcenter', 'nsx', 'ssh', 'token', 'depot-token'));
