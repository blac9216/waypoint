-- Issue #598 (epic #558): the per-control inventory `GET /profiles/{id}/controls`
-- (docs/api-contract.md) reads. Slot 0038 -- 0036/0037 are claimed by other in-flight
-- PRs (#569, #39) at the time this migration was authored; ordering vs. either is not
-- assumed (neither touches profiles/compliance-content tables).
--
-- Why a table and not lazy filesystem parsing at request time: the compliance-content
-- working tree (ComplianceContentOptions.ContentPath) is a compliance-runner-only mount
-- (ADR-0017; ADR-0014 §7 "storage follows least privilege" lists only "compliance
-- runner: compliance-content read access" -- the API process has no such mount today,
-- and ComplianceContentOptions is bound only in Waypoint.Infrastructure.Execution, never
-- Waypoint.Api). The content-pull job (compliance-runner) already walks each profile's
-- directory once per pull to discover it for the `profiles` table (0035); parsing that
-- profile's `controls/*.rb` files at the same time and persisting the result is strictly
-- cheaper than adding a second cross-process filesystem dependency to the API, and keeps
-- controls in sync with whatever commit `profiles.commit` says is installed.
--
-- Read-only to everything but content-management ops, mirroring `profiles`: only
-- content-pull/content-import (compliance-runner) ever write it; the API only reads.
CREATE TABLE IF NOT EXISTS profile_controls (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL REFERENCES profiles (id) ON DELETE CASCADE,
    control_id TEXT NOT NULL,
    title TEXT NULL,
    severity TEXT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT profile_controls_profile_control_unique UNIQUE (profile_id, control_id)
);

CREATE OR REPLACE TRIGGER trg_profile_controls_updated_at
    BEFORE UPDATE ON profile_controls
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX IF NOT EXISTS idx_profile_controls_profile_id ON profile_controls (profile_id);

-- Runner grants (ADR-0017), same shape as 0035's profiles grant: content-pull replaces
-- a profile's whole control set per pull (upsert + delete-missing), so the runner needs
-- the full read/write set; the API is read-only.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE, DELETE ON profile_controls TO waypoint_compliance_runner;
