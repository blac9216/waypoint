-- Issue #21 (epic #13, M2): the `discover` job's cached inventory store.
-- docs/domain-model.md "Target": "Discovered ESXi hosts and VMs are cached inventory
-- under a `vsphere` target, not standalone targets." docs/api-contract.md
-- `/targets/{id}/inventory` and the Postgres schema sketch:
-- `inventory_items` (target_id, type, parent_id, name, build, maintenance).
--
-- Idempotent by construction (IF NOT EXISTS / OR REPLACE), matching every prior
-- migration in this directory.

-- inventory_items ------------------------------------------------------------
-- One row per discovered cluster/host/VM under a vSphere target. `type` is the
-- three-level tree api-contract.md names for the start-a-scan checkbox tree
-- (cluster -> host -> vm); `parent_id` self-references to build that tree (NULL for a
-- top-level cluster; a bare vCenter with no cluster is represented as a host with a
-- NULL parent_id too, since "cluster" is optional in vSphere).
--
-- `moref` is the vSphere managed-object reference (e.g. "host-21", "vm-104") --
-- discovery's natural stable identity, independent of display name (a host/VM can be
-- renamed in vCenter without losing its inventory row). UNIQUE(target_id, moref) is
-- the upsert key re-discovery ON CONFLICTs against (no duplicate hosts, issue #21
-- acceptance criterion).
--
-- `build` carries the ESXi/VM build/version string (api-contract.md: "capture build,
-- maintenance mode"); `maintenance_mode` is host-only (NULL for cluster/vm rows).
--
-- `removed_at` implements soft-delete removal detection (issue #21: "removals
-- detected"): a re-discovery pass that no longer sees a previously-discovered moref
-- stamps this instead of hard-deleting, so a mid-scan reference to an item that
-- vanished between discovery and scan-start still resolves. `last_seen_at` advances on
-- every discovery pass that still finds the item (including the one that first creates
-- it); a row whose `last_seen_at` predates the current pass and is not already removed
-- is exactly the set to soft-delete. Once removed, a subsequent pass that sees the same
-- moref again simply un-marks it (`removed_at` back to NULL) rather than inserting a
-- second row -- vSphere morefs are not reused across unrelated objects within a single
-- vCenter's lifetime, so treating a moref's reappearance as "the same object came
-- back" (e.g. exiting maintenance mode, a brief host disconnect) is the correct read,
-- not a new object that happens to collide.
CREATE TABLE IF NOT EXISTS inventory_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    target_id UUID NOT NULL REFERENCES targets (id) ON DELETE CASCADE,
    parent_id UUID NULL REFERENCES inventory_items (id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    moref TEXT NOT NULL,
    name TEXT NOT NULL,
    build TEXT NULL,
    maintenance_mode BOOLEAN NULL,
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    removed_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT inventory_items_type_check CHECK (type IN ('cluster', 'host', 'vm')),
    CONSTRAINT inventory_items_target_id_moref_key UNIQUE (target_id, moref)
);

CREATE INDEX IF NOT EXISTS idx_inventory_items_target_id ON inventory_items (target_id);
CREATE INDEX IF NOT EXISTS idx_inventory_items_parent_id ON inventory_items (parent_id);

CREATE OR REPLACE TRIGGER trg_inventory_items_updated_at
    BEFORE UPDATE ON inventory_items
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- job_events: add discover.progress -----------------------------------------
-- Mirrors download.progress's job-scoped shape (job_id required, run_id NULL is NOT
-- required here since a discover run is 1 job = 1 target, same as catalog-index's
-- run.progress choice would suggest -- but discover progress is naturally per-job
-- item-count progress like catalog-index's run.progress, so this event type is
-- job-scoped like job.log/download.progress: one discover job, one target, its own
-- progress stream).
ALTER TABLE job_events DROP CONSTRAINT IF EXISTS job_events_event_type_check;
ALTER TABLE job_events ADD CONSTRAINT job_events_event_type_check CHECK (event_type IN (
    'job.state', 'job.log', 'run.progress', 'queue.state',
    'download.progress', 'discover.progress', 'system.notice'
));

ALTER TABLE job_events DROP CONSTRAINT IF EXISTS job_events_scope_check;
ALTER TABLE job_events ADD CONSTRAINT job_events_scope_check CHECK (
    CASE event_type
        WHEN 'job.state' THEN job_id IS NOT NULL
        WHEN 'job.log' THEN job_id IS NOT NULL
        WHEN 'download.progress' THEN job_id IS NOT NULL
        WHEN 'discover.progress' THEN job_id IS NOT NULL
        WHEN 'run.progress' THEN job_id IS NULL AND run_id IS NOT NULL
        WHEN 'queue.state' THEN job_id IS NULL AND run_id IS NOT NULL
        WHEN 'system.notice' THEN job_id IS NULL AND run_id IS NULL
        ELSE false
    END
);
