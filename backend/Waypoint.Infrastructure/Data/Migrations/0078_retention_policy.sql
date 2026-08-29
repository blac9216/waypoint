-- Issue #1062 (epic #726 sections 6/7): Admin-configurable scan-evidence retention
-- period backing the automated sweep that drives past-retention compliance runs
-- through the existing `RunPurgeService.PurgeRunAsync` path (ADR-0019). Section 6:
-- "retention defaults to six months, Admin-configurable". Section 7: "only Admin
-- manages retention administration".
--
-- retention_policy ------------------------------------------------------------------
-- Singleton row, same "id SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1)" shape
-- appliance_state (migration 0001) already established for exactly one piece of
-- appliance-wide configurable state -- no reason to invent a second idiom for this
-- one. evidence_retention_days is an integer day count rather than an INTERVAL
-- column: nothing else in this schema uses INTERVAL yet, an integer is trivial to
-- round-trip through Npgsql/System.Text.Json with no custom mapping, and the sweep
-- only ever needs "now() minus N days" -- INTERVAL's extra precision (months, exact
-- calendar arithmetic) buys nothing here. Default 180 (~6 months) matches the
-- owner's six-month default decision (issue #1062's Summary); an Admin may set any
-- positive day count via the retention-policy API.
CREATE TABLE IF NOT EXISTS retention_policy (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    evidence_retention_days INT NOT NULL DEFAULT 180,
    updated_by TEXT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT retention_policy_singleton_check CHECK (id = 1),
    CONSTRAINT retention_policy_evidence_retention_days_check CHECK (evidence_retention_days > 0)
);

INSERT INTO retention_policy (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_retention_policy_updated_at
    BEFORE UPDATE ON retention_policy
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE retention_policy IS
    'Issue #1062: Admin-configurable evidence retention period (default 180 days / ~6 months). Read/written exclusively by the API process -- see the withheld runner grants note below.';
COMMENT ON COLUMN retention_policy.updated_by IS
    'Actor (username) who last changed the retention period, or NULL if it still holds the seeded default and has never been changed.';

-- Runner grants: deliberately NONE, same posture migration 0075 documented for
-- run_retention_holds and the same reasoning -- reading the configured retention
-- period and driving the sweep off it is exclusively an API-side responsibility
-- (RetentionPolicyController / EvidenceRetentionSweepHostedService, over the same
-- owner-privileged connection every other Admin-reasoned singleton in this codebase
-- already uses). Neither waypoint_compliance_runner nor waypoint_download_runner
-- ever needs to read or write this table: the runner never decides what gets purged
-- or when, it only executes the artifact-deletion job the API already enqueued via
-- RunPurgeService, exactly like an operator-initiated purge. Left withheld from both
-- roles the way appliance_state (0025's header) and run_retention_holds (0075's
-- header) already are.
