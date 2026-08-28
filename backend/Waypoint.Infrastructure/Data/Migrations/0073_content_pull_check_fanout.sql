-- Issue #1016 (epic #726), owner decision 2026-08-28: parallelize the content-pull
-- inspec-check phase by REUSING the existing job-queue parallelism (ADR-0020 capacity
-- pool + the multi-job component fan-out scan runs already use) instead of adding
-- in-process concurrency inside one PowerShell invocation.
--
-- ContentPullJobHandler's phase 1 (git clone/fetch/checkout + directory enumeration)
-- is unchanged. Phase 2 no longer runs its chunked `inspec check` invocations itself:
-- it now fans out one 'content-check' job per chunk onto its OWN run (the same run
-- the content-pull job itself belongs to -- a content-pull run already fans out to
-- exactly one job today; this is the first run_type where a SECOND wave of jobs is
-- added to an already-running run rather than at initial fan-out). The capacity pool
-- and ordinary claim/admission machinery schedule those chunk jobs in parallel across
-- however many runner replicas/slots exist, exactly like scan's component jobs.
--
-- content_pull_checks -------------------------------------------------------------------
-- One row per fanned-out 'content-check' job: which content-pull job/run it belongs
-- to, the commit it is checking, and the exact profile-directory/profile-key chunk it
-- was handed (mirrors the parameters ContentPullJobHandler used to pass directly to
-- Get-WaypointComplianceContentEntries -- now threaded through the check job's own
-- payload/this row instead of an in-process loop variable). `status` tracks whether
-- the reconcile sweep has already consumed this row's results; a row is never deleted
-- (durable fan-out history, ADR-0022 "immutable source observations ... retained").
--
-- check_job_id is NULLABLE for exactly one shape (PR #1017 review round 1, finding 2):
-- a successful pull that enumerated ZERO executable profiles fans out zero check jobs,
-- but the reconcile sweep discovers work solely through this table -- without a row,
-- such a pull would never be reconciled and its pull history/staging never recorded
-- (breaking issue #40's "every attempt recorded" invariant). ContentPullJobHandler
-- records one zero-chunk MARKER row (check_job_id NULL, profile_directories '[]') for
-- that case; the marker-shape CHECK below keeps it honest (a NULL check_job_id is only
-- ever legal with an empty chunk). UNIQUE (check_job_id) still holds for real rows --
-- Postgres permits multiple NULLs under a plain UNIQUE constraint, and at most one
-- marker per pull is ever written.
CREATE TABLE IF NOT EXISTS content_pull_checks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL REFERENCES runs (id) ON DELETE RESTRICT,
    content_pull_job_id UUID NOT NULL REFERENCES jobs (id) ON DELETE RESTRICT,
    check_job_id UUID NULL REFERENCES jobs (id) ON DELETE RESTRICT,
    source_commit TEXT NOT NULL,
    profile_directories JSONB NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_pull_checks_check_job_unique UNIQUE (check_job_id),
    CONSTRAINT content_pull_checks_status_check CHECK (status IN ('pending', 'reconciled')),
    CONSTRAINT content_pull_checks_marker_shape_check CHECK (
        check_job_id IS NOT NULL OR profile_directories = '[]'::jsonb
    )
);

CREATE INDEX IF NOT EXISTS idx_content_pull_checks_content_pull_job_id
    ON content_pull_checks (content_pull_job_id);
CREATE INDEX IF NOT EXISTS idx_content_pull_checks_run_id ON content_pull_checks (run_id);
CREATE INDEX IF NOT EXISTS idx_content_pull_checks_pending
    ON content_pull_checks (content_pull_job_id)
    WHERE status = 'pending';

-- content_pull_check_results -------------------------------------------------------------
-- One row per profile a 'content-check' job actually processed -- the durable,
-- cross-process form of what used to be an in-memory VendorContentEntry the single
-- content-pull invocation built up itself. A check job running on any runner replica
-- writes its rows here as soon as it finishes; the reconcile step (run on whichever
-- process observes every content_pull_checks row for a pull job terminal) reads them
-- all back to rebuild the exact same contentEntries list the old single-pass loop
-- assembled in memory, so RunSemanticImportAsync's staging/promotion logic is
-- reachable UNCHANGED from a completion step instead of the original handler body.
CREATE TABLE IF NOT EXISTS content_pull_check_results (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    check_job_id UUID NOT NULL REFERENCES jobs (id) ON DELETE RESTRICT,
    profile_key TEXT NOT NULL,
    raw_yaml TEXT NULL,
    has_controls_directory BOOLEAN NOT NULL DEFAULT false,
    has_files_directory BOOLEAN NOT NULL DEFAULT false,
    control_file_names JSONB NOT NULL DEFAULT '[]'::jsonb,
    inspec_check_ran BOOLEAN NOT NULL DEFAULT false,
    inspec_check_passed BOOLEAN NOT NULL DEFAULT false,
    inspec_check_detail TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_pull_check_results_unique UNIQUE (check_job_id, profile_key)
);

CREATE INDEX IF NOT EXISTS idx_content_pull_check_results_check_job_id
    ON content_pull_check_results (check_job_id);

-- 'content-check' is a new Simple-shape job type (queued -> running -> done|failed,
-- same shape as content-pull itself): it runs the #989-bounded `inspec check` pass
-- for exactly the chunk of profiles content_pull_checks recorded for it and reports
-- honest per-profile outcomes, never staging/promoting anything itself (that stays the
-- reconcile completion step's job, run once, after every sibling chunk job for a pull
-- reaches a terminal state). Belongs to the compliance-runner domain for the same
-- reason content-pull/content-import do (ADR-0017) -- JobCapabilities.Compliance gains
-- it in the same change (closed job-type/capability-set lockstep, JobCapabilities'
-- own doc comment).
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull'
    ));

-- Runner grants: compliance-runner both inserts content_pull_checks rows (fan-out, from
-- inside ContentPullJobHandler; UPDATE covers the reconcile sweep's status flip -- the
-- sweep runs inside compliance-runner too, see ContentPullReconcileHostedService) and
-- inserts/reads content_pull_check_results rows (the content-check handler, and the
-- reconcile sweep that reads them all back). No DELETE grant -- these are durable
-- fan-out/result history, matching catalog_import_reports' "no runner delete" posture
-- (migration 0051).
--
-- The column-scoped UPDATE on content_pull_check_results exists because
-- RecordCheckResultAsync writes INSERT ... ON CONFLICT DO UPDATE (PR #1017 review
-- round 1, finding 1 -- proven 42501 without it): the conflict path fires whenever a
-- content-check job re-executes the same (check_job_id, profile_key) pair, i.e. any
-- lease-recovery requeue or retry -- the exact re-run idempotency the ON CONFLICT is
-- there for. Scoped to exactly the payload columns the DO UPDATE SET touches
-- (migration 0033's column-scoped grant idiom); identity/created_at stay
-- runner-immutable.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT, UPDATE ON content_pull_checks TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON content_pull_check_results TO waypoint_compliance_runner;
GRANT UPDATE (raw_yaml, has_controls_directory, has_files_directory, control_file_names,
    inspec_check_ran, inspec_check_passed, inspec_check_detail)
    ON content_pull_check_results TO waypoint_compliance_runner;
