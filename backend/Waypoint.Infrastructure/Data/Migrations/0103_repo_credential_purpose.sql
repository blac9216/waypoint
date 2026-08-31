-- Issue #1517 (epic #1180, split from design record #1043; slot pre-assigned
-- 2026-08-30, inside the 0082-0106 numbering gap concurrently in-flight sibling
-- issues reserve -- NpgsqlSchemaMigrator orders by filename, not contiguous
-- numbers, so a gap here is expected, not a collision): adds the
-- 'repo-basic-auth' credential_type (Waypoint-managed Basic-auth
-- pair for the nginx repo path-space #1502 stood up) and repo_credential_bindings
-- (which repo STORE -- depot/umds/photon/vmtools/vks/content-libraries -- a
-- repo-basic-auth credential authenticates for).
--
-- Scope note (issue #1517's own "Risks" section): this migration is credential
-- CRUD/rotation + the store-binding record ONLY. It does not touch nginx
-- (auth_request/htpasswd wiring is the sibling B child, #1510, blocked on #1608)
-- and does not touch the UI (#1525). No new runner grant either -- see the closing
-- comment below.
--
-- Two things, mirroring two established precedents exactly:
--
--   1. Widens credentials_credential_type_check (migration 0022, widened again by
--      0047's DROP/ADD idiom) to admit 'repo-basic-auth'. This is a CredentialTypes
--      value (Waypoint.Core.Secrets.CredentialTypes.RepoBasicAuth), NOT a
--      CredentialPurposes value (that closed set is ADR-0021's target-kind x
--      operation matrix -- catalog_credential_requirements/target_credential_bindings
--      -- an unrelated bounded context repo stores do not participate in). A
--      repo-basic-auth credential rides the EXISTING credentials table/API
--      (CredentialsController POST/PUT/DELETE/test) unmodified: username + secret,
--      same write-only contract, same rotation path (PUT with a new secret stamps
--      rotated_at) every other credential type already gets -- issue #1517's AC
--      "rotation reuses the existing credential rotation machinery -- no new
--      rotation mechanism" is satisfied by adding NO new rotation code at all.
--
--   2. Creates repo_credential_bindings: one row per repo STORE (not per target --
--      there is no DB-level "target" row for a repo store; #1502 stood up the
--      nginx location tree, not a table), mirroring target_credential_bindings'
--      (migration 0043) shape as closely as issue #1517 asks a reviewer to
--      recognize: same id/timestamps/updated_at-trigger shape, same "credential_id
--      is NOT NULL, RESTRICT (implicit default), a binding's whole reason to exist
--      is naming a credential" rule, same "no ON DELETE action -- a credential
--      referenced by a live binding is not silently deletable out from under it"
--      choice CredentialRepository.DeleteAsync's blocker model already enforces for
--      target_credential_bindings (issue #1517 AC3: extended below, application
--      code not a DB trigger, as its own RepoCredentialBindings blocking category --
--      the identical shape #584/migration 0043 established for
--      TargetCredentialBindings, not a new pattern).
--
--      UNIQUE on store ALONE (not (store, purpose) like 0043's (target_id,
--      purpose)): there is exactly one purpose in this bounded context
--      ('repo-basic-auth' Basic-auth serving), so a store binds at most one
--      credential at a time -- setting a new one replaces (UPSERTs) the prior
--      binding, same override semantics ADR-0021 SS4 established for
--      target_credential_bindings.
--
--      store is CHECK-constrained to the six repo stores deploy/nginx/conf.d/
--      default.conf's location blocks actually serve today (#1502, merged):
--      depot, umds, photon, vmtools, vks, content-libraries. Kept in lockstep
--      with backend/Waypoint.Core/Secrets/RepoStores.cs by the
--      ParseCheckInList drift guard in
--      RepoCredentialBindingConstraintDriftTests
--      (RepoStoresAll_EqualsRepoCredentialBindingsStoreCheckConstraintValueSet),
--      the same convention targets_kind_check/TargetKinds and
--      target_credential_bindings_purpose_check/CredentialPurposes already use
--      (SchemaMigrationTests.Migration0050_/0051_CheckConstraintValueList(s)_
--      MatchTheCSharpClosedVocabulary). The widened credentials_credential_type_check
--      above is kept in lockstep with CredentialTypes.cs the same way, by
--      RepoCredentialBindingConstraintDriftTests'
--      CredentialTypesAll_EqualsCredentialsCredentialTypeCheckConstraintValueSet
--      (which parses the LAST declaration across all migrations, since this
--      constraint has been widened twice via the DROP/ADD idiom).
--
-- No new runner grant: this table has exactly one consumer today -- the API
-- process (RepoCredentialsController, via RepoCredentialBindingRepository) --
-- same API-process-owned posture as 0059/0078 (0100/0107's "correct rationale" is
-- the honest form of this: withholding a grant is right BECAUSE there is no
-- runner-claimed consumer yet, not because a future consumer is itself
-- API-process-owned -- issue #1406 review round 1 finding 5 is the landed lesson
-- this comment is careful not to repeat). The sibling B child (#1510, blocked on
-- #1608) enforces Basic auth at nginx via auth_request against the API's own
-- endpoints (or an API-generated artifact) -- neither shape is a
-- waypoint_compliance_runner/waypoint_download_runner claimed-job consumer of
-- this table, so #1510 does not owe this migration a grant either; if that
-- changes when #1510 lands as filed, #1510 ships its own GRANT migration
-- (0100/#1484 precedent), the same rule RunnerRoleGrantDriftTests' negative-
-- direction cases below prove now: both runner roles are denied even SELECT.
ALTER TABLE credentials
    DROP CONSTRAINT IF EXISTS credentials_credential_type_check;

ALTER TABLE credentials
    ADD CONSTRAINT credentials_credential_type_check
    CHECK (credential_type IN (
        'vcenter', 'nsx', 'ssh', 'token', 'depot-token', 'depot-activation-code',
        'legacy-download-token', 'repo-basic-auth'
    ));

CREATE TABLE IF NOT EXISTS repo_credential_bindings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    store TEXT NOT NULL,
    credential_id UUID NOT NULL REFERENCES credentials (id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT repo_credential_bindings_store_check CHECK (
        store IN ('depot', 'umds', 'photon', 'vmtools', 'vks', 'content-libraries')
    ),
    CONSTRAINT repo_credential_bindings_store_key UNIQUE (store)
);

CREATE INDEX IF NOT EXISTS idx_repo_credential_bindings_credential_id ON repo_credential_bindings (credential_id);

CREATE OR REPLACE TRIGGER trg_repo_credential_bindings_updated_at
    BEFORE UPDATE ON repo_credential_bindings
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
