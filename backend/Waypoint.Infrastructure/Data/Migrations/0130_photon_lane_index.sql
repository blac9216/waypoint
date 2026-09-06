-- Issue #1509 (epic #1184, split from #1052's closing comment; migration slot 0108
-- pre-assigned 2026-08-30, RENUMBERED to 0129 at authoring time then to 0130 at
-- rebase time -- see MIGRATION SLOT note below). Establishes the Photon lane's
-- persisted index schema:
-- the RPM-repo index, the image-tree index, and an (unpopulated) subscription-config
-- table. Layout facts encoded here come from the ratified research on lane #1029
-- (issue #1026's research epic): version branch x repo variant x arch is the RPM
-- axis (`photon_<variant>_<version>_<arch>`, composite `photon_<version>_<arch>`),
-- and GA/RC/Beta x image kind is the image-tree axis (plain HTTP files, not yum
-- repos). This migration ships schema only -- see PhotonRepoDiscoveryJobHandler
-- (this same issue) for the one discovery job that writes photon_repo_index rows;
-- the image-discovery job and the subscription-config consumer are the documented
-- remainder, filed as a follow-up issue, so photon_image_index and
-- photon_subscription_config get no runner grant yet (0118's oci_bundles precedent:
-- granting write access with no consumer to exercise it is untested surface).
--
-- MIGRATION SLOT: this repo's numbered-migration sequence is a shared resource
-- across concurrently-developed issues; 0108 was pre-assigned on 2026-08-30 while
-- #1389 (0113) and #1392 (0109) were also in flight. By the time this issue was
-- actually implemented, origin/main already carried 0117/0118/0127/0128 (all merged
-- ahead of this branch) -- 0127 in particular DROPs and re-ADDs
-- jobs_job_type_check/runs_run_type_check wholesale, which would silently erase this
-- migration's new 'photon-repo-discovery' value if this file still ran before it in
-- filename order. Renumbered to 0129 (the next free slot after the highest migration
-- present on origin/main at authoring time) so this migration's ALTER TABLE runs
-- LAST and its addition survives. A pre-push rebase then found origin/main had ALSO
-- gained 0129 in the meantime (#1705's depot_artifacts_status_check widening,
-- unrelated column, no conflict with this migration's content) -- renumbered again to
-- 0130, the next free slot after that rebase. #1389's 0113 and #1392's 0109 had not
-- merged as of this second renumbering -- if either lands with a numbering or
-- constraint-redeclaration collision against 0130, whoever merges second renumbers
-- (this file plus SchemaMigrationTests.ExpectedMigrationCount), per the convention
-- migration 0037 already documents.
--
-- photon_repo_index --------------------------------------------------------------------
-- One row per (version, variant, arch) RPM repo directory this lane has ever
-- discovered, keyed on that triple (never row-deleted -- a repo that disappears
-- upstream is a discrepancy for a later reconciliation pass to surface, matching
-- this repo's alert-instead-of-drop convention, not this table's own job).
-- has_repodata=false marks a `photon_snapshots`-style directory with no
-- `repodata/repomd.xml` (research finding #5's open unknown, resolved here: index
-- it, do not error) -- repomd_revision/package_count stay NULL for such a row.
-- Both arches are indexed unconditionally (AC 2) -- arch is part of the identity,
-- never filtered by a subscription preset at discovery time.
CREATE TABLE IF NOT EXISTS photon_repo_index (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version TEXT NOT NULL,
    variant TEXT NOT NULL,
    arch TEXT NOT NULL,
    base_url TEXT NOT NULL,
    has_repodata BOOLEAN NOT NULL DEFAULT true,
    repomd_revision TEXT NULL,
    package_count INT NULL,
    discovered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT photon_repo_index_version_variant_arch_unique UNIQUE (version, variant, arch),
    CONSTRAINT photon_repo_index_variant_check CHECK (variant IN (
        'release', 'updates', 'extras', 'debuginfo', 'srpms', 'snapshots', 'composite'
    )),
    CONSTRAINT photon_repo_index_arch_check CHECK (arch IN ('x86_64', 'aarch64')),
    CONSTRAINT photon_repo_index_no_repodata_shape_check CHECK (
        has_repodata OR (repomd_revision IS NULL AND package_count IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_photon_repo_index_version ON photon_repo_index (version);

COMMENT ON TABLE photon_repo_index IS
    'Issue #1509: every Photon RPM repo directory (version x variant x arch) this lane has ever discovered, metadata only -- no package bytes are ever fetched to populate this table. has_repodata=false is the photon_snapshots-shaped no-repodata classification (research #1029 finding 5); repomd_revision is the repomd.xml <revision> change-detection token.';
COMMENT ON COLUMN photon_repo_index.variant IS
    'release | updates | extras | debuginfo | srpms | snapshots | composite -- the repo axis observed at every sampled Photon branch (research #1029 finding 1). composite is the bare photon_<version>_<arch> repo (no variant word in its directory name).';
COMMENT ON COLUMN photon_repo_index.has_repodata IS
    'false for a photon_snapshots-style directory with no repodata/repomd.xml -- indexed, never an error (research #1029 finding 5 / this issue AC 3).';
COMMENT ON COLUMN photon_repo_index.repomd_revision IS
    'repodata/repomd.xml''s <revision> value, the natural per-repo change-detection token (research #1029 finding 1). NULL when has_repodata is false.';
COMMENT ON COLUMN photon_repo_index.package_count IS
    'Count of <package> entries parsed from primary.xml.gz -- header/metadata only, never a download of the packages themselves. NULL when has_repodata is false.';

-- photon_image_index -------------------------------------------------------------------
-- One row per (version, channel, relative_path) image-tree file this lane has ever
-- discovered under a version's GA/RC/Beta trees (iso/ova/ovf/ami/azure/gce/rpi --
-- research #1029 finding 1's "non-repo siblings"), consolidated here to the
-- product-facing kinds this issue's Proposed Changes name: iso, ova, ovf,
-- cloud-image (ami/azure/gce), rpi. size_bytes is nullable and MUST NEVER be
-- populated from a rounded HTML listing column (issue #1170's guard, reused by
-- name below rather than re-implemented -- #1170 had not merged as of this
-- migration; PhotonRepoDiscoveryJobHandler's sibling image-discovery job, filed as
-- this issue's documented remainder, is what actually writes rows here).
CREATE TABLE IF NOT EXISTS photon_image_index (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version TEXT NOT NULL,
    channel TEXT NOT NULL,
    image_kind TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    size_bytes BIGINT NULL,
    etag TEXT NULL,
    discovered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT photon_image_index_version_channel_path_unique UNIQUE (version, channel, relative_path),
    CONSTRAINT photon_image_index_channel_check CHECK (channel IN ('GA', 'RC', 'Beta')),
    CONSTRAINT photon_image_index_kind_check CHECK (image_kind IN (
        'iso', 'ova', 'ovf', 'cloud-image', 'rpi'
    ))
);

CREATE INDEX IF NOT EXISTS idx_photon_image_index_version ON photon_image_index (version);

COMMENT ON TABLE photon_image_index IS
    'Issue #1509: every Photon installer/appliance image-tree file this lane has ever discovered per version/channel, metadata only. Written by the image-discovery job (this issue''s documented remainder) -- schema lands now so every other Photon-lane child has a stable shape to read/write against.';
COMMENT ON COLUMN photon_image_index.size_bytes IS
    'NEVER sourced from a rounded HTML directory-listing column (issue #1170''s guard) -- only from a real byte count (storage-API size or HEAD Content-Length). NULL when neither is available.';

-- photon_subscription_config -------------------------------------------------------------
-- Repo/image selectors (which version/variant/arch/channel/kind combinations an
-- operator has actually subscribed to sync) as a preset -- unpopulated by this
-- issue (greenfield schema only; the sync child that reads this table is a
-- separate, not-yet-filed issue). arches defaults to x86_64-only opt-in (research
-- #1029 finding 1's "no hardcoded major versions... let the preset select" design
-- implication) -- discovery itself never consults this table (AC 2: both arches
-- indexed regardless of preset).
CREATE TABLE IF NOT EXISTS photon_subscription_config (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    preset_name TEXT NOT NULL,
    repo_selectors JSONB NOT NULL DEFAULT '[]'::jsonb,
    image_selectors JSONB NOT NULL DEFAULT '[]'::jsonb,
    arches JSONB NOT NULL DEFAULT '["x86_64"]'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT photon_subscription_config_preset_name_unique UNIQUE (preset_name)
);

CREATE OR REPLACE TRIGGER trg_photon_subscription_config_updated_at
    BEFORE UPDATE ON photon_subscription_config
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE photon_subscription_config IS
    'Issue #1509: unpopulated Photon subscription preset shape (repo/image selectors, arch opt-in). No reader or writer exists yet -- a later sync-lane issue owns that.';

-- 'photon-repo-discovery' job/run type -----------------------------------------------
-- PhotonRepoDiscoveryJobHandler (this issue) claims this type; registered in
-- Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed in the same change
-- (issue #619's own convention: handler registration and allowlist entry land
-- together, never one without the other). The image-discovery job type
-- ('photon-image-discovery') is NOT reserved here -- it is this issue's documented
-- remainder and reserves its own job/run type alongside its own handler, mirroring
-- issue #1479/#1482's precedent for binaries-download.
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep', 'photon-repo-discovery'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep', 'photon-repo-discovery'
    ));

-- Runner grants: PhotonRepoDiscoveryJobHandler upserts rows in photon_repo_index only
-- (no DELETE -- discovery never row-deletes, this repo's #556 grant-hygiene
-- convention, proven both directions by PhotonRepoIndexRunnerRoleGrantTests).
-- photon_image_index and photon_subscription_config get no runner grant in this
-- migration -- no consumer exists yet (0118's oci_bundles precedent for the same
-- shape of gap).
GRANT SELECT, INSERT, UPDATE ON photon_repo_index TO waypoint_download_runner;
