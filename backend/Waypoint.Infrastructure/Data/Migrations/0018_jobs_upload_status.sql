-- Issue #311 (second slice of the #25 split): per-target STIG Manager CKL
-- upload status. 0017 (#310) took the previous free slot; #308 (NSX
-- transport, parallel lane) was told none-expected/0018-if-forced and had
-- not added a migration as of this PR, so this PR owns 0018.
--
-- `jobs.upload_status` is deliberately independent of `jobs.state`/`stage`:
-- ADR-0012's state machine already spends `uploaded` as the Standard shape's
-- terminal meaning "artifacts ready" (PR #302's note, unchanged by this
-- issue -- see ScanJobHandler's doc comment), so the actual HTTP upload
-- outcome cannot be encoded as a new state without contradicting that
-- accepted contract. A separate nullable column lets the convert stage
-- record uploaded/failed/conflict *after* the job has already reached its
-- terminal state, and lets a retry (issue #297's future endpoint shape,
-- reused narrowly here) flip the column without touching `state`/`stage` or
-- re-running the state machine at all.
--
-- NULL means "no upload was ever attempted" (SRG-only/`done`-terminating
-- jobs per #24/#309, or a job that never reached convert) -- the artifacts
-- API's existing state-derived fallback still applies for those rows.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS upload_status TEXT NULL;

ALTER TABLE jobs DROP CONSTRAINT IF EXISTS jobs_upload_status_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_upload_status_check CHECK (upload_status IN (
    'pending', 'uploaded', 'failed', 'conflict'
));

-- jobs.upload_detail carries the short, redacted human-readable reason for a
-- failed/conflict outcome (mirrors jobs.note's redaction discipline) -- shown
-- alongside the pill on Results without needing a job.log fetch.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS upload_detail TEXT NULL;
