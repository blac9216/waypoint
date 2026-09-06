-- Issue #1705 (part of validation epic #1704; slot 0129 -- the next free slot after
-- #1440's 0128 at the time this migration was authored, verified against both the
-- migrations directory and `gh pr list --state open` before use): the #1503
-- presence-sweep rewrite (PR #1629) changed WaypointCatalogIndex.psm1's output
-- contract from "one row per file found on disk" to "one presence result per catalog
-- entry, 'present' or 'missing'" (see the module's Test-CatalogEntryPresent /
-- New-ArtifactPresenceRecord, ValidateSet('present', 'missing')), and
-- CatalogIndexJobHandler.UpsertArtifactsAsync upserts that Status straight into
-- depot_artifacts.status -- but 0001_initial_schema.sql's
-- depot_artifacts_status_check only ever allowed
-- ('indexed', 'downloading', 'present', 'failed'), so the first 'missing' row a real,
-- partially populated depot produces (every real depot) throws 23514 and aborts the
-- whole catalog-index job (jobs.state ends completed_with_failures).
--
-- This migration widens the constraint to admit 'missing' alongside the four values
-- already in production use, using the same DROP/ADD idiom 0127 used to widen
-- jobs_job_type_check/runs_run_type_check. It does not remove 'downloading': that
-- value predates this issue (0001) and, although no depot_artifacts writer currently
-- emits it, removing an already-shipped allowed value is a separate, unrelated
-- change this issue does not need to make. The full post-migration vocabulary --
-- 'indexed' (VendorProductVersionCatalogParser's connected pull, issue #687),
-- 'downloading' (reserved, currently unwritten), 'present' and 'failed'
-- (DownloadJobHandler/BinariesDownloadJobHandler's verify-outcome upserts), and the
-- new 'missing' (the presence sweep's absent-from-disk result) -- is captured as the
-- single source of truth Waypoint.Core.Catalog.DepotArtifactStatuses.All, which
-- DepotArtifactStatusesConstraintDriftTests parses this constraint's IN-list against
-- (this repo's #1517/RepoCredentialBindingConstraintDriftTests convention: assert
-- equality against the parsed SQL, not a hand-copied guess, so adding a value on
-- either side alone fails a test). No grant changes: migration 0025 already grants
-- waypoint_download_runner exactly SELECT, INSERT, UPDATE on depot_artifacts, and a
-- CHECK constraint widening touches no privileges.
ALTER TABLE depot_artifacts
    DROP CONSTRAINT IF EXISTS depot_artifacts_status_check;

ALTER TABLE depot_artifacts
    ADD CONSTRAINT depot_artifacts_status_check
    CHECK (status IN ('indexed', 'downloading', 'present', 'failed', 'missing'));
