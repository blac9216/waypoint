-- Issue #784 (epic #726): an Admin-only, audited retention hold that protects a
-- terminal compliance run's ENTIRE evidence graph -- as one unit -- from the
-- existing `POST /runs/{id}/purge` path (RunPurgeService) and from issue #1062's
-- not-yet-built graph-wide retention sweep, which the issue's owner ruling
-- requires to be exempted through THIS real hold model rather than an inert stub.
--
-- run_retention_holds --------------------------------------------------------------
-- Presence-based current state: a row exists for run_id if and only if that run is
-- currently held. Placing a hold inserts the row; removing one deletes it. This
-- mirrors run_secrets/trust bypass's "presence implies state" idiom already used
-- elsewhere in this schema rather than adding a boolean flag column that could go
-- stale relative to a separate event log.
--
-- One row per run (PRIMARY KEY run_id) -- a run is either held or it is not, never
-- "held twice". FK to runs(id) with no ON DELETE clause (default RESTRICT): matches
-- every other purge-adjacent table's own header note (0042, 0046) that `runs` rows
-- are retained forever, never hard-deleted, so this FK's delete behavior is never
-- actually exercised -- RESTRICT just documents that expectation defensively, same
-- as target_credential_bindings/job_credential_bindings's default posture.
--
-- reason is required and non-blank (CHECK) for BOTH placing and removing a hold --
-- RunRetentionHoldRepository writes the removal reason to audit_log (see below)
-- rather than to this table, since this table only ever describes the CURRENT
-- (placed) state; the same NOT NULL/non-blank shape is enforced at the API layer
-- for the removal reason too, just not by a column here.
--
-- Every transition (place AND remove), with actor/time/reason/direction, is recorded
-- in the EXISTING append-only audit_log table (migration 0001/0020) via the same
-- inline "INSERT INTO audit_log (event_type, actor, run_id, detail)" idiom
-- TrustRepository already established for a reasoned Admin action -- no new audit
-- table is introduced here. event_type values: 'retention_hold_placed' /
-- 'retention_hold_removed'; detail carries the reason for both directions.
CREATE TABLE IF NOT EXISTS run_retention_holds (
    run_id UUID PRIMARY KEY REFERENCES runs (id),
    reason TEXT NOT NULL CHECK (length(btrim(reason)) > 0),
    placed_by TEXT NOT NULL,
    placed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Runner grants: deliberately NONE. Placing/removing a hold and reading its status
-- for the run-details/admin surface are exclusively API-side actions
-- (RunsController -> RunRetentionHoldService, over the same owner-privileged
-- connection every other Admin-reasoned action in this codebase already uses --
-- TrustRepository, RunPurgeRepository, RunHistoryDeletionRepository). Neither
-- waypoint_compliance_runner nor waypoint_download_runner ever needs to read or
-- write this table: the runner never purges anything itself (RunPurgeService's own
-- doc comment -- "this service never claims/executes anything itself"), so the hold
-- exclusion this table backs is checked entirely in RunPurgeService, API-side,
-- before any runner-side purge job is ever enqueued. Withholding both roles' grants
-- here mirrors appliance_state's existing "API/Settings-only singleton" posture
-- (migration 0025's header). RunnerRoleGrantDriftTests-style coverage in
-- RunRetentionHoldGrantDriftTests proves this table is unreachable (42501) under
-- the REAL waypoint_compliance_runner role, not just under the migration owner.
