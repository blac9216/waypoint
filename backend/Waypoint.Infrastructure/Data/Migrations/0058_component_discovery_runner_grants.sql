-- Issue #732 (discovery-wiring remainder) + #840 (folded, atomicity/guard): wires
-- DiscoverJobHandler (the compliance-runner's `discover` job) to call
-- ComponentRepository.UpsertDiscoveredAsync alongside the existing
-- InventoryRepository.UpsertDiscoveryResultsAsync write, so `components` rows exist
-- for real discovered targets instead of only for API/repository-seeded test rows
-- (PR #839's own stated remainder, made user-visible by PR #856's Start-a-Scan wizard
-- reading GET /targets/{id}/components).
--
-- Migration 0054 ("No new runner grants: nothing in the compliance-runner ... process
-- writes this schema in this slice") is now stale for exactly the reason its own
-- header predicted would eventually apply -- discovery-job scheduling changes moving
-- inventory materialization into the runner. This migration grants the
-- waypoint_compliance_runner role the same SELECT/INSERT/UPDATE shape 0051/0053
-- already established for catalog_* atomic-upsert tables, plus INSERT-only on the
-- append-only component_observations table (never SELECT/UPDATE/DELETE -- the runner
-- writes observations forward-only through UpsertDiscoveredAsync and never reads them
-- back; API-side GET /components/{id}/observations reads through the owner
-- connection like every other repository in this codebase).
--
-- #840's atomicity fix (UpsertDiscoveredAsync's check-then-insert becoming a
-- constraint-backed ON CONFLICT upsert against BOTH of migration 0054's unique
-- indexes -- components_vendor_identity_unique for the vendor-identity case,
-- idx_components_no_vendor_identity_unique for the NULL-vendor partial-index case)
-- needs no new schema: both conflict targets already exist as of 0054. Only the
-- runner-role grant below is new.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE ON components TO waypoint_compliance_runner;
GRANT INSERT ON component_observations TO waypoint_compliance_runner;
