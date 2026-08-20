-- Issue #31 (epic #14): docs/api-contract.md `/schedules` -- cron-style schedules for
-- read-only job types (scan/discover/credential-test/catalog-index), enqueued by a
-- control-plane hosted service (ScheduleDispatchHostedService, Waypoint.Api) that
-- validates due schedules and enqueues via IJobControlRepository. Runners never touch
-- this table -- they claim/execute the runs the dispatcher creates, through their
-- existing job_type allowlists (migration 0024/JobCapabilities), so no runner grant is
-- added here (contrast migration 0025, which grants exactly the tables each runner's
-- claim/execute path reads/writes).
--
-- job_type is a narrower, explicitly read-only subset of jobs_job_type_check
-- (0019) -- 'remediate', 'download', 'bundle-*', 'content-*', and 'update' are
-- schedulable by neither this CHECK nor the API's server-side validation
-- (SchedulesController), which is the second, redundant enforcement point noted in
-- issue #31's validation notes (claim-side allowlists already separate the two
-- execution domains; this is the create-side half of the same guarantee).
--
-- There is no dedicated depot-kind column: whether a schedule is depot-backed
-- (catalog-index -- the only download-domain read-only type) is derived from
-- job_type membership in ScheduleJobTypes.DepotKinds, which is how
-- ScheduleDispatchService.ReconcileDepotAutoPauseAsync auto-pauses only those
-- schedules in disconnected mode (docs/api-contract.md `/schedules`: "auto-paused
-- states in air-gapped mode for depot kinds"). scope carries the same
-- site/target-selection JSON shape RunCreationService already parses for `/runs`
-- (ScanScopeParser) -- re-used, not duplicated, by the dispatcher.
CREATE TABLE IF NOT EXISTS schedules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    job_type TEXT NOT NULL,
    cron_expression TEXT NOT NULL,
    scope JSONB NOT NULL DEFAULT '{}'::jsonb,
    credential_id UUID NULL REFERENCES credentials (id),
    enabled BOOLEAN NOT NULL DEFAULT true,
    paused_reason TEXT NULL,
    next_run_at TIMESTAMPTZ NULL,
    last_run_at TIMESTAMPTZ NULL,
    last_run_id UUID NULL REFERENCES runs (id),
    last_result TEXT NULL,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT schedules_name_key UNIQUE (name),
    CONSTRAINT schedules_job_type_check CHECK (job_type IN (
        'scan', 'discover', 'credential-test', 'catalog-index'
    )),
    CONSTRAINT schedules_last_result_check CHECK (last_result IS NULL OR last_result IN (
        'success', 'failure'
    ))
);

CREATE INDEX IF NOT EXISTS idx_schedules_due
    ON schedules (next_run_at)
    WHERE enabled AND next_run_at IS NOT NULL;

CREATE OR REPLACE TRIGGER trg_schedules_updated_at
    BEFORE UPDATE ON schedules
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
