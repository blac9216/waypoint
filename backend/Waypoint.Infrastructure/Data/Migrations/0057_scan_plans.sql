-- Issue #734 (epic #726 Wave 2: "Compile and validate immutable component scan plans
-- before queue fan-out"). ADR-0023 "Immutable plans" and ADR-0024 are the governing
-- decisions. Migration 0056 (#733) froze WHICH components a run's requested/resolved
-- scope covers; this migration freezes WHAT WOULD BE DONE to each accepted one --
-- exact catalog/baseline/transport/credential-purpose/benchmark identity, so a run's
-- intent survives later inventory, content, or configuration changes exactly as
-- ADR-0023 requires ("A persisted plan is sufficient to reproduce what was intended
-- without reading mutable current target/catalog/config state").
--
-- Two tables, one plan per run:
--
-- `scan_plans` is the plan header: one row per run (UNIQUE run_id, ON DELETE CASCADE,
-- same "no independent meaning once its owning run is gone" convention as migration
-- 0056's run_scope_snapshots), the plan schema version (fail-closed on an unknown
-- version per issue #734 AC), the requested/resolved scope link (run_scope_snapshots,
-- #733 -- this table does not duplicate that freeze, only references it), a
-- deterministic content-addressed `plan_digest` (issue #734 AC "preview and create ...
-- produce the same plan digest"), and a human-readable `explanation` summarizing
-- accepted/skipped counts and reasons for run-history display without recomputing the
-- join. Immutable once written -- there is no UPDATE path in
-- Waypoint.Infrastructure.Runs.ScanPlanRepository, matching every other
-- digest-addressed history table in this schema (0052, 0055, 0056).
--
-- `scan_plan_items` is one row per ACCEPTED execution item only (issue #734 AC "No
-- run/job rows are created when any required execution item is invalid" reconciled
-- against ADR-0023's explicit-skip rule -- see the planner service doc comment for the
-- full reasoning): a rejected/skipped component is recorded in the plan header's
-- `skips_json`, not as a plan-item row, mirroring migration 0056's
-- resolved-vs-omitted split one layer up. Each accepted item freezes the exact
-- component/endpoint identity, the catalog execution profile and its content
-- release/product-version identity, the active baseline id (STIG) or null (SRG has no
-- baseline concept -- ADR-0022), the benchmark revision id (STIG) or null (SRG), the
-- transport and selector parameters, priority/report-group, the credential purposes
-- required, and the declared-input names the component's profile requires -- enough
-- for #735-#737's component-job layer to build a job from this row alone, without
-- re-deriving any of it from current catalog/component state.
--
-- Scope discipline (issue #734's own "NOT this slice" list, mirrored from #733's
-- migration 0056 header): this migration does NOT create component-granular job/
-- attempt rows (#735-#737, ADR-0024 "Issues #735-#737 implement snapshots, credential
-- requirements, and queue fan-out"); RunCreationService's job fan-out stays
-- target-granular exactly as #733 left it. This migration also does not add the
-- `/runs/plan-preview` HTTP endpoint, schedule-dispatch planning, or discovery-refresh
-- integration -- explicitly deferred remainders of this issue (see PR body). Control-
-- setting snapshots (Input/Attestation, ADR-0024) and credential-binding RESOLUTION
-- (vs. the purpose REQUIREMENT recorded here) are also #735-#737.
--
-- No new runner grants: like migration 0056, this slice's write path is API-side only
-- (Waypoint.Infrastructure.Runs.ScanPlannerService runs inside RunCreationService's
-- existing control-plane request path). The compliance-runner does not read these
-- tables in this slice -- a runner grant is deferred to whichever of #735-#737 first
-- makes a runner path consume an accepted plan item.
CREATE TABLE IF NOT EXISTS scan_plans (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL UNIQUE REFERENCES runs (id) ON DELETE CASCADE,
    plan_schema_version INTEGER NOT NULL,
    run_scope_snapshot_id UUID NULL REFERENCES run_scope_snapshots (id) ON DELETE RESTRICT,
    plan_digest TEXT NOT NULL,
    explanation TEXT NOT NULL,
    skips_json JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT scan_plans_plan_schema_version_check CHECK (plan_schema_version > 0)
);

CREATE INDEX IF NOT EXISTS idx_scan_plans_run_id ON scan_plans (run_id);

-- One row per accepted execution item. `component_id`/`catalog_execution_profile_id`/
-- `baseline_id`/`benchmark_revision_id` are RESTRICT, not CASCADE: a plan item is
-- reproducibility evidence of what a past run intended, and its referenced identity
-- rows must never disappear out from under a frozen plan (ADR-0023 "Later inventory,
-- content activation, target edits, retirement, or purge cannot rewrite them"). Only
-- `scan_plan_id` cascades, because a plan item has no independent meaning once its
-- owning plan (and therefore run) is gone -- same convention migration 0056 already
-- uses for run_scope_snapshots off runs.
CREATE TABLE IF NOT EXISTS scan_plan_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scan_plan_id UUID NOT NULL REFERENCES scan_plans (id) ON DELETE CASCADE,
    component_id UUID NOT NULL REFERENCES components (id) ON DELETE RESTRICT,
    catalog_execution_profile_id UUID NOT NULL REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    baseline_id UUID NULL REFERENCES baselines (id) ON DELETE RESTRICT,
    benchmark_revision_id UUID NULL REFERENCES benchmark_revisions (id) ON DELETE RESTRICT,
    transport TEXT NOT NULL,
    selector_kind TEXT NOT NULL,
    selector_name TEXT NULL,
    report_group_key TEXT NOT NULL,
    priority INTEGER NOT NULL,
    output_kind TEXT NOT NULL,
    required_purposes_json JSONB NOT NULL DEFAULT '[]',
    declared_inputs_json JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT scan_plan_items_unique_component_per_plan UNIQUE (scan_plan_id, component_id)
);

CREATE INDEX IF NOT EXISTS idx_scan_plan_items_scan_plan_id ON scan_plan_items (scan_plan_id);
CREATE INDEX IF NOT EXISTS idx_scan_plan_items_component_id ON scan_plan_items (component_id);
