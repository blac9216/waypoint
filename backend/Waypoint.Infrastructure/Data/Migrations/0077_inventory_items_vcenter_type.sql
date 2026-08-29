-- Issue #1081 (epic #726), live validation round 11 finding: a successful discovery
-- pass creates a `vcenter` component with neither a stable vendor identifier nor any
-- version fact, and no `inventory_items` row of any vCenter/appliance type exists
-- either -- discovery persists `cluster`, `host` and `vm` rows only, so the
-- appliance's own version/build fact (available from the vSphere API's
-- `content.about`, alongside a stable instance UUID identity) had nowhere to be
-- captured.
--
-- This migration widens the closed `inventory_items.type` vocabulary (migration 0011)
-- with a fourth value, `vcenter`, so the appliance's own row -- moref = its
-- authoritative `content.about.instanceUuid`, version/build = `content.about.version`/
-- `.build` -- has the same home an `esxi` row already has for a discovered host.
-- No column change: `inventory_items` already carries `build`/`version` (migrations
-- 0011/0068), both nullable, both type-agnostic.
--
-- Idempotent by construction (DROP CONSTRAINT IF EXISTS + re-ADD), matching every
-- prior migration in this directory that widens a closed CHECK vocabulary (e.g. 0011's
-- own `job_events_event_type_check` widening in the same file).
ALTER TABLE inventory_items DROP CONSTRAINT IF EXISTS inventory_items_type_check;
ALTER TABLE inventory_items ADD CONSTRAINT inventory_items_type_check
    CHECK (type IN ('cluster', 'host', 'vm', 'vcenter'));
