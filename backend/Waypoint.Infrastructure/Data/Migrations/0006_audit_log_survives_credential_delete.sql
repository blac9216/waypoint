-- Epic #8 slice 3: deleting a credential 500'd on audit_log's plain FK -- the
-- audit trail was blocking the very deletions it should outlive. Audit rows are
-- the record of use (security.md goal 3); they must survive their subject.
-- ON DELETE SET NULL keeps the rows; the deletion itself is audited with the
-- credential's identity captured in detail JSON before the FK nulls out
-- (CredentialRepository.DeleteAsync writes both in one transaction).
ALTER TABLE audit_log
    DROP CONSTRAINT IF EXISTS audit_log_credential_id_fkey;

ALTER TABLE audit_log
    ADD CONSTRAINT audit_log_credential_id_fkey
    FOREIGN KEY (credential_id) REFERENCES credentials (id) ON DELETE SET NULL;
