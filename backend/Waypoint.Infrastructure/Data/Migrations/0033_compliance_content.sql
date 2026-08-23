-- Issue #40 (epic #558), backend slice: compliance-content management. Persisted
-- appliance state for the VMware DoD compliance-automation repo (docs/domain-model.md
-- "Compliance content (the profiles repo)": "pinned tag or tracked branch, recorded
-- commit, last-pull author/time, profile inventory") plus the pull history and profile
-- inventory the Benchmarks screen (#559) reads. See ADR-0017: content-pull/
-- content-import execute in compliance-runner, not download-runner -- the grants below
-- follow that placement, mirroring 0025_runner_db_roles.sql's per-domain shape.

-- compliance_content -----------------------------------------------------------------
-- Singleton config row (id fixed at 1, same shape as 0017's stigman_connections):
-- which ref to track (pinned tag XOR tracked branch) and the last commit actually
-- pulled. GET returns "not configured" (no row) until the first PUT; a pull job reads
-- this row for its ref and writes pulled_commit/pulled_by/pulled_at back on success.
CREATE TABLE IF NOT EXISTS compliance_content (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    repository_url TEXT NOT NULL,
    ref_type TEXT NOT NULL,
    ref_value TEXT NOT NULL,
    pulled_commit TEXT NULL,
    pulled_by TEXT NULL,
    pulled_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT compliance_content_singleton_check CHECK (id = 1),
    CONSTRAINT compliance_content_ref_type_check CHECK (ref_type IN ('tag', 'branch'))
);

CREATE OR REPLACE TRIGGER trg_compliance_content_updated_at
    BEFORE UPDATE ON compliance_content
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- compliance_content_pulls ------------------------------------------------------------
-- Append-only pull history (who/when/commit, issue #40 AC "Pull updates the mounted
-- profile content as a job with history"). One row per content-pull/content-import job
-- execution, successful or not, so a failed pull is still visible in history rather
-- than silently vanishing -- the same "individual failures are recorded, not hidden"
-- convention audit_log (0001) and job_events (0003) already follow. job_id links back
-- to the owning jobs row for correlation with its event stream; ON DELETE SET NULL so
-- history survives if job retention ever prunes old jobs rows (no such pruning exists
-- yet, but this table's whole purpose is being a durable record independent of the
-- job's own lifecycle).
CREATE TABLE IF NOT EXISTS compliance_content_pulls (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id UUID NULL REFERENCES jobs (id) ON DELETE SET NULL,
    ref_type TEXT NOT NULL,
    ref_value TEXT NOT NULL,
    commit TEXT NULL,
    status TEXT NOT NULL,
    note TEXT NULL,
    initiated_by TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT compliance_content_pulls_ref_type_check CHECK (ref_type IN ('tag', 'branch')),
    CONSTRAINT compliance_content_pulls_status_check CHECK (status IN ('succeeded', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_compliance_content_pulls_created_at ON compliance_content_pulls (created_at DESC);

-- profiles --------------------------------------------------------------------------
-- Profile inventory (issue #40 AC "Profile inventory drives the Benchmarks profile
-- list"): one row per InSpec profile discovered in the pulled content tree, upserted
-- by the same content-pull job that updates compliance_content above. `state` is
-- derived at write time by the handler (current / update_pending / pinned) rather than
-- computed at read time, since "pinned" depends on compliance_content.ref_type, which
-- only the pull that wrote this row's commit knew at that moment; a future PUT that
-- changes ref_type does not retroactively relabel rows already at the old commit.
CREATE TABLE IF NOT EXISTS profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_key TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    version TEXT NULL,
    commit TEXT NOT NULL,
    state TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT profiles_state_check CHECK (state IN ('current', 'update_pending', 'pinned'))
);

CREATE OR REPLACE TRIGGER trg_profiles_updated_at
    BEFORE UPDATE ON profiles
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Runner grants (ADR-0017): content-pull/content-import are compliance-runner job
-- types, so compliance_content/compliance_content_pulls/profiles are a
-- compliance-runner-only write surface -- download-runner gets nothing here, matching
-- 0025's per-domain grant split. Guarded by the same role-existence check 0025 uses so
-- this migration fails loudly rather than silently granting nothing against a
-- pre-#442 pgdata volume.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE ON compliance_content TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON compliance_content_pulls TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE, DELETE ON profiles TO waypoint_compliance_runner;
