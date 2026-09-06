-- Issue #1389 (epic #1185 "Content libraries", split from design record #1056 --
-- see its closing comment for the split plan; slot pre-assigned 2026-08-30): the
-- operator-defined virtual folder tree over a content library's items -- DB-only
-- organizational metadata, never written to disk. Two tables:
--
-- `content_library_folders`: one row per folder, self-referencing via
-- `parent_folder_id` to form a tree rooted at NULL. Depth is unbounded (no stated
-- limit in the parent design record -- flagged for the reviewer, not invented here).
--
-- NULL-parent uniqueness is EXPLICIT, not left to the composite UNIQUE constraint's
-- default NULL-distinct behavior (which would let two root folders in the same
-- library both be named the same thing, since Postgres treats every NULL as
-- distinct from every other NULL in a UNIQUE constraint): a plain UNIQUE constraint
-- on (library_id, parent_folder_id, name) only dedupes NON-root folders, so a second,
-- partial unique index on (library_id, name) WHERE parent_folder_id IS NULL closes
-- the root-level gap, treating "no parent" as its own implicit sibling group that
-- must also have unique names.
--
-- `content_library_item_folders`: the item-folder assignment. Issue #1391/PR #1649
-- (migration 0090) shipped ONLY the library registry -- there is no items table yet
-- (#1396, item CRUD via the VCSP writer, is still queued). The nearest thing to an
-- item identity that exists today is Waypoint.Core.ContentLibraries.
-- ContentLibraryItemWrite.Id (a caller-supplied Guid the VCSP writer, #1393, uses to
-- recognize "same item across writes"), so this table's `item_id` is a plain UUID
-- column with NO foreign key -- there is nothing to reference yet. #1396, when it
-- lands, either introduces a real item row this column can be re-pointed at via a
-- follow-up migration, or confirms the writer's caller-supplied Guid IS the durable
-- item identity, in which case this column already carries it correctly. Single-
-- parent by design: UNIQUE (library_id, item_id) means an item has at most one
-- folder; the ABSENCE of a row is the item's "unassigned / at library root" state
-- (there is no folder row representing the root itself), so unassigning an item is a
-- DELETE, not an UPDATE to some sentinel folder id.
--
-- Both tables cascade off `content_libraries` (whole-library delete removes every
-- folder and assignment with it) and `content_library_folders` (deleting a folder
-- removes its own item assignments) -- application code (ContentLibraryFolderRepository)
-- is the ONLY path that can delete a folder directly, and it rejects a non-empty one
-- (child folders or assigned items) with 409 before ever reaching this cascade;
-- `parent_folder_id`'s own self-referential ON DELETE CASCADE exists purely so a
-- whole-library cascade delete (which removes every folder row for that library in
-- one statement) never has to worry about self-FK ordering among the rows it is
-- removing together.
CREATE TABLE IF NOT EXISTS content_library_folders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    library_id UUID NOT NULL REFERENCES content_libraries(id) ON DELETE CASCADE,
    parent_folder_id UUID REFERENCES content_library_folders(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_library_folders_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT content_library_folders_sibling_name_key UNIQUE (library_id, parent_folder_id, name)
);

-- Closes the NULL-parent gap the composite UNIQUE constraint above cannot cover (see
-- header): unique folder names among ROOT folders (no parent) within one library.
CREATE UNIQUE INDEX IF NOT EXISTS content_library_folders_root_name_key
    ON content_library_folders (library_id, name)
    WHERE parent_folder_id IS NULL;

CREATE INDEX IF NOT EXISTS content_library_folders_parent_idx
    ON content_library_folders (parent_folder_id);

CREATE TABLE IF NOT EXISTS content_library_item_folders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    library_id UUID NOT NULL REFERENCES content_libraries(id) ON DELETE CASCADE,
    item_id UUID NOT NULL,
    folder_id UUID NOT NULL REFERENCES content_library_folders(id) ON DELETE CASCADE,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_library_item_folders_item_key UNIQUE (library_id, item_id)
);

CREATE INDEX IF NOT EXISTS content_library_item_folders_folder_idx
    ON content_library_item_folders (folder_id);

COMMENT ON TABLE content_library_folders IS
    'Issue #1389: operator-defined virtual folder tree over one content library''s items -- DB metadata only, never written to disk. Root folders (parent_folder_id IS NULL) have their own name-uniqueness index; see this migration''s header comment.';
COMMENT ON TABLE content_library_item_folders IS
    'Issue #1389: single-parent item-to-folder assignment. No FK on item_id -- issue #1391/PR #1649 shipped no items table yet (#1396 remains queued); item_id is the caller-supplied Guid identity Waypoint.Core.ContentLibraries.ContentLibraryItemWrite.Id already establishes. Absence of a row means the item is unassigned (library root).';

-- Runner grants: deliberately NONE, same rationale and precedent as 0090's own
-- no-grant posture for content_libraries (this repo's #556 grant-hygiene
-- convention): every read and write in this slice is Admin/Viewer API-side through
-- ContentLibraryFoldersController via the owner connection string. No runner process
-- reads or writes either table today; a future runner-side consumer ships its own
-- GRANT migration when it lands (0100/#1484, 0127/#1436 precedent). Proven both
-- directions (both runner roles denied SELECT) by RunnerRoleGrantDriftTests, mirroring
-- 0103/#1517's ComplianceRunnerRole_CannotReadRepoCredentialBindings pattern.
