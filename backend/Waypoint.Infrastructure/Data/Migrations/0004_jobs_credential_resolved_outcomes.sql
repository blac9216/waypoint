-- Serve the consecutive-auth-failure window as a deterministic resolved-outcome scan.
-- Unfinished jobs carry no outcome and must never displace completed attempts.
CREATE INDEX IF NOT EXISTS idx_jobs_credential_resolved_outcomes
	ON jobs (credential_id, finished_at DESC, id DESC)
	WHERE credential_id IS NOT NULL AND finished_at IS NOT NULL;
