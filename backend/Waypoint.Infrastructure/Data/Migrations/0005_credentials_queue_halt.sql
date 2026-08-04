-- PR #144 round 1 (issue #130): the consecutive-auth-failure halt blocked only rows
-- queued at the instant it tripped. A run fanned out for the same credential *after*
-- the halt created fresh 'queued' jobs the dispatcher could claim -- the halt was an
-- event, not a state. Same lesson as 0002/0003: make the bad row unrepresentable at
-- the database level instead of trusting every writer to remember to check.
--
-- The halt now persists on the credential itself; the trigger below coerces any
-- 'queued' write for a halted credential to 'blocked', regardless of which code path
-- produced it (fan-out insert, lease-recovery requeue, claim release, manual UPDATE).
-- Clearing queue_halted is the future credential-swap/unblock flow's job -- nothing
-- in the engine unsets it.
ALTER TABLE credentials
    ADD COLUMN IF NOT EXISTS queue_halted BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS queue_halted_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS queue_halted_at TIMESTAMPTZ NULL;

-- A halted credential must always carry the operator-visible why/when.
ALTER TABLE credentials
    DROP CONSTRAINT IF EXISTS credentials_queue_halt_reason_check;

ALTER TABLE credentials
    ADD CONSTRAINT credentials_queue_halt_reason_check
    CHECK (NOT queue_halted OR (queue_halted_reason IS NOT NULL AND queue_halted_at IS NOT NULL));

-- Mirrors 0003's run-abort trigger, including its locking argument: the FOR SHARE
-- here serializes a queued-job write against a concurrent halt's FOR UPDATE on the
-- same credentials row. If the job write locks first, the halt waits and then sweeps
-- the committed queued row via its queued-only UPDATE; if the halt locks first, this
-- waits and then observes the committed queue_halted = true. Either interleaving
-- leaves the job 'blocked', never claimably 'queued'.
CREATE OR REPLACE FUNCTION block_queued_job_under_halted_credential()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
	halted BOOLEAN;
	halt_reason TEXT;
BEGIN
	SELECT queue_halted, queue_halted_reason INTO halted, halt_reason
	FROM credentials WHERE id = NEW.credential_id FOR SHARE;
	IF halted THEN
		NEW.state := 'blocked';
		NEW.claimed_by := NULL;
		NEW.claimed_at := NULL;
		NEW.lease_expires_at := NULL;
		NEW.heartbeat_at := NULL;
		NEW.note := COALESCE(halt_reason, 'Credential queue halted');
	END IF;
	RETURN NEW;
END;
$$;

-- Named to sort after jobs_no_queued_under_aborted_run: for one row both BEFORE
-- triggers fire alphabetically, so an aborted run's coercion to 'cancelled' wins
-- (this trigger's WHEN then sees state <> 'queued' and skips) -- a job that is both
-- under an aborted run and a halted credential ends 'cancelled', not 'blocked'.
DROP TRIGGER IF EXISTS jobs_no_queued_under_halted_credential ON jobs;
CREATE TRIGGER jobs_no_queued_under_halted_credential
	BEFORE INSERT OR UPDATE ON jobs
	FOR EACH ROW
	WHEN (NEW.state = 'queued' AND NEW.credential_id IS NOT NULL)
	EXECUTE FUNCTION block_queued_job_under_halted_credential();
