-- Issue #690 (epic #667): separates the single, ambiguously-named 'depot-token'
-- credential type into two non-interchangeable Broadcom depot credentials --
-- 'depot-activation-code' (VCF 9.1: authenticates vcf-download-tool metadata/binary
-- commands, paired to a Software Depot ID) and 'legacy-download-token' (substituted
-- into dl.broadcom.com URL templates for UMDS/older flows) -- mirroring the sibling
-- vcf-docker-download reference, which provisions softwareDepotActivationCode.txt and
-- downloadToken.txt as two separate files for two separate consumers. Broadcom's own
-- guidance: "Download Token has been replaced by Activation Code" for VCF 9.1
-- (knowledge.broadcom.com/external/article/443647).
--
-- NON-DESTRUCTIVE migration path (issue #690 AC, explicitly required by the issue
-- body: "do not guess its meaning; require the operator to classify/re-enter it, or
-- retain it as an explicitly legacy alias with visible migration status"):
--
--   - This migration does NOT rewrite, reclassify, or delete any existing
--     'depot-token' row. No UPDATE statement touches the credentials table. An
--     operator's pre-#690 depot-token credential keeps its exact stored ciphertext,
--     id, and 'depot-token' credential_type -- it is never silently relabeled as
--     either new type, because this migration (and no code path anywhere in this
--     PR) has any way to know which of the two purposes the operator originally
--     intended.
--   - 'depot-token' is RETAINED in the CHECK constraint (not dropped) precisely so
--     that pre-existing row remains valid and visible, not orphaned by a constraint
--     violation on the next unrelated write to that row. Waypoint.Core.Secrets.
--     CredentialTypes.DepotToken is documented as deprecated: no handler resolves it
--     for either new purpose (CatalogIndexJobHandler resolves no credential at all
--     post-#690; ManagedToolInstallJobHandler's depot-fetch path resolves only
--     'depot-activation-code'), so a lingering 'depot-token' row is inert but
--     visible -- DownloadsController.GetReadiness and DepotTokensTab.tsx surface it
--     as a distinct, clearly-labeled legacy/deprecated credential the operator must
--     explicitly re-enter under one of the two new types to restore depot
--     functionality. This is the "visible migration status" the issue requires:
--     status is visible in the UI/API, not encoded as a new column, because the
--     fact needing surfacing ("this credential's real purpose is unknown") has no
--     migration-computable answer to store.
ALTER TABLE credentials
    DROP CONSTRAINT IF EXISTS credentials_credential_type_check;

ALTER TABLE credentials
    ADD CONSTRAINT credentials_credential_type_check
    CHECK (credential_type IN ('vcenter', 'nsx', 'ssh', 'token', 'depot-token', 'depot-activation-code', 'legacy-download-token'));
