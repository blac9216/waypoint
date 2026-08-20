-- Issue #515: 0001 declared runs.schedule_id UUID NULL as a forward reference (the
-- same "table doesn't exist yet" pattern later applied to runs.site_id for #13) --
-- schedules didn't exist until #31's migration 0030. Now that it does, add the real
-- FK. ON DELETE SET NULL rather than CASCADE/RESTRICT mirrors 0006's
-- audit_log_credential_id_fkey precedent: a run is a completed historical record
-- (docs/domain-model.md) that must outlive the schedule that produced it -- deleting
-- a schedule (SchedulesController.Delete) must not delete or be blocked by the runs
-- it already dispatched.
--
-- This is the run-carries-its-own-schedule half of the link; schedules.last_run_id
-- (0030) is the other half and only ever points at the most recent run. Populating
-- this column is ScheduleDispatchService's job (RunCreationService/
-- IJobControlRepository.CreateRunAsync, issue #515) -- this migration only adds the
-- constraint.
ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_schedule_id_fkey;

ALTER TABLE runs
    ADD CONSTRAINT runs_schedule_id_fkey
    FOREIGN KEY (schedule_id) REFERENCES schedules (id) ON DELETE SET NULL;
