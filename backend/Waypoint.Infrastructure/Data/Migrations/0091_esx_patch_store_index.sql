-- Issue #1447 (epic #1183, split from design record #38, approved design #16
-- section 4; slot 0091 pre-assigned 2026-08-30 on #38's closing comment). Consumes
-- #1446's IEsxPatchStoreMetadataParser output (content identity by zip-byte
-- SHA-256, tolerant parse with warnings) and indexes it into the database as a
-- full, cumulative model of what an ESX patch store SHOULD contain, plus a
-- first-class discrepancies ledger a reconciliation pass writes to.
--
-- esx_patch_store_index -------------------------------------------------------------
-- One row per metadata bundle this reconciler has EVER seen at a given store
-- root, keyed on the parser's own content identity (never the non-deterministic
-- 9.1 micro-depot zip filename -- issue #1446 AC). A bundle absent from the most
-- recent parse is NOT deleted from this table -- it is what makes a later parse's
-- "previously indexed, now absent" comparison possible (the reconciler's own
-- "missing" detection) and matches this repo's alert-instead-of-drop convention
-- (design decision Q11). last_indexed_at advances every time the bundle is seen
-- again; first_indexed_at is stamped once and never touched again.
CREATE TABLE IF NOT EXISTS esx_patch_store_index (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    store_root TEXT NOT NULL,
    layout TEXT NOT NULL CHECK (layout IN ('Legacy', 'Depot91')),
    content_key TEXT NOT NULL,
    vendor_code TEXT NOT NULL,
    zip_relative_path TEXT NOT NULL,
    product_id TEXT NULL,
    version TEXT NULL,
    channel_name TEXT NULL,
    first_indexed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_indexed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT esx_patch_store_index_store_content_key UNIQUE (store_root, content_key)
);

CREATE INDEX IF NOT EXISTS idx_esx_patch_store_index_store_root ON esx_patch_store_index (store_root);

COMMENT ON TABLE esx_patch_store_index IS
    'Issue #1447: cumulative model of every metadata bundle a reconciliation pass has ever seen at a store root, keyed on #1446''s content-identity SHA-256. Never row-deleted by the reconciler.';

-- esx_patch_store_discrepancies -------------------------------------------------------
-- Discrepancies as first-class records (issue #1447 Proposed Changes: "not just
-- logged"), one row per (store_root, discrepancy_type, key) so a repeat
-- reconciliation touches the same row rather than accumulating duplicates.
-- discrepancy_type='missing': key is the content_key of a bundle this store's
-- own esx_patch_store_index has previously recorded that the most recent parse
-- no longer found. discrepancy_type='orphan': key is 'vendor_code/file_name' for
-- a zip file physically present under a vendor directory that no successfully
-- parsed bundle in the most recent run references.
--
-- resolved_at is set (never row-deleted) when a later reconciliation no longer
-- observes the condition -- e.g. a "missing" bundle reappears (content arrived
-- via transfer, per the issue's Risks note treating "orphan today" as
-- provisional), or an "orphan" zip is picked up by metadata on a later sync.
-- This is bookkeeping on the alert's own lifecycle, not removal of a disk
-- object or of the row itself -- the never-auto-remove invariant this issue's
-- AC3 states is about disk content and about this reconciler never deleting a
-- row; explicit orphan removal from disk is a later sibling's (#1452) action.
CREATE TABLE IF NOT EXISTS esx_patch_store_discrepancies (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    store_root TEXT NOT NULL,
    discrepancy_type TEXT NOT NULL CHECK (discrepancy_type IN ('missing', 'orphan')),
    key TEXT NOT NULL,
    vendor_code TEXT NULL,
    relative_path TEXT NULL,
    detail TEXT NULL,
    first_detected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_detected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at TIMESTAMPTZ NULL,
    CONSTRAINT esx_patch_store_discrepancies_store_type_key UNIQUE (store_root, discrepancy_type, key)
);

CREATE INDEX IF NOT EXISTS idx_esx_patch_store_discrepancies_open
    ON esx_patch_store_discrepancies (store_root, discrepancy_type)
    WHERE resolved_at IS NULL;

COMMENT ON TABLE esx_patch_store_discrepancies IS
    'Issue #1447: missing/orphan discrepancies a reconciliation pass found, as first-class alertable records. resolved_at is bookkeeping on the alert only -- rows are never deleted, and orphan disk content is never removed by this table or its writer.';

-- Runner grants -----------------------------------------------------------------------
-- The reconciler reads the ESX patch store's on-disk hostupdate/ tree directly
-- (via #1446's parser), which only a runner process has filesystem access to
-- (ADR-0013/0014: runners host domain logic in-process against their own mounted
-- volumes; the API process never touches the depot filesystem). It therefore
-- runs under waypoint_download_runner, same as every other download-domain
-- table's grant precedent (0025/0100/0107/0127). SELECT+INSERT+UPDATE on both
-- tables: the reconciler upserts index rows and discrepancy rows, and touches
-- resolved_at/last_*_at columns on existing rows -- deliberately no DELETE on
-- either table (this repo's #556 grant-hygiene convention: withhold what the
-- code never calls, defense in depth alongside the repository having no delete
-- method).
GRANT SELECT, INSERT, UPDATE ON esx_patch_store_index TO waypoint_download_runner;
GRANT SELECT, INSERT, UPDATE ON esx_patch_store_discrepancies TO waypoint_download_runner;
