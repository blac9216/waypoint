-- Issue #1396 (epic #1185 "Content libraries", split from design record #37 --
-- see its closing comment for the split plan; slot free as of this tree's HEAD, next
-- after 0129 with 0130/0131/0132 carried by open PRs #1791/#1816/#1807): the durable
-- item model, the operation layer's item identity table. Item CRUD (add/update/remove)
-- is driven by Waypoint.Infrastructure.ContentLibraries.ContentLibraryItemService,
-- which delegates every disk write to IContentLibraryWriter (#1393) -- this table is
-- Waypoint's own record of "what items does this library have", the source of the
-- full item set the writer's full-rewrite contract requires on every call.
--
-- `files` is a JSONB array of `{"name": ..., "size": ..., "content_hash": ...}`
-- objects (Waypoint.Core.ContentLibraries.ContentLibraryItemFileWrite`'s own shape) --
-- this slice scopes one item to exactly one file (a single ISO/OVF/other artifact),
-- matching every real use of this table today; the writer's own contract already
-- supports a multi-file item for a future slice, so the column is a JSON array (not a
-- single filename) to avoid a breaking schema change if/when that lands.
--
-- `directory_name` is the item's own directory segment directly under the library's
-- disk root (research #1032: flat, one level, never nested) -- this service always
-- derives it as the item's own `id` in `N` format (32 hex characters, no separators),
-- so it never carries operator input and needs no path-traversal validation of its
-- own; UNIQUE (library_id, directory_name) is still enforced as a second, independent
-- invariant, matching this table's own `id`-derivation-must-hold assumption rather
-- than trusting it silently.
--
-- `content_library_item_folders.item_id` (migration 0113, issue #1389) shipped with
-- NO foreign key because this table did not exist yet ("#1396 still queued", per
-- 0113's own header). This migration closes that gap with `ON DELETE CASCADE`, not
-- `RESTRICT`: 0113's own design already treats the ABSENCE of a
-- `content_library_item_folders` row as an item's valid "unassigned / at library
-- root" state (there is no sentinel folder row for the root), and folder DELETE
-- already has its own independent non-empty guard (`content_library_folders`'s own
-- children/assignments) that has nothing to do with item lifecycle -- so an item
-- being organized into a folder is metadata about the item, not a reason to block the
-- item's own removal (which is a normal operation here, unlike folder delete's
-- Admin-gated "deletes/purges" bucket). RESTRICT would force every caller to
-- unassign an item from its folder before it could ever be removed, which is exactly
-- the workflow-blocking friction 0113's cascade-heavy posture (both its own tables
-- cascade off `content_libraries`/`content_library_folders`) was designed to avoid.
CREATE TABLE IF NOT EXISTS content_library_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    library_id UUID NOT NULL REFERENCES content_libraries(id) ON DELETE CASCADE,
    directory_name TEXT NOT NULL,
    name TEXT NOT NULL,
    type TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    version BIGINT NOT NULL DEFAULT 1,
    files JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_library_items_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT content_library_items_version_positive CHECK (version > 0),
    CONSTRAINT content_library_items_files_not_empty CHECK (jsonb_array_length(files) > 0),
    CONSTRAINT content_library_items_type_check CHECK (type IN ('vcsp.ovf', 'vcsp.iso', 'vcsp.other')),
    CONSTRAINT content_library_items_directory_key UNIQUE (library_id, directory_name)
);

CREATE INDEX IF NOT EXISTS content_library_items_library_idx
    ON content_library_items (library_id);

CREATE OR REPLACE TRIGGER trg_content_library_items_updated_at
    BEFORE UPDATE ON content_library_items
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Closes the FK gap 0113 left open (see header above): item_id now names a real row,
-- CASCADE so removing an item quietly drops its own folder assignment rather than
-- blocking the removal or leaving a dangling row. DROP-then-ADD (not
-- "ADD CONSTRAINT IF NOT EXISTS", which Postgres does not support for a table
-- constraint) is this repo's own idempotent-ALTER convention -- see 0103's identical
-- shape on credentials_credential_type_check.
ALTER TABLE content_library_item_folders
    DROP CONSTRAINT IF EXISTS content_library_item_folders_item_id_fkey;

ALTER TABLE content_library_item_folders
    ADD CONSTRAINT content_library_item_folders_item_id_fkey
    FOREIGN KEY (item_id) REFERENCES content_library_items(id) ON DELETE CASCADE;

COMMENT ON TABLE content_library_items IS
    'Issue #1396: durable item identity for one content library -- add/update/remove is Waypoint.Infrastructure.ContentLibraries.ContentLibraryItemService, which delegates every on-disk write to IContentLibraryWriter (#1393). UUID is reused across an update (same id, version increments); directory_name is always the id in "N" format, never operator input.';
COMMENT ON COLUMN content_library_items.files IS
    'JSONB array of {"name", "size", "content_hash"} objects (Waypoint.Core.ContentLibraries.ContentLibraryItemFileWrite shape). This slice scopes one item to exactly one file; the array shape avoids a breaking schema change for a future multi-file item.';

-- Runner grants: deliberately NONE, same rationale and precedent as 0090's and
-- 0113's own no-grant posture (this repo's #556 grant-hygiene convention): every row
-- this table holds is created/mutated through ContentLibraryItemService (issue #1396)
-- via the owner connection string; the HTTP surface calling that service is its own
-- follow-up issue (#1826) -- no runner process reads or writes this table today
-- either way. The nearest future
-- runner-side consumer is #1057 (depot-fed add-to-library), which ships its own GRANT
-- migration when it lands, following the 0100/#1484/0127/#1436 precedent. Proven both
-- directions (both runner roles denied SELECT) by ContentLibraryItemsRunnerRoleGrantTests,
-- mirroring 0113/#1389's ContentLibraryFoldersRunnerRoleGrantTests pattern.
