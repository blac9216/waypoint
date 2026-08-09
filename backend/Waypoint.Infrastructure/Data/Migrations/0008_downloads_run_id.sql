-- Issue #10 (M1 vertical slice, ADR-0008 Option-A): DownloadsController creates
-- one run of N jobs per POST /downloads batch, not one run per artifact.
-- downloads.run_id is stored here so a downloads row resolves straight to its
-- owning batch run for the queue/UI view without widening
-- IJobQueueRepository with a get-by-job-id lookup; DELETE /downloads/{id}
-- cancels the individual job via the CancelJobAsync per-job cancel primitive,
-- not the whole run.
ALTER TABLE downloads
    ADD COLUMN IF NOT EXISTS run_id UUID NULL REFERENCES runs (id);

CREATE INDEX IF NOT EXISTS idx_downloads_run_id ON downloads (run_id) WHERE run_id IS NOT NULL;
