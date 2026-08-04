-- A queued job belonging to an aborted run must be impossible regardless of writer.
-- Coerce instead of raising so one aborted row cannot abort an entire recovery batch.
CREATE OR REPLACE FUNCTION cancel_queued_job_under_aborted_run()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
	run_state TEXT;
BEGIN
	-- Serialize a queued job write with a concurrent run-state update. If the job
	-- takes the share lock first, the abort trigger below sweeps it; if abort takes
	-- the row lock first, this waits and then observes the committed aborted state.
	SELECT state INTO run_state FROM runs WHERE id = NEW.run_id FOR SHARE;
	IF run_state = 'aborted' THEN
		NEW.state := 'cancelled';
		NEW.claimed_by := NULL;
		NEW.claimed_at := NULL;
		NEW.lease_expires_at := NULL;
		NEW.heartbeat_at := NULL;
		NEW.finished_at := COALESCE(NEW.finished_at, now());
		NEW.note := 'Cancelled: run aborted';
	END IF;
	RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS jobs_no_queued_under_aborted_run ON jobs;
CREATE TRIGGER jobs_no_queued_under_aborted_run
	BEFORE INSERT OR UPDATE ON jobs
	FOR EACH ROW
	WHEN (NEW.state = 'queued' AND NEW.run_id IS NOT NULL)
	EXECUTE FUNCTION cancel_queued_job_under_aborted_run();


CREATE OR REPLACE FUNCTION cancel_queued_jobs_when_run_aborted()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
	IF NEW.state = 'aborted' AND OLD.state IS DISTINCT FROM 'aborted' THEN
		UPDATE jobs SET
			state = 'cancelled',
			claimed_by = NULL,
			claimed_at = NULL,
			lease_expires_at = NULL,
			heartbeat_at = NULL,
			finished_at = COALESCE(finished_at, now()),
			note = 'Cancelled: run aborted'
		WHERE run_id = NEW.id AND state = 'queued';
	END IF;
	RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS runs_abort_cancels_queued_jobs ON runs;
CREATE TRIGGER runs_abort_cancels_queued_jobs
	BEFORE UPDATE OF state ON runs
	FOR EACH ROW
	WHEN (FALSE)
	EXECUTE FUNCTION cancel_queued_jobs_when_run_aborted();
