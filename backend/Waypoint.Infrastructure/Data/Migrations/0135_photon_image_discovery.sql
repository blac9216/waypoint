-- Issue #1790 (epic #1184): documented remainder of #1509 (migration 0130) -- the
-- Photon image-tree discovery job (`photon-image-discovery`). Migration 0130 shipped
-- the `photon_image_index` table schema-only, deliberately WITHOUT a runner grant
-- (0118's oci_bundles precedent: no consumer, no grant). This migration is that
-- consumer's grant, plus the job/run-type reservation issue #619's convention
-- requires land in the same change as the handler registration
-- (PhotonImageDiscoveryJobHandler, this same issue).
--
-- MIGRATION SLOT: originally authored as 0134 (next free slot verified against
-- origin/main at authoring time: 96 migrations, highest 0130, with 0131/0132/0133
-- already carried by open PRs #1816/#1807/#1831). Renumbered to 0135 at rebase time
-- (2026-09-08): PR #1842 (issue #1804, `depot_artifacts.superseded_at`) merged first
-- and also claimed slot 0134 -- per the convention migration 0037 documents, whoever
-- merges last among colliding slots renumbers.

ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep', 'photon-repo-discovery', 'photon-image-discovery'
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
        'retention-sweep', 'photon-repo-discovery', 'photon-image-discovery'
    ));

-- Runner grant: PhotonImageDiscoveryJobHandler upserts rows in photon_image_index only
-- (no DELETE -- discovery never row-deletes, this repo's #556 grant-hygiene
-- convention, proven both directions by PhotonImageIndexRunnerRoleGrantTests).
-- photon_subscription_config still gets no runner grant -- no consumer exists yet
-- (0118's oci_bundles precedent for the same shape of gap).
GRANT SELECT, INSERT, UPDATE ON photon_image_index TO waypoint_download_runner;
