-- Issue #1403 (epic #1181), split from the design record #1161: the OCI bundle store
-- schema + push-target consumer model. Model-only -- no acquisition (#1413) or push
-- (#1441) logic ships here, just the shared shapes both build against.
--
-- MIGRATION SLOT: pre-assigned 0118 on 2026-08-30 while several sibling migrations
-- (slots 0099/0100/0107/0117) were in flight on other branches. At the time this file
-- was authored the highest migration present on this branch's base was 0081 -- the
-- gap between 0081 and 0118 is expected and is NOT a numbering collision: those slots
-- belong to work not yet merged to main. NpgsqlSchemaMigrator orders migrations by
-- filename (ordinal string compare), not by contiguous numbering, so the gap closes
-- itself as each sibling lands and is harmless in the meantime. If a collision is
-- discovered at merge time, whoever merges second renumbers (this file plus
-- SchemaMigrationTests.ExpectedMigrationCount), per the convention migration 0037
-- already documents.
--
-- oci_bundles -------------------------------------------------------------------------
-- One staged OCI image bundle: an imgpkg-shaped tar acquired to local disk (#1413,
-- not yet built) and its computed depot-registry destination, awaiting an
-- operator-triggered push (#1441, not yet built). See OciBundle.cs / #1157's findings
-- for the vendor context this schema encodes.
CREATE TABLE IF NOT EXISTS oci_bundles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    component_key TEXT NOT NULL,
    source_version TEXT NOT NULL,
    target_repo_path TEXT NOT NULL,
    tar_file_path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'staged',
    staged_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    push_failure_reason TEXT NULL,
    CONSTRAINT oci_bundles_status_check CHECK (status IN ('staged', 'pushed', 'push_failed'))
);

COMMENT ON TABLE oci_bundles IS
    'Issue #1403: one staged OCI image bundle tar (imgpkg-shaped) and its computed depot-registry destination, awaiting an operator-triggered push. Acquisition is #1413; push execution is #1441 -- this table only tracks the local staged artifact.';
COMMENT ON COLUMN oci_bundles.component_key IS
    'The catalog component this bundle carries, e.g. SUPERVISOR_SERVICE_HARBOR, VKR (#1157''s taxonomy). Matches a key in OciBundleComponentRepoPaths when the repo-path map has an entry for it.';
COMMENT ON COLUMN oci_bundles.source_version IS
    'The component version/ref this bundle was acquired at.';
COMMENT ON COLUMN oci_bundles.target_repo_path IS
    'The computed depot-registry repo path this bundle pushes to (OciBundleComponentRepoPaths), e.g. /supervisor-service-harbor/ga.';
COMMENT ON COLUMN oci_bundles.tar_file_path IS
    'Local filesystem path of the staged imgpkg-shaped tar on the artifact store volume.';
COMMENT ON COLUMN oci_bundles.sha256 IS
    'SHA-256 of the staged tar''s bytes, recorded at acquisition time.';
COMMENT ON COLUMN oci_bundles.status IS
    'staged | pushed | push_failed -- see OciBundleStatuses. Never any other value.';
COMMENT ON COLUMN oci_bundles.push_failure_reason IS
    'Set only when status = push_failed -- the last push attempt''s failure reason. NULL otherwise.';

CREATE INDEX IF NOT EXISTS idx_oci_bundles_component_key ON oci_bundles (component_key);
CREATE INDEX IF NOT EXISTS idx_oci_bundles_status ON oci_bundles (status);

-- push_target_consumers ----------------------------------------------------------------
-- A configured push target: the operator's own depot-registry (Software Depot /
-- Harbor / Bootstrap Registry Appliance) that a staged oci_bundles row is pushed
-- into. write_mode_enabled is a placeholder column -- always false until #1441 builds
-- the enable-push-disable bracket that actually drives it (#1157: depot OCI pushes
-- are unauthenticated while enabled, so the vendor's own guidance is to flip the
-- toggle only to bracket a single push).
CREATE TABLE IF NOT EXISTS push_target_consumers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    registry_fqdn TEXT NOT NULL,
    write_mode_enabled BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE push_target_consumers IS
    'Issue #1403: a configured push target for the OCI bundle store -- the operator''s own depot-registry consumer that a staged oci_bundles row pushes into. #1441 builds the push operation and the write-mode enable/disable bracket this row''s write_mode_enabled placeholder anticipates.';
COMMENT ON COLUMN push_target_consumers.name IS
    'Operator-facing label for this push target.';
COMMENT ON COLUMN push_target_consumers.registry_fqdn IS
    'The depot-registry FQDN imgpkg copy --to-repo targets.';
COMMENT ON COLUMN push_target_consumers.write_mode_enabled IS
    'Placeholder safety flag mirroring the registry''s own unauthenticated-write toggle. Always false until #1441''s push operation drives it.';

CREATE OR REPLACE TRIGGER trg_push_target_consumers_updated_at
    BEFORE UPDATE ON push_target_consumers
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Runner grants: deliberately NONE in this migration. This child (#1403) is model-only
-- -- no runner process reads or writes either table yet. #1413 (acquisition, writes
-- oci_bundles) and #1441 (push, reads oci_bundles / reads+writes push_target_consumers)
-- each add the GRANTs their own writer needs, alongside the runner-role-connects test
-- proving them (this repo's #556 convention) -- granting now, with no consumer, would
-- be untested surface with no test able to exercise it honestly.
