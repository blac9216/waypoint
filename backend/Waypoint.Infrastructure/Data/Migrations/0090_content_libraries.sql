-- Issue #1391 (epic #1185 "Content libraries", split from design record #37 --
-- see its closing comment for the four-child (A/B/C/D) breakdown -- approved design
-- #16 section 6; slot pre-assigned 2026-08-30): the content-library REGISTRY, the
-- first of the four children in dependency order. A `content_libraries` row names one
-- MULTIPLE-named-VCSP-library and the single flat directory it owns -- "flat" meaning
-- the row IS the directory, never a directory tree of nested libraries (design #16
-- section 6 "Multiple named VCSP libraries; flat on disk"). Deliberately inert: no
-- VCSP file semantics land here -- `lib.json`/`items.json` are written by the sibling
-- writer issue (#1393) into the directory this table names, and per-item rows are
-- #1396's. This migration only provisions the registry and the minimal CRUD API
-- (POST/GET/DELETE) every later step resolves "which library, which path" through
-- before it can write anything.
--
-- disk_path is derived by the API from `name` (RootPath/{name}, see
-- Waypoint.Core.ContentLibraries.ContentLibraryOptions), never a free-form
-- operator-supplied path -- but `name` itself is still operator input, so keeping
-- every library's directory inside the configured root is an ENFORCED invariant, not
-- a by-construction one: Waypoint.Infrastructure.ContentLibraries.
-- ContentLibraryRepository.ResolveDiskPath validates `name` as a single path segment
-- (rejecting `..`, any `/`, and rooted input) and re-checks the resolved path against
-- the root via Path.GetFullPath before it is ever combined with RootPath, at the same
-- layer that touches the filesystem -- the controller's NamePattern regex is a
-- second, operator-facing 400 one layer up, not the only guard. Both `name` and
-- `disk_path` carry their own UNIQUE constraint: `name` is the one an operator-facing
-- 409 hangs off, `disk_path` is a belt-and-suspenders invariant that should never
-- itself be reachable while the derivation above holds.
CREATE TABLE IF NOT EXISTS content_libraries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    disk_path TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_libraries_name_key UNIQUE (name),
    CONSTRAINT content_libraries_disk_path_key UNIQUE (disk_path)
);

CREATE OR REPLACE TRIGGER trg_content_libraries_updated_at
    BEFORE UPDATE ON content_libraries
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE content_libraries IS
    'Issue #1391: the content-library registry -- one row per named, flat-on-disk VCSP library. No VCSP file state (lib.json/items.json, #1393) or item rows (#1396) live here.';
COMMENT ON COLUMN content_libraries.disk_path IS
    'Absolute path of the one directory this library owns, derived by the API as RootPath/{name} -- never a free-form operator-supplied path.';

-- Runner grants: deliberately NONE (this repo's #556 grant-hygiene convention,
-- following the 0059/0078/0107 precedent: document the no-grant posture with an
-- accurate rationale, not a guessed one -- see epic #1182's #1406 landed event for
-- what a WRONG rationale here costs later). Every write and read in this slice is
-- Admin/Viewer API-side (Waypoint.Api.Controllers.ContentLibrariesController) through
-- the owner connection string -- no runner process reads or writes this table yet.
-- The nearest future runner-side consumer is #1057 (depot-fed add-to-library,
-- filed against this contract per #37's closing comment) -- a genuine
-- waypoint_download_runner-claimed job resolving a library's disk_path to write
-- into it -- which must ship its own GRANT migration (SELECT on content_libraries)
-- when it lands, following the 0100/#1484/0107/#1436 "the job that reads this table
-- grants itself what it needs when it lands" precedent. Neither
-- waypoint_compliance_runner nor waypoint_download_runner needs access to this table
-- YET.
