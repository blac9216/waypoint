-- Issue #1783 (part of validation epic #1704; slot 0132 -- 0130 and 0131 were both
-- claimed by other in-flight PRs (#1791/#1509's 0130_photon_lane_index.sql, and 0131
-- assigned to #1464) after this migration was originally authored at 0130; renumbered
-- per orchestrator correction, verified against both the migrations directory and
-- `gh pr list --state open` before use): live validation run
-- 2 of #1704 proved `binaries-download` passes `depot_artifacts.relative_path`
-- (the catalog's binary fileName, VendorProductVersionCatalogParser's own
-- flattening key) as the real vcf-download-tool's `--id`, which the tool never
-- matches -- every invocation resolves an empty ("0 elements") selection and exits 0.
--
-- Per #1027's depot-consumption research finding (`BINARY_NOT_FOUND_IN_LOCAL_PVC=
-- Binary with ID {0} could not be found in the local product version catalog` --
-- "bundles are addressed by catalog id"), the real `--id` value is the vendor
-- catalog's own `artifacts.bundles[].id` field -- a sibling identifier to the
-- fileName the parser already reads from the same bundle object, but never carried
-- onto the artifact row until now.
--
-- This adds that column additively: NULL for every pre-existing row (neither legacy
-- write path recorded it), populated going forward by
-- VendorProductVersionCatalogParser's connected pull. The offline disk walk
-- (CatalogIndexJobHandler, #1512) has no vendor catalog document to read a bundle id
-- from, so its rows keep bundle_id = NULL -- DownloadsController.QueueBinariesDownload
-- refuses to enqueue a binaries-download job for a NULL bundle_id (409, "re-pull the
-- catalog") rather than pass a garbage/absent id as --id and repeat this issue's bug.
ALTER TABLE depot_artifacts
    ADD COLUMN IF NOT EXISTS bundle_id TEXT NULL;

COMMENT ON COLUMN depot_artifacts.bundle_id IS
    'Issue #1783: the vendor catalog''s artifacts.bundles[].id -- the identifier the real vcf-download-tool binaries download --id actually selects on (#1027 finding), distinct from relative_path (the binary fileName). NULL for rows indexed before this migration or by the offline disk walk, which has no catalog document to read it from.';
