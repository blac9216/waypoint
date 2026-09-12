-- Issue #1472 (epic #1182 "Subscriptions, retention & scheduling", split from design
-- record #1046): the subscription-evaluation job -- diff, fetch-set, lib.json
-- version-counter pre-check, run-job fan-out. UTC-timestamp-prefixed per
-- docs/reference/schema-migrations.md (post-#1845 Step 1; no numeric slot claimed, no
-- ExpectedMigrationCount to bump).
--
-- Reserves the 'subscription-evaluate' job/run type SubscriptionEvaluationJobHandler
-- claims (download-runner domain, JobCapabilities.Download/DownloadRunnerJobTypes.Allowed
-- -- registered in the same change), and adds subscription_evaluation_state: one row
-- per subscription recording the lib.json last-seen version counter, the last
-- evaluation timestamp, and the persisted fetch-set/projected-bytes result (issue
-- #1472's Proposed Changes: "Expose the fetch-set/projected-bytes result on the
-- run/job payload so #1042 has something to read"). ON DELETE CASCADE off
-- subscriptions.id -- this state has no meaning once its subscription is gone.
--
-- Per ADR-0013 (see IJobControlRepository's own doc comment: run creation/fan-out is
-- an ASP.NET-control-plane-only surface) and migration 0025's own grants (neither
-- waypoint_compliance_runner nor waypoint_download_runner has ever been granted INSERT
-- on `runs`), the runner-executed subscription-evaluate job computes and PERSISTS the
-- fetch set here; it never calls IJobControlRepository.CreateRunAsync/FanOutJobsAsync
-- itself. The actual one-run/one-job-per-item fan-out (issue #1472 AC3) is
-- Waypoint.Infrastructure.Subscriptions.SubscriptionEvaluationFanOutService, run under
-- the owner-privileged API connection like every other run-creating call site
-- (RunsController, DownloadsController.QueueBinariesDownload) -- fanned_out_at marks a
-- persisted fetch set consumed so a second fan-out call is a no-op, not a duplicate run.
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep', 'photon-repo-discovery', 'photon-image-discovery',
        'subscription-evaluate'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download',
        'retention-sweep', 'photon-repo-discovery', 'photon-image-discovery',
        'subscription-evaluate'
    ));

CREATE TABLE IF NOT EXISTS subscription_evaluation_state (
    subscription_id UUID PRIMARY KEY REFERENCES subscriptions (id) ON DELETE CASCADE,
    last_seen_lib_version_counter BIGINT NULL,
    last_evaluated_at TIMESTAMPTZ NULL,
    last_fetch_set_count INT NOT NULL DEFAULT 0,
    last_projected_bytes BIGINT NULL,
    fetch_set_json TEXT NOT NULL DEFAULT '[]',
    fanned_out_at TIMESTAMPTZ NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT subscription_evaluation_state_fetch_set_count_check CHECK (last_fetch_set_count >= 0)
);

CREATE OR REPLACE TRIGGER trg_subscription_evaluation_state_updated_at
    BEFORE UPDATE ON subscription_evaluation_state
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE subscription_evaluation_state IS
    'Issue #1472: per-subscription evaluation-job state -- lib.json last-seen version counter (library-mirror lane only), last evaluation timestamp, and the persisted fetch-set/projected-bytes result. fanned_out_at marks a fetch set already consumed by SubscriptionEvaluationFanOutService.';
COMMENT ON COLUMN subscription_evaluation_state.last_seen_lib_version_counter IS
    'The upstream lib.json version counter observed at the last evaluation of a content-libraries-lane subscription. NULL for every other lane, which has no lib.json to poll.';
COMMENT ON COLUMN subscription_evaluation_state.fetch_set_json IS
    'JSON array of the last evaluation''s fetch-set items ({depot_artifact_id, external_id, version, bundle_id, size_bytes}) -- the exact set SubscriptionEvaluationFanOutService reads to fan out one binaries-download job per item.';

GRANT SELECT ON subscriptions TO waypoint_download_runner;
GRANT SELECT, INSERT, UPDATE ON subscription_evaluation_state TO waypoint_download_runner;
