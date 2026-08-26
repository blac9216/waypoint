-- Issue #832 (same defect class as PR #831's NULL-parent catalog_components race):
-- catalog_execution_profiles' promotion write used check-then-insert
-- (FindExecutionProfileAsync then CreateExecutionProfileAsync), guarded only by
-- 0050's plain UNIQUE (component_id, content_release_id) constraint. A plain UNIQUE
-- constraint backs SEQUENTIAL dedup fine (a second promotion attempt correctly hits
-- the constraint and throws), but check-then-insert across two AUTOCOMMIT connections
-- is not atomic: two concurrent compliance-runner replicas promoting the same
-- (component, content release) pair can both run FindExecutionProfileAsync, both see
-- nothing, and both attempt CreateExecutionProfileAsync -- the loser throws instead of
-- silently duplicating (0050's constraint DOES exist here, unlike the NULL-parent
-- components case), but throwing mid-promotion is still not the atomic-upsert
-- behavior ADR-0022's "additive-ingestion dedup guarantee" and this repo's established
-- convention (issue #831) require: a race should DEDUPE to one row, not fail one of
-- the two concurrent callers.
--
-- 0050's UNIQUE (component_id, content_release_id) constraint already provides real
-- DB-level uniqueness for this natural key (no NULL-column caveat here -- both
-- columns are NOT NULL) -- this migration does not add a new index. It exists only to
-- record the doc-comment intent match to 0050's `catalog_execution_profiles_unique`
-- constraint that CatalogRepository.PromoteCandidateAsync's rewritten atomic
-- INSERT ... ON CONFLICT DO UPDATE now binds to, and to host the class-killing
-- convention test's evidence trail alongside the fix. No schema change is required
-- because unlike the NULL-parent component case, the natural key here has no
-- NULL-distinctness gap: (component_id, content_release_id) are both NOT NULL FK
-- columns, so the existing plain UNIQUE constraint IS a valid ON CONFLICT target as-is.
--
-- Grants: migration 0051 already grants waypoint_compliance_runner SELECT, INSERT on
-- catalog_execution_profiles for the promotion path; the rewritten atomic upsert also
-- needs UPDATE for its re-promote DO UPDATE branch (mirroring catalog_components' own
-- SELECT/INSERT/UPDATE grant since PR #831), which 0051 did NOT grant (it only granted
-- SELECT, INSERT on this one table, matching the check-then-insert code that existed
-- then). This migration adds the missing UPDATE grant so the atomic upsert's DO UPDATE
-- branch does not ship green in tests (run under the full-privilege owner role) and
-- then fail 42501 live under the real waypoint_compliance_runner role. Same
-- role-must-exist guard as 0051 (fresh pgdata provisions the role via
-- deploy/postgres/initdb/01-runner-roles.sh before migrations run).
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT UPDATE ON catalog_execution_profiles TO waypoint_compliance_runner;
