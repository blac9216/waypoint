-- Issue #737 (epic #726 Wave 2 capstone: "Fan out component jobs through Waypoint
-- queue priorities and resource admission"). ADR-0024 "A compliance run is a domain
-- projection over its immutable plan. Each concrete PlannedComponentItem has exactly
-- one Postgres component job" is the governing decision. This is the FIRST slice of
-- #737 (fan-out + claim-safety only -- see the PR body for the exact stated
-- remainder, principally ScanJobHandler's component-granular vSphere execution).
--
-- Adds jobs.scan_plan_item_id: a nullable link from a fanned-out 'scan' job row to the
-- exact immutable scan_plan_items row (migration 0057) it executes. NULL for every
-- pre-existing job and for the still-supported legacy target_ids/profile_id-only scan
-- path (RunCreationService keeps fanning out one job per TARGET there, exactly as
-- before -- see that method's own doc comment on the coexistence). Non-NULL only for a
-- job fanned out from a target_scope-driven run whose scope compiled into a plan
-- (issue #734): RunCreationService now fans out one job per ACCEPTED scan_plan_item
-- instead of one job per target on that path, per ADR-0024's "one run, one
-- independently controllable job per concrete scan item."
--
-- ON DELETE RESTRICT, matching every other FK onto scan_plan_items (migration 0057):
-- a component job is durable execution history and must never be silently orphaned by
-- a plan-row deletion that cannot happen anyway (scan_plan_items has no delete path
-- other than cascading off its owning scan_plans/runs row, at which point this job
-- row itself is also being deleted by whatever process deletes run history -- see
-- run_history_deletion_tombstones/run_purges).
--
-- No new runner grant: waypoint_compliance_runner already holds SELECT/INSERT/UPDATE
-- on jobs (migration 0025) and a nullable additive column needs no new grant. The
-- runner does not yet read scan_plan_items in this slice (ScanJobHandler's
-- component-granular execution is this issue's stated remainder) -- a runner grant on
-- scan_plan_items/scan_plans is deferred to whichever future slice makes a runner
-- path actually consume a claimed job's frozen plan item.
ALTER TABLE jobs
    ADD COLUMN IF NOT EXISTS scan_plan_item_id UUID NULL
        REFERENCES scan_plan_items (id) ON DELETE RESTRICT;

CREATE INDEX IF NOT EXISTS idx_jobs_scan_plan_item_id
    ON jobs (scan_plan_item_id)
    WHERE scan_plan_item_id IS NOT NULL;
