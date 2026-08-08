-- Closes issue #124: 0002 (issue #107) made state='running' with a NULL
-- lease_expires_at unrepresentable, but left 'attesting' and 'converting' --
-- the Standard-shape scan pipeline's other two active, worker-owned states
-- (see JobShape.Standard / JobStateMachine) -- unconstrained. A job hand-advanced
-- (or advanced by a future, buggy handler) straight into 'attesting'/'converting'
-- with a NULL lease is exactly as stranded as the original #107 bug: not 'queued'
-- (so unclaimable by the claim query) and not caught by idx_jobs_lease_recovery's
-- state = 'running' predicate (so unrecoverable by the sweep either).
--
-- #124 was deliberately deferred out of #107/0002 because no handler could reach
-- these two states yet (scan, the only Standard-shape job type, had no
-- implementation) -- see 0001's jobs table comment and #124's "Discovery" section.
-- It is implemented now, independently of that handler landing, because the CHECK
-- constrains rows regardless of which handler eventually writes them, and #124 is
-- the designated trigger for the first PR that introduces one.
--
-- Same idiom as 0002: DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT, replacing the
-- narrower 'running'-only CHECK from 0002 with one that also covers 'attesting'
-- and 'converting'. No other state changes: queued/failed/auth-failed/blocked/
-- cancelled/done/uploaded still carry no lease and are not claimed by anyone, so
-- they remain unconstrained by this CHECK, same as 0002 left them.
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_running_requires_lease_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_running_requires_lease_check
    CHECK (state NOT IN ('running', 'attesting', 'converting') OR lease_expires_at IS NOT NULL);
