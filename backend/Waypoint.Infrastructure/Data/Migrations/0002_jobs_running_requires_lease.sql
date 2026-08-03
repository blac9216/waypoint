-- Closes issue #107: a jobs row with state='running' and lease_expires_at IS NULL is
-- reachable by neither the claim query (filters state='queued') nor the recovery
-- sweep (filters lease_expires_at < now(), and NULL < now() is NULL, not true), so it
-- is a permanently stranded queue slot with no operator-visible signal.
--
-- Option A from #107 (preferred over widening the recovery sweep's WHERE clause):
-- make the state unrepresentable rather than merely handled. The claim path (#128,
-- JobQueueRepository.ClaimJobAsync) will only ever set state='running' in the same
-- atomic UPDATE that stamps lease_expires_at -- this CHECK is the backstop that keeps
-- that true even if a future code path forgets, or something writes to the row outside
-- the repository (a manual UPDATE, a future migration, an operator console query).
-- Landing the CHECK before the first writer is deliberate: the constraint is then a
-- precondition every later slice is built against, not a retrofit.
--
-- Every other state is unconstrained by this CHECK on purpose: only 'running' is the
-- state #107 identified as stranding-prone (queued/failed/etc. carry no lease and are
-- not claimed by anyone). 'attesting'/'converting' also represent active work without
-- their own lease-tied CHECK -- tracked separately in #124, which blocks the first PR
-- introducing a handler that can reach those states -- rather than folded in here,
-- since #107 is scoped to 'running' only.
--
-- NOT VALID + VALIDATE CONSTRAINT is unnecessary here: schema v1 (#4) has no writers
-- yet (see #107 "Impact: None today"), so there are no live rows to conflict with --
-- a plain CHECK added now costs nothing extra over the NOT VALID dance #107
-- anticipated for "later".
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_running_requires_lease_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_running_requires_lease_check
    CHECK (state <> 'running' OR lease_expires_at IS NOT NULL);
