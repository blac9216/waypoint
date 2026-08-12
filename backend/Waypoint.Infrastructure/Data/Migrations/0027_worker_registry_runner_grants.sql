-- Issue #443: grants on 0026's worker_registry table for the two runner roles 0025
-- created (waypoint_compliance_runner, waypoint_download_runner). Both runners write
-- their own heartbeat row on the same cadence their file-based readiness report
-- already runs on; SELECT is not needed by either role -- only the control plane
-- (migrator/API connection) reads this table back for GET /system, and that
-- connection is not one of these two least-privilege roles (ADR-0014 §7).
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

-- INSERT + UPDATE only: the ON CONFLICT upsert in WorkerRegistryRepository.HeartbeatAsync
-- needs both, but neither runner role ever SELECTs this table (only the control-plane
-- connection reads it back for GET /system, per the comment above), so SELECT is
-- deliberately withheld to keep these least-privilege roles matching their stated intent.
GRANT INSERT, UPDATE ON worker_registry TO waypoint_compliance_runner, waypoint_download_runner;
