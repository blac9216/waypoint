-- Issue #560 (epic #558): Depot & Tokens credential management.
--
-- NOTE ON NUMBERING: PR #566 (compliance-content tables) was still open and
-- unmerged against `main` at the round-1 rebase of this PR, so 0034 was the
-- next free slot and this migration takes it. If #566 lands first and claims
-- 0034, whoever merges second must renumber this file (and bump
-- SchemaMigrationTests.ExpectedMigrationCount accordingly).
--
-- Two independent additions:
--
--   1. credentials.last_tested_at / credentials.expires_at -- the "last tested"
--      and "expiry" facts the Depot & Tokens screen needs (docs/ui/prototype/
--      README.md's "Broadcom Support Portal token ... expiry warning") that
--      today's schema has no column for. last_tested_at is stamped by every
--      CredentialTestJobHandler outcome (success or failure), for every
--      credential type -- it generalizes past depot-token because "when was
--      this last tested" is equally meaningful for a vcenter/nsx/ssh/token
--      credential and there is no reason to special-case the column to one
--      type. expires_at stays NULL until a real upstream response supplies a
--      date; nothing in this codebase may write a fabricated value into it
--      (CLAUDE.md: never invent a date) -- the API distinguishes "known future
--      expiry" from "unknown" by NULL-ness, never by a placeholder timestamp.
--
--   2. worker_registry.tool_present -- nullable (not boolean-with-default),
--      because "does the operator-installed vcf-download-tool binary exist"
--      (ADR-0015, ManagedToolPresenceChecker) is meaningful only for the
--      download-runner's heartbeat; a compliance-runner row has nothing to
--      report here and must stay NULL rather than default to a misleading
--      false. GET /downloads/readiness (DownloadsController) reads this
--      column back to answer the combined-readiness question #560 asks for
--      without duplicating #39's future install-flow scope.
ALTER TABLE credentials
    ADD COLUMN IF NOT EXISTS last_tested_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ NULL;

ALTER TABLE worker_registry
    ADD COLUMN IF NOT EXISTS tool_present BOOLEAN NULL;

COMMENT ON COLUMN credentials.last_tested_at IS
    'Issue #560: stamped by CredentialRepository.MarkTestOutcomeAsync on every credential-test outcome (success or failure), any credential_type.';
COMMENT ON COLUMN credentials.expires_at IS
    'Issue #560: NULL means unknown/never supplied by upstream -- never fabricated. Set only when a real depot response carries an expiry.';
COMMENT ON COLUMN worker_registry.tool_present IS
    'Issue #560: download-runner-only heartbeat field (NULL for compliance-runner rows) -- ManagedToolPresenceChecker.IsPresent() as of the last readiness probe.';

-- Runner-role grants for the two new credentials columns ---------------------------
--
-- credentials is column-scoped for the runner roles (migration 0025): the shared
-- SELECT grant enumerates an explicit column list, and the only UPDATE columns are
-- the consecutive-auth-failure queue-halt/health set the dispatcher writes. The two
-- columns this migration adds are exercised by CredentialTestJobHandler, which runs
-- as waypoint_compliance_runner (JobCapabilities.Compliance) -- so 0025's column
-- lists have drifted from what that handler's repository code now touches, the same
-- class of gap migration 0033 closed for run_secrets/targets/config_docs:
--
--   * SELECT (last_tested_at, expires_at): CredentialRepository.ProjectionSql (used
--     by GetAsync, which CredentialTestJobHandler calls at the start of every test
--     job) now selects c.last_tested_at, c.expires_at. Without these two columns in
--     the runner's SELECT grant the projection fails 42501 the moment a real runner
--     claims a credential-test job.
--   * UPDATE (last_tested_at): CredentialRepository.MarkTestOutcomeAsync issues
--     `UPDATE credentials SET health = $2, last_tested_at = now()` on every outcome
--     (success or failure). health was already granted in 0025; last_tested_at is
--     the new column and needs adding to the UPDATE grant.
--
-- Column-scoped to these two facts only: expires_at stays SELECT-only for runners
-- (never fabricated, and nothing in a runner writes it -- an upstream-supplied
-- expiry is an API-side write path), and name/owner/credential_type/username etc.
-- remain outside the runner's UPDATE grant exactly as 0025/0033 intend. Granted to
-- the compliance runner only (credential-test is pinned to JobCapabilities.Compliance);
-- the download runner has no code path reading or writing these columns.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT (last_tested_at, expires_at) ON credentials TO waypoint_compliance_runner;
GRANT UPDATE (last_tested_at) ON credentials TO waypoint_compliance_runner;
