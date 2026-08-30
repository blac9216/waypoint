-- Issue #1488 (epic #1180, split from design record #1038): rekey
-- depot_artifacts's identity to the authenticated vendor catalog's own stable
-- identity -- relative path + size/hash -- instead of the two incompatible
-- ExternalId namespaces CatalogIndexJobHandler's offline disk walk (already a
-- depot-relative path) and VendorProductVersionCatalogParser's connected pull
-- (a bare filename, issue #687) wrote into the same column. This migration is
-- schema-shape only: it does not merge or reconcile the two legacy
-- namespaces against each other (that requires real presence-sweep logic,
-- landing in #1503/#1512) -- it renames the identity column in place so every
-- pre-existing row keeps its data untouched, and adds the columns/table the
-- later children need. No row is ever dropped (repo's no-silent-drop
-- convention, restated in #1038's Risks section).
--
-- depot_artifacts.external_id -> relative_path -------------------------------------
-- A straight column rename: Postgres preserves the existing UNIQUE constraint,
-- index, and every row's value across a RENAME COLUMN, so rows from BOTH
-- legacy namespaces (nested depot-relative paths from the offline disk walk,
-- bare filenames from the connected pull) survive this migration unchanged --
-- exactly the "left as-is, no data loss" posture #1038's Proposed Changes
-- section calls for. Reconciling the two namespaces into one true identity
-- per artifact is presence-sweep behavior (#1503), not a schema concern.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'depot_artifacts' AND column_name = 'external_id'
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'depot_artifacts' AND column_name = 'relative_path'
    ) THEN
        ALTER TABLE depot_artifacts RENAME COLUMN external_id TO relative_path;
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'depot_artifacts_external_id_key'
    ) AND NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'depot_artifacts_relative_path_key'
    ) THEN
        ALTER TABLE depot_artifacts RENAME CONSTRAINT depot_artifacts_external_id_key TO depot_artifacts_relative_path_key;
    END IF;
END $$;

-- size_bytes: the other half of the catalog identity pair (relative path +
-- size/hash -- sha256 is already a real column since migration 0001). NULL
-- for every pre-existing row (neither legacy write path recorded it as a
-- real column, only inside metadata JSONB where CatalogIndexJobHandler's
-- output happened to place it) -- callers backfill it the next time they
-- upsert the row, same posture 0001 already established for sha256.
--
-- last_verified_at: presence field #1038 calls for ("status, last_verified_at
-- if not already sufficient"). Left unset by the generic upsert path in this
-- slice -- deciding WHEN a row counts as freshly verified is presence-sweep
-- behavior (#1503/#1512), not a schema concern; the column exists now so
-- those children have somewhere to write without a further migration.
ALTER TABLE depot_artifacts
    ADD COLUMN IF NOT EXISTS size_bytes BIGINT NULL,
    ADD COLUMN IF NOT EXISTS last_verified_at TIMESTAMPTZ NULL;

-- unknown_catalog_files --------------------------------------------------------------
-- Files found on the depot share that the authenticated vendor catalog does
-- not describe (#1038's Motivation: "silently absent from every surface
-- today"). Decision Q11 (referenced by #1038's Proposed Changes): alert
-- instead of drop -- a row here is insert-or-touch-last-seen ONLY. There is
-- deliberately no delete path in the repository (enforced by
-- UnknownCatalogFileRepositoryTests.Repository_HasNoDeleteOrRemoveMethod --
-- ANY file this table has ever seen stays visible until a human or a later
-- child's reconciliation logic explicitly decides otherwise; that decision
-- does not exist yet, so today's contract is "never auto-deleted", full
-- stop.
CREATE TABLE IF NOT EXISTS unknown_catalog_files (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    relative_path TEXT NOT NULL,
    size_bytes BIGINT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT unknown_catalog_files_relative_path_key UNIQUE (relative_path)
);

CREATE INDEX IF NOT EXISTS idx_unknown_catalog_files_last_seen_at ON unknown_catalog_files (last_seen_at);

COMMENT ON TABLE unknown_catalog_files IS
    'Issue #1488: files present on a depot share with no matching catalog identity. Insert-or-touch-last-seen only -- never auto-deleted (design decision Q11 referenced by #1038).';

-- Runner grants: waypoint_download_runner is the role that will populate this
-- table once the presence sweep (#1503) runs inside CatalogIndexJobHandler's
-- rework (#1512) -- same role migration 0025 already grants
-- SELECT/INSERT/UPDATE on depot_artifacts to. Deliberately no DELETE grant:
-- the repository has no delete method to call it from (see the table's own
-- comment above), so withholding the grant is defense in depth, not the only
-- control.
GRANT SELECT, INSERT, UPDATE ON unknown_catalog_files TO waypoint_download_runner;
