-- Issue #444 (fresh-stack parity): 0027 withheld SELECT on worker_registry from the
-- two runner roles on the reasoning that "neither runner role ever SELECTs this
-- table" -- true of the application's own SQL text, but wrong about what Postgres
-- actually requires to execute it. WorkerRegistryRepository.HeartbeatAsync's upsert
-- is `INSERT ... ON CONFLICT (worker_id) DO UPDATE ...`: evaluating the conflict
-- target and the UPDATE's EXCLUDED-row reference both require SELECT privilege on
-- the table being upserted into, even though no explicit SELECT statement is ever
-- issued. Verified directly against a live role, isolated from the application:
-- a plain `INSERT INTO worker_registry (...) VALUES (...)` (no conflict clause)
-- succeeds under INSERT-only, but the exact `ON CONFLICT (worker_id) DO UPDATE ...`
-- statement HeartbeatAsync sends fails `permission denied for table worker_registry`
-- under INSERT+UPDATE alone and succeeds once SELECT is added -- reproduced on an
-- otherwise fully-migrated fresh Compose stack (deploy/scripts/fresh-stack-smoke-
-- test.sh), not a race or a stale-connection artifact: a brand-new psql session as
-- the same role, well after 0027 had already applied, hit the identical error on the
-- identical statement shape.
--
-- This is why compliance-runner's/download-runner's own worker_registry heartbeat
-- (and therefore GET /system's runner visibility) never actually worked end to end
-- on a fresh deployment before this migration, despite 0025/0026/0027 all applying
-- cleanly and unit/integration coverage passing -- no prior test exercised the real
-- least-privilege role against a genuine INSERT ... ON CONFLICT DO UPDATE with a
-- pre-existing conflicting row via that literal SQL text.
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

GRANT SELECT ON worker_registry TO waypoint_compliance_runner, waypoint_download_runner;
