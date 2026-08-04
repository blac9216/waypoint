-- A queued job belonging to an aborted run must be impossible regardless of writer.
-- Coerce instead of raising so one aborted row cannot abort an entire recovery batch.
CREATE OR REPLACE FUNCTION cancel_queued_job_under_aborted_run()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
	IF EXISTS (SELECT 1 FROM runs WHERE id = NEW.run_id AND state = 'aborted') THEN
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
