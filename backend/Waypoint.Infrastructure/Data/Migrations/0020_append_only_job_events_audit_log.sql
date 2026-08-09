-- Issue #106: job_events and audit_log are documented "append-only" (0001's header
-- comment, docs/api-contract.md's schema sketch) but nothing enforced it -- UPDATE and
-- DELETE both silently succeeded against a real Postgres 16 container. For audit_log
-- that's not a nicety: docs/security.md control 4 ("Decrypt audit trail") is the
-- compensating control that makes the service/shared credential tier acceptable, and a
-- trail the compromised component can edit compensates for nothing.
--
-- Enforcement is a BEFORE UPDATE/DELETE trigger that RAISEs, not a rule that silently
-- swallows the statement (CREATE RULE ... DO INSTEAD NOTHING) and not a role/grant
-- split (no separate migration-vs-runtime role exists in this deployment yet -- see
-- #106's "Possible Fixes"). RAISE is chosen deliberately: a buggy caller that attempts
-- a mutation should see a failed statement, not silent data loss disguised as success.
--
-- job_events: no code path ever legitimately mutates a committed row (confirmed against
-- JobEventPublisher.cs, the only writer -- it only INSERTs). Every UPDATE and DELETE is
-- blocked, unconditionally.
CREATE OR REPLACE FUNCTION job_events_block_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'job_events is append-only: % is not permitted (seq=%)',
        TG_OP, COALESCE(OLD.seq, NEW.seq);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_job_events_block_update
    BEFORE UPDATE ON job_events
    FOR EACH ROW EXECUTE FUNCTION job_events_block_mutation();

CREATE OR REPLACE TRIGGER trg_job_events_block_delete
    BEFORE DELETE ON job_events
    FOR EACH ROW EXECUTE FUNCTION job_events_block_mutation();

-- audit_log: almost the same story, with one carve-out. 0006 added
-- audit_log.credential_id ... REFERENCES credentials (id) ON DELETE SET NULL so a
-- credential delete doesn't 500 against the very audit trail that should outlive it
-- (CredentialRepository.DeleteAsync). That FK action performs its nulling as a real
-- UPDATE statement against audit_log, executed by the Postgres engine itself, not by
-- app code -- and it must keep working. So the trigger below permits exactly that
-- shape of UPDATE (credential_id changing from non-null to NULL, every other column
-- byte-for-byte unchanged) and blocks every other UPDATE and all DELETEs.
--
-- This is intentionally structural rather than "trust the caller": it doesn't matter
-- whether the nulling UPDATE was issued by the FK action or by an app bug that
-- happened to null credential_id and nothing else -- either way the row's meaning
-- (what happened, who did it, when) is preserved, which is what the append-only
-- guarantee is actually protecting.
CREATE OR REPLACE FUNCTION audit_log_block_mutation()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE'
        AND NEW.id IS NOT DISTINCT FROM OLD.id
        AND NEW.event_type IS NOT DISTINCT FROM OLD.event_type
        AND NEW.actor IS NOT DISTINCT FROM OLD.actor
        AND NEW.job_id IS NOT DISTINCT FROM OLD.job_id
        AND NEW.run_id IS NOT DISTINCT FROM OLD.run_id
        AND NEW.detail IS NOT DISTINCT FROM OLD.detail
        AND NEW.occurred_at IS NOT DISTINCT FROM OLD.occurred_at
        AND OLD.credential_id IS NOT NULL
        AND NEW.credential_id IS NULL
    THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'audit_log is append-only: % is not permitted (id=%)',
        TG_OP, COALESCE(OLD.id, NEW.id);
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER trg_audit_log_block_update
    BEFORE UPDATE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_block_mutation();

CREATE OR REPLACE TRIGGER trg_audit_log_block_delete
    BEFORE DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_block_mutation();
