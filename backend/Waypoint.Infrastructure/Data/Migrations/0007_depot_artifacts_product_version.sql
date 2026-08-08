-- Issue #193 (epic #9 slice 1): the catalog REST surface filters by
-- product/version/status (docs/api-contract.md `/catalog/artifacts`). Vendor
-- catalog shapes stay JSONB (ADR-0002, see 0001's depot_artifacts comment) --
-- product and version are promoted to GENERATED STORED columns rather than
-- real writable ones, the same reasoning 0001 used for sha256: they exist so
-- the list query can filter/index on them directly instead of scanning
-- metadata on every request, but they are still derived, not independently
-- writable. NULL-safe: rows whose metadata omits either key simply don't
-- match a filter for it, rather than erroring the projection.
ALTER TABLE depot_artifacts
    ADD COLUMN IF NOT EXISTS product TEXT GENERATED ALWAYS AS (metadata ->> 'product') STORED,
    ADD COLUMN IF NOT EXISTS version TEXT GENERATED ALWAYS AS (metadata ->> 'version') STORED;

CREATE INDEX IF NOT EXISTS idx_depot_artifacts_product ON depot_artifacts (product) WHERE product IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_depot_artifacts_version ON depot_artifacts (version) WHERE version IS NOT NULL;
