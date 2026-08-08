-- Issue #10 (M1 vertical slice): DownloadsController fans each queued artifact
-- out as its own single-job run (one run per artifact, not one run for the
-- whole batch -- see DownloadsController's class doc comment for why: it
-- keeps "cancel one artifact's download" scoped to that artifact's own run,
-- reusing IJobQueueRepository.AbortRunAsync as-is rather than adding a new
-- cancel-one-job-of-a-run primitive). DELETE /downloads/{id} needs to resolve
-- a downloads row straight to the run to abort; job_id alone does not carry
-- that (jobs.run_id is not queryable through IJobQueueRepository by job id),
-- so run_id is stored redundantly here at creation time instead of widening
-- the job-queue repository interface (and every fake that implements it) for
-- one narrow lookup.
ALTER TABLE downloads
    ADD COLUMN IF NOT EXISTS run_id UUID NULL REFERENCES runs (id);

CREATE INDEX IF NOT EXISTS idx_downloads_run_id ON downloads (run_id) WHERE run_id IS NOT NULL;
