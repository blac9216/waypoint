-- Issue #745 (epic #726 Wave 4, ADR-0024/0025): the domain-owned, immutable result
-- model for planned component execution -- "Results owns honest coverage, current
-- findings, attempt history, artifacts, attestations, and upload receipts" (epic §6).
-- This is the FIRST SLICE: the persistence shape, HDF-to-finding parsing, and the
-- run-rollup read path. It does NOT yet replace the existing filesystem-derived
-- results screens/read paths (RunArtifactProjectionService/HdfSeverityCounter) --
-- those stay exactly as they are; this table is additive evidence alongside them
-- until a later slice migrates the UI onto it (stated remainder, see PR body).
--
-- Three tables:
--
-- `component_results` is one immutable row per (scan_plan_item, job, attempt) --
-- ADR-0024's "each planned item owns ordered attempts; the latest completed attempt
-- supplies the current component result; prior failures/cancellations/logs remain
-- immutable history" reconciled with epic §6's "findings distinguish genuine
-- compliance failure from execution error". There is no UPDATE path anywhere in this
-- migration or its repository -- a result row is a historical fact, written once when
-- a job's HDF becomes available (or, for a component that could not execute at all,
-- written as a single synthetic execution_error/not_reviewed row so epic §6's
-- "present exactly once, never omitted" rule holds even when there is no HDF to
-- parse). `attempt_number` mirrors migration 0062's upload_attempts convention
-- (1-based, monotonic per job, application-assigned). `job_id` IS a foreign key here
-- (unlike upload_attempts) because a component result's whole reason for existing is
-- to be joined back to the job/plan-item identity chain for aggregation -- ON DELETE
-- RESTRICT, matching every other FK onto jobs/scan_plan_items in this schema, so a
-- result row is never silently orphaned.
--
-- `component_result_findings` is one row per XCCDF-mapped control result inside a
-- component_results row -- the actual CAT-severity finding detail, closed `status`
-- vocabulary distinguishing pass/fail/not_applicable/not_reviewed/execution_error/
-- skipped (epic §6: "Failed, skipped, excluded, not-applicable, open, and passed
-- states are not conflated"). `not_reviewed` is the epic §6 "applicable control that
-- cannot execute" state -- HdfFindingsParser guarantees a control present in the
-- profile's control catalog but absent from a malformed/truncated results array is
-- synthesized as exactly one `not_reviewed` row, never dropped and never
-- `not_applicable` (see HdfFindingsParser's own doc comment for the exact rule and
-- HdfFindingsParserTests for the pin).
--
-- `component_result_artifacts` is one row per attached artifact (raw/attested HDF,
-- CKL, summary, log) with kind/path/digest/size -- epic §6 "Attach raw/attested HDF,
-- CKL, summary, log, and upload records with digest/size". This does NOT duplicate
-- migration 0062's upload_attempts (epic §6 "attach, don't duplicate" / PR #932) --
-- an upload receipt stays in upload_attempts; this table only records the scan
-- OUTPUT artifacts themselves. path is a server-relative artifact-store path
-- (ScanArtifactPaths' own convention), never an absolute filesystem path leaked to a
-- client.
--
-- Runner grants: waypoint_compliance_runner gets INSERT (never UPDATE/DELETE, per the
-- immutability contract) on all three tables plus the SELECT it needs to compute the
-- next attempt_number under its own connection (same pattern as migration 0062).
CREATE TABLE IF NOT EXISTS component_results (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL REFERENCES runs (id) ON DELETE RESTRICT,
    job_id UUID NOT NULL REFERENCES jobs (id) ON DELETE RESTRICT,
    scan_plan_item_id UUID NOT NULL REFERENCES scan_plan_items (id) ON DELETE RESTRICT,
    component_id UUID NOT NULL REFERENCES components (id) ON DELETE RESTRICT,
    attempt_number INTEGER NOT NULL,
    -- Closed vocabulary for the OVERALL component-level outcome this attempt produced.
    -- Mirrors the per-finding status vocabulary below one level up: an attempt that
    -- executed and produced a parseable HDF is 'completed' regardless of how many
    -- individual findings failed (finding-level pass/fail lives in
    -- component_result_findings); an attempt that could not execute at all (auth
    -- failure, unreachable target, malformed/absent HDF) is 'execution_error'; an
    -- attempt explicitly skipped before execution (unsupported component, missing
    -- credential -- ADR-0023/0024 skip reasons) is 'skipped'.
    status TEXT NOT NULL,
    cat_i_open INTEGER NOT NULL DEFAULT 0,
    cat_ii_open INTEGER NOT NULL DEFAULT 0,
    cat_iii_open INTEGER NOT NULL DEFAULT 0,
    passed_count INTEGER NOT NULL DEFAULT 0,
    not_applicable_count INTEGER NOT NULL DEFAULT 0,
    not_reviewed_count INTEGER NOT NULL DEFAULT 0,
    skipped_count INTEGER NOT NULL DEFAULT 0,
    -- Non-secret, human-actionable summary of why status is execution_error/skipped --
    -- never a raw exception or credential-shaped string (same "sanitized evidence"
    -- discipline as ScanJobHandler's FailScanAsync redaction).
    detail TEXT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT component_results_attempt_number_check CHECK (attempt_number >= 1),
    CONSTRAINT component_results_status_check CHECK (status IN ('completed', 'execution_error', 'skipped')),
    CONSTRAINT component_results_counts_non_negative_check CHECK (
        cat_i_open >= 0 AND cat_ii_open >= 0 AND cat_iii_open >= 0
        AND passed_count >= 0 AND not_applicable_count >= 0
        AND not_reviewed_count >= 0 AND skipped_count >= 0
    ),
    -- One result row per (job, attempt): a job's attempt is a single execution and
    -- must never be recorded twice, mirroring upload_attempts' own per-job-attempt
    -- shape one column over.
    CONSTRAINT component_results_unique_job_attempt UNIQUE (job_id, attempt_number)
);

CREATE INDEX IF NOT EXISTS idx_component_results_run_id ON component_results (run_id);
CREATE INDEX IF NOT EXISTS idx_component_results_scan_plan_item_id ON component_results (scan_plan_item_id);
CREATE INDEX IF NOT EXISTS idx_component_results_job_id ON component_results (job_id, attempt_number);

-- Run-rollup aggregation (GET /runs/{id}/component-results/summary) groups by
-- (run_id, status) and needs the latest attempt per scan_plan_item only -- see the
-- repository's SQL doc comment for the exact query this index serves.
CREATE INDEX IF NOT EXISTS idx_component_results_run_id_plan_item_attempt
    ON component_results (run_id, scan_plan_item_id, attempt_number DESC);

CREATE TABLE IF NOT EXISTS component_result_findings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    component_result_id UUID NOT NULL REFERENCES component_results (id) ON DELETE RESTRICT,
    -- The XCCDF/benchmark rule identity this finding maps to (e.g. an SV-xxxxxx rule
    -- id) -- free text rather than a benchmark_rules FK because SRG/HDF-only findings
    -- have no XCCDF mapping at all (epic §6 "SRGs remain HDF-only unless a future
    -- exact STIG mapping is introduced"); an InSpec control id is always present,
    -- an XCCDF rule id is present only for STIG output.
    control_id TEXT NOT NULL,
    rule_id TEXT NULL,
    title TEXT NULL,
    severity TEXT NOT NULL,
    -- Closed status vocabulary -- epic §6's exact list, plus 'skipped' for a control
    -- explicitly excluded by scope/attestation before execution (distinct from
    -- 'not_reviewed', which is epic §6's "applicable but could not execute").
    status TEXT NOT NULL,
    -- Sanitized, bounded evidence text (a truncated InSpec result message or "no
    -- results reported for this control") -- never the full raw HDF blob (that lives
    -- in component_result_artifacts as a file reference, not inline).
    evidence TEXT NULL,
    CONSTRAINT component_result_findings_severity_check CHECK (severity IN ('cat_i', 'cat_ii', 'cat_iii')),
    CONSTRAINT component_result_findings_status_check CHECK (
        status IN ('passed', 'failed', 'not_applicable', 'not_reviewed', 'execution_error', 'skipped')
    )
);

CREATE INDEX IF NOT EXISTS idx_component_result_findings_component_result_id
    ON component_result_findings (component_result_id);

CREATE TABLE IF NOT EXISTS component_result_artifacts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    component_result_id UUID NOT NULL REFERENCES component_results (id) ON DELETE RESTRICT,
    kind TEXT NOT NULL,
    path TEXT NOT NULL,
    digest TEXT NOT NULL,
    size_bytes BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT component_result_artifacts_kind_check CHECK (kind IN ('hdf_raw', 'hdf_attested', 'ckl', 'summary', 'log')),
    CONSTRAINT component_result_artifacts_size_non_negative_check CHECK (size_bytes >= 0)
);

CREATE INDEX IF NOT EXISTS idx_component_result_artifacts_component_result_id
    ON component_result_artifacts (component_result_id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

-- The compliance-runner records a result (and its findings/artifacts) at job
-- completion time and reads its own history back only to compute the next
-- attempt_number under its own connection -- never UPDATE/DELETE, matching the
-- append-only contract migration 0062 already established.
GRANT SELECT, INSERT ON component_results TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON component_result_findings TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON component_result_artifacts TO waypoint_compliance_runner;

-- Migration 0061 deferred the runner's scan_plan_items grant "to whichever future
-- slice makes a runner path actually consume a claimed job's frozen plan item" --
-- this is that slice: ComponentResultRecordingService resolves the claimed job's
-- scan_plan_item_id to its frozen component_id before writing a result row. SELECT
-- only; the runner never writes plan rows (plans stay API-side immutable, ADR-0023).
GRANT SELECT ON scan_plan_items TO waypoint_compliance_runner;
