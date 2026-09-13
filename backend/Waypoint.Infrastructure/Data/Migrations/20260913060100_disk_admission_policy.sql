-- Issue #1529 (epic #1180, split from #1042): a durable, Admin-configurable disk
-- reserve backing the future admission check ("jobs refuse to start when projected
-- size exceeds free space minus a configurable reserve", #1042). This child only
-- makes the setting exist and be readable/writable at the repository layer -- the
-- admission decision itself, its call sites, and any API controller are the
-- enforcement child's surface (#1531).
--
-- disk_admission_policy --------------------------------------------------------------
-- Singleton row, the exact "id SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1)" idiom
-- retention_policy (migration 0078) and appliance_state (migration 0001) already
-- established for appliance-wide configurable state -- no reason to invent a second
-- shape for this one. reserve_bytes is a BIGINT byte count (not a percentage or an
-- INTERVAL-shaped value): #1042's body says "a configurable reserve", singular, and a
-- byte count compares directly against the free-space byte counts
-- IArtifactStoreDiskUsageProvider already produces with no unit conversion at the call
-- site. Default 10 GiB (10737418240 bytes) is an owner-chosen starting point, the same
-- kind of call retention's six-month default was (issue #1062's Summary) -- an Admin
-- may set any non-negative byte count once the enforcement child's API surface lands.
CREATE TABLE IF NOT EXISTS disk_admission_policy (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    reserve_bytes BIGINT NOT NULL DEFAULT 10737418240,
    updated_by TEXT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT disk_admission_policy_singleton_check CHECK (id = 1),
    CONSTRAINT disk_admission_policy_reserve_bytes_check CHECK (reserve_bytes >= 0)
);

INSERT INTO disk_admission_policy (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_disk_admission_policy_updated_at
    BEFORE UPDATE ON disk_admission_policy
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE disk_admission_policy IS
    'Issue #1529 (split from #1042): Admin-configurable disk-reserve setting (default 10 GiB) the future admission check compares free space against. Read/written exclusively by the API process -- see the withheld runner grants note below.';
COMMENT ON COLUMN disk_admission_policy.updated_by IS
    'Actor (username) who last changed the reserve, or NULL if it still holds the seeded default and has never been changed.';

-- Runner grants: deliberately NONE, same posture migration 0078 documented for
-- retention_policy and the same reasoning -- this is an Admin-configurable policy the
-- API makes visible and writable; no runner ever gates its own admission decision on
-- it (that decision, and any runner-side read it eventually needs, is #1531's surface
-- to add explicitly, not something to grant preemptively here). Left withheld from
-- both waypoint_compliance_runner and waypoint_download_runner the way retention_policy
-- (0078's header), run_retention_holds (0075's header), and appliance_state (0025's
-- header) already are.
