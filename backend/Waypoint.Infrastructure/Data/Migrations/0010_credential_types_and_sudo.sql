-- Issue #20 (epic #13, M2): extend the M1 minimal credential store (#8) to the full
-- typed model. docs/domain-model.md "Credential" names four credential types
-- (vCenter, NSX, SSH with optional sudo, token); 0001's comment on `credentials`
-- deliberately left `credential_type` as free TEXT pending this slice, and
-- `owner` free TEXT pending ADR-0011 -- ADR-0011 settled SHARED ONLY (no personal
-- credentials in v1), so `owner` gets a CHECK now rather than staying open-ended.
--
-- credential_type deliberately stays a DATABASE-level free TEXT column (validated
-- against the closed CredentialTypes set at the API layer only, in
-- CredentialsController -- the same split TargetsController uses for
-- kind-specific connection fields, not the DB-CHECK split TargetKinds/targets.kind
-- uses). A DB CHECK was tried first and reverted: `credentials` already carries
-- widespread pre-existing test fixtures seeded directly via
-- INSERT/CredentialRepository.CreateAsync with placeholder types ('service' in the
-- job-engine test suites, 'depot-token' in epic #10's catalog-index slice) that
-- predate this issue and are not part of its four user-facing types. A CHECK strict
-- enough for the real API surface would break that unrelated, already-merged
-- fixture population across ~8 test files -- out of proportion for this slice.
-- Revisit at the migration-importer or discovery-job slice, when it's clear whether
-- those placeholder types should be renamed instead.

-- ADR-0011: shared/service credentials only in v1 -- there is no personal-credential
-- row shape to allow for. The column stays (rather than being dropped) because
-- ADR-0011 explicitly describes a later passphrase-wrapped personal tier as a schema
-- extension, not a redesign; closing the CHECK to the one value it can hold today
-- keeps that door open without pretending the app supports it now.
ALTER TABLE credentials
    DROP CONSTRAINT IF EXISTS credentials_owner_check;

ALTER TABLE credentials
    ADD CONSTRAINT credentials_owner_check
    CHECK (owner = 'shared');

-- sudo_enabled: SSH-type-only optional flag (domain-model.md "SSH (with optional
-- sudo)"). Modeled as a plain boolean rather than a separate sudo-credential
-- reference -- the existing SRG/SSH transport (vmware-stig-docker) escalates with the
-- same account's sudo rights, not a second identity. Meaningless for non-ssh types;
-- left unconstrained by kind (API-layer validation, same split TargetsController uses
-- for kind-specific connection fields) rather than a CHECK that would need to know
-- about credential_type in the same expression per row.
ALTER TABLE credentials
    ADD COLUMN IF NOT EXISTS sudo_enabled BOOLEAN NOT NULL DEFAULT false;
