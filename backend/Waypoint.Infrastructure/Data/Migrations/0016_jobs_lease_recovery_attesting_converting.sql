-- Closes issue #282 (the expired-lease sibling of #124): idx_jobs_lease_recovery's
-- partial predicate and JobQueueRepository.RecoverSql's WHERE clause both hard-coded
-- state = 'running', so a worker that crashed mid-'attesting'/'converting' left a row
-- whose lease had gone from valid to expired -- not 'queued' (unclaimable), not caught
-- by the sweep either (its predicate never matched). #124/0015 made a NULL-lease
-- attesting/converting row unrepresentable; this migration is the companion fix for
-- the *expired*-lease crashed-worker case #124 did not cover, deferred to #293/#282's
-- resolution because the correct requeue target for a partially-attested/converted job
-- is the stage-per-execution dispatcher design landing alongside this migration (see
-- JobQueueRepository.RecoverSql and JobDispatcherHostedService in the same PR): recovery
-- now requeues to 'queued' with the job's stage marker intact, so the resumed claim
-- re-enters the pipeline at that stage rather than restarting from 'running'.
--
-- Same DROP/CREATE-IF-NOT-EXISTS idiom as the rest of this migration series: widen the
-- index's partial predicate from state = 'running' to the three actively-worked,
-- lease-bearing states (0015's CHECK already recognizes these three as a unit).
DROP INDEX IF EXISTS idx_jobs_lease_recovery;

CREATE INDEX IF NOT EXISTS idx_jobs_lease_recovery
    ON jobs (lease_expires_at)
    WHERE state IN ('running', 'attesting', 'converting');
