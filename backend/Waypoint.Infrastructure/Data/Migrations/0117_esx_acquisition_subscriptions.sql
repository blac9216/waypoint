-- Issue #1470 (epic #1181, split from design record #1159/child B): the ESX
-- acquisition subscription/preset MODEL -- which platforms an operator wants synced,
-- independent of the tool wrapper (#1459) and the sync job that consumes this table
-- (#1484). Slot 0117 was pre-assigned 2026-08-30 while migrations 0082-0116 were not
-- yet claimed by any in-flight branch; if one of those numbers lands on main first,
-- whoever merges second must renumber this file (name + the ledger entry in
-- SchemaMigrationTests.cs) before merging, same discipline as 0037's own header note.
--
-- esx_acquisition_subscriptions ---------------------------------------------------
-- One row per named preset selecting a subset of the
-- `lcm.esx.supported.host.platforms` vendor vocabulary (see
-- Waypoint.Core.Downloads.IEsxPlatformVocabularyReader -- resolved at request time
-- from the depot's authenticated product-version catalog document, never a hardcoded
-- enum here or in application code). `selected_platforms` is a plain TEXT[] rather
-- than a join table: the vocabulary is small (a handful of platform keys), presets
-- never need to join against it relationally, and the API validates every element
-- against the live vocabulary on write (never at the database layer, since the set
-- of valid values is not known to this schema).
--
-- Disabling a subscription is a plain `enabled = false` UPDATE, never a DELETE --
-- issue #1470 AC "disabling a subscription doesn't delete its history": the row,
-- its `created_at`, and its selected-platforms value are preserved untouched so the
-- preset can be re-enabled later with its original selection intact.
--
-- No runner grant in this migration: the sync job that reads this table to drive
-- acquisition (#1484) is out of scope here and grants itself whatever
-- waypoint_download_runner access it needs when it lands. Every write here is
-- Admin-only, API-side (EsxAcquisitionController), matching trust_policies'
-- (migration 0059) "no runner grant in the model slice" precedent.
CREATE TABLE IF NOT EXISTS esx_acquisition_subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    selected_platforms TEXT[] NOT NULL DEFAULT '{}',
    enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT esx_acquisition_subscriptions_name_not_blank_check CHECK (btrim(name) <> '')
);

COMMENT ON TABLE esx_acquisition_subscriptions IS
    'Issue #1470: named ESX acquisition presets selecting a subset of the lcm.esx.supported.host.platforms vendor vocabulary. Disabling never deletes the row.';
COMMENT ON COLUMN esx_acquisition_subscriptions.selected_platforms IS
    'Platform keys chosen from the vocabulary at write time -- validated against the live vocabulary by the API, not by this schema (the valid set is sourced from the depot catalog, never hardcoded).';
COMMENT ON COLUMN esx_acquisition_subscriptions.enabled IS
    'Admin on/off switch. Disabling is a plain UPDATE (this column only); the row and its history are never deleted by that action.';

CREATE OR REPLACE TRIGGER trg_esx_acquisition_subscriptions_updated_at
    BEFORE UPDATE ON esx_acquisition_subscriptions
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
