-- Issue #1406 (epic #1182 "Subscriptions, retention & scheduling", split from
-- design record #1047, approved design #16 section 2): the retention DOMAIN MODEL
-- and its migration only -- grace-period policy, per-artifact retention state, and
-- pin metadata. No sweep logic (#1436), no manual-download dial mechanism (#1440),
-- no API surface (#1453) -- those are separate PRs building against the shape this
-- migration freezes. Distinct bounded context from the unrelated compliance-scan
-- `retention_policy` (0078) / `run_retention_holds` (0075) tables -- table names
-- below are prefixed `download_` to keep the two contexts unambiguous, per #1406's
-- own "Risks/Considerations" note.
--
-- download_retention_policies -------------------------------------------------------
-- One row per retention scope. scope_key 'default' is the seeded, always-present
-- appliance-wide fallback every artifact resolves to when no more specific policy
-- exists; a future per-subscription override (once #1421's Subscription entity
-- lands) is just another row keyed by the subscription's id rendered as text --
-- deliberately NOT a foreign key to a subscriptions table, since that table does
-- not exist yet in this schema (#1421 is still open) and this migration must not
-- block on it. grace_period_days is an integer day count for the same reasons
-- 0078's header gives for evidence_retention_days (no INTERVAL idiom used
-- elsewhere, trivial Npgsql/JSON round-trip, sweep only ever needs "now() minus N
-- days"). grace_max_refreshes bounds how many times the sweep (#1436) may push the
-- grace window back out when the artifact is re-referenced during grace, before it
-- must proceed to pending-purge regardless; 0 means "never refresh, prune at the
-- first expiry". manual_download_dial_default seeds the value #1440's per-artifact
-- dial starts from; the dial's own resolution/override mechanism is #1440's, this
-- column only carries the scope-level default.
CREATE TABLE IF NOT EXISTS download_retention_policies (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_key TEXT NOT NULL,
    grace_period_days INT NOT NULL DEFAULT 30,
    grace_max_refreshes INT NOT NULL DEFAULT 0,
    manual_download_dial_default TEXT NOT NULL DEFAULT 'review',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT download_retention_policies_scope_key_key UNIQUE (scope_key),
    CONSTRAINT download_retention_policies_grace_period_days_check CHECK (grace_period_days > 0),
    CONSTRAINT download_retention_policies_grace_max_refreshes_check CHECK (grace_max_refreshes >= 0),
    CONSTRAINT download_retention_policies_dial_check
        CHECK (manual_download_dial_default IN ('auto-prune', 'keep', 'review'))
);

INSERT INTO download_retention_policies (scope_key)
VALUES ('default')
ON CONFLICT (scope_key) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_download_retention_policies_updated_at
    BEFORE UPDATE ON download_retention_policies
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE download_retention_policies IS
    'Issue #1406: per-scope grace-window/dial-default retention configuration for the download/depot domain. scope_key ''default'' is the seeded appliance-wide fallback; not to be confused with the compliance-domain retention_policy (0078).';
COMMENT ON COLUMN download_retention_policies.scope_key IS
    'Opaque scope identifier: ''default'' for the appliance-wide fallback, or a subscription id (rendered as text, no FK -- the subscriptions table does not exist yet) for a per-subscription override.';

-- download_retained_content_state ---------------------------------------------------
-- One row per depot_artifacts row that has entered the retention lifecycle
-- (presence-based, same idiom run_retention_holds' header describes: a row exists
-- once the artifact is first evaluated by the -- not-yet-built -- sweep, absence
-- means "never evaluated", not "not retained"). FK to depot_artifacts(id), the
-- single source of artifact identity (approved design #16 section 1); ON DELETE
-- CASCADE mirrors every other artifact-scoped table's expectation that
-- depot_artifacts rows are the identity anchor and are not deleted independently
-- of their retention state. policy_id is nullable and RESTRICT-deleted (a policy
-- governing live state may not be removed out from under it); NULL means "resolves
-- to the 'default' scope's policy at evaluation time" rather than a frozen copy.
-- state is the five-value lifecycle from #1406's Proposed Changes; legal
-- transitions are enforced in the domain layer
-- (Waypoint.Core.Downloads.RetainedContentStateTransitions), not by a DB trigger --
-- same "C# owns the state graph, the CHECK only bounds the value set" split
-- JobStateMachine already establishes for jobs.state.
CREATE TABLE IF NOT EXISTS download_retained_content_state (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    depot_artifact_id UUID NOT NULL REFERENCES depot_artifacts (id) ON DELETE CASCADE,
    policy_id UUID NULL REFERENCES download_retention_policies (id) ON DELETE RESTRICT,
    state TEXT NOT NULL DEFAULT 'tracked',
    grace_started_at TIMESTAMPTZ NULL,
    pinned_by TEXT NULL,
    pinned_at TIMESTAMPTZ NULL,
    pin_note TEXT NULL,
    purged_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT download_retained_content_state_depot_artifact_id_key UNIQUE (depot_artifact_id),
    CONSTRAINT download_retained_content_state_state_check
        CHECK (state IN ('tracked', 'grace', 'pinned', 'pending-purge', 'purged')),
    CONSTRAINT download_retained_content_state_pin_check
        CHECK ((pinned_by IS NULL) = (pinned_at IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_download_retained_content_state_state
    ON download_retained_content_state (state);

CREATE OR REPLACE TRIGGER trg_download_retained_content_state_updated_at
    BEFORE UPDATE ON download_retained_content_state
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE download_retained_content_state IS
    'Issue #1406: per-artifact retention lifecycle state (tracked/grace/pinned/pending-purge/purged) and pin metadata for one depot_artifacts row. State-transition legality is enforced in Waypoint.Core.Downloads.RetainedContentStateTransitions, not by a DB trigger.';
COMMENT ON COLUMN download_retained_content_state.pinned_by IS
    'Actor who pinned this content, or NULL when not pinned. Pin/unpin is a #1453 API concern; this column only carries the resulting state.';

-- Runner grants: deliberately NONE, same posture 0075 (run_retention_holds) and
-- 0078 (retention_policy) document. This issue introduces the model and
-- persistence only -- no sweep job (#1436) reads or writes these tables yet, and
-- when it lands it does so as an API-process-owned background service (the same
-- posture EvidenceRetentionSweepHostedService already uses for the compliance
-- domain), not as a claimed runner job. Neither waypoint_compliance_runner nor
-- waypoint_download_runner needs access to either table introduced here.
