-- Issue #1421 (epic #1182 "Subscriptions, retention & scheduling", split from
-- design record #1045; approved design #16 section 2, ADR-0028): the Subscription
-- and Preset DOMAIN MODEL and its migration only -- no evaluation-job wiring
-- (#1046), no API surface (#1450/#1453), no cross-lane supersession (#1437). Slot
-- 0104 pre-assigned 2026-08-30, inside the same 0082-0106 numbering gap 0107's own
-- header reserves for concurrently in-flight sibling issues.
--
-- Scope note at pick time (2026-09-07): this repo has no EF Core -- there is no
-- WaypointDbContext. Persistence is these tables plus Npgsql repository classes
-- (Waypoint.Infrastructure.Subscriptions.SubscriptionRepository/PresetRepository)
-- behind Waypoint.Core interfaces, mirroring every other domain in this schema.
--
-- presets ----------------------------------------------------------------------------
-- One row per preset: a shipped, read-only starting point (stack x generation,
-- is_custom = false, curated in-repo content refreshed by appliance updates --
-- ADR-0028's "Presets are appliance-shipped content") or an operator's
-- clone-to-custom (is_custom = true, source_preset_id names the preset it was
-- cloned from). generation is TEXT, not an integer column, so no release-line
-- number is ever hardcoded in a migration or in C# (epic #16 decision 5 / this
-- issue's own AC4) -- the appliance's shipped-preset seed data supplies the actual
-- values, not this migration. line_granularity is the tracking-line vocabulary
-- ADR-0028's Decision fixes: subminor/minor/major, mirroring
-- Waypoint.Core.Versions.VersionLineGranularity 1:1 (issue #1421 AC amendment
-- 2026-09-07, review round 1: ADR-0028 names exactly three widths -- "adopting a
-- subscription pulls the whole release (every bundle/binary)" is artifact
-- completeness, not a fourth tracking width, so no whole-release value exists
-- here). anchor_version is nullable because a from-scratch custom preset (no
-- source_preset_id) may exist before an operator has picked a starting version;
-- a shipped preset always carries one. stack is a second closed vocabulary
-- (VCF/VVF), mirrored by Waypoint.Core.Subscriptions.PresetStacks.All and proven
-- against this CHECK by SubscriptionsConstraintDriftTests (review round 1 F2),
-- the same convention subscriptions.lane below follows against RepoStores.All.
-- The two lineage CHECKs below enforce, in the schema rather than only in prose,
-- the invariant this table's own COMMENT ON COLUMN source_preset_id states
-- (review round 1 F4): a shipped preset (is_custom = false) may never carry
-- lineage, and a preset may never name itself as its own clone source. Neither
-- CHECK catches a longer cycle (A clones from B, B clones from A) -- Postgres has
-- no portable per-row CHECK for that, and no evaluation logic in this PR walks
-- lineage chains yet, so a longer cycle is left undetected here.
CREATE TABLE IF NOT EXISTS presets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    stack TEXT NOT NULL,
    generation TEXT NOT NULL,
    name TEXT NOT NULL,
    line_granularity TEXT NOT NULL,
    anchor_version TEXT NULL,
    is_custom BOOLEAN NOT NULL DEFAULT false,
    source_preset_id UUID NULL REFERENCES presets (id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT presets_stack_check CHECK (stack IN ('VCF', 'VVF')),
    CONSTRAINT presets_line_granularity_check
        CHECK (line_granularity IN ('subminor', 'minor', 'major')),
    CONSTRAINT presets_lineage_requires_custom_check
        CHECK (source_preset_id IS NULL OR is_custom = true),
    CONSTRAINT presets_source_preset_id_not_self_check
        CHECK (source_preset_id IS NULL OR source_preset_id <> id)
);

CREATE INDEX IF NOT EXISTS idx_presets_is_custom ON presets (is_custom);

CREATE OR REPLACE TRIGGER trg_presets_updated_at
    BEFORE UPDATE ON presets
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE presets IS
    'Issue #1421: shipped read-only (is_custom = false) or operator clone-to-custom (is_custom = true, source_preset_id) subscription starting points. stack/generation/name/anchor_version are appliance-shipped seed data, never hardcoded here.';
COMMENT ON COLUMN presets.generation IS
    'Data, not a literal: e.g. the text "9.0". No migration or C# source hardcodes a release generation (epic #16 decision 5).';
COMMENT ON COLUMN presets.source_preset_id IS
    'The preset this row was cloned from, when is_custom = true and the clone has known lineage. NULL for a shipped preset or a from-scratch custom preset -- enforced by presets_lineage_requires_custom_check (a non-NULL value requires is_custom = true) and presets_source_preset_id_not_self_check (a preset may never name itself).';

-- subscriptions ------------------------------------------------------------------------
-- One row per operator-durable "keep this scope current" expression (ADR-0028): a
-- product/lane pair tracked at a declared line_granularity, anchored at
-- anchor_version (the version whose line -- per Waypoint.Core.Subscriptions
-- ISubscriptionLineEvaluator, which wraps the #1039 comparator -- other catalog
-- versions are compared against). lane is the closed acquisition-lane vocabulary
-- Waypoint.Core.Secrets.RepoStores.All already fixes for repo serving
-- (depot/umds/photon/vmtools/vks/content-libraries) -- reused rather than a new
-- vocabulary, since a subscription always targets exactly one of those lanes.
-- preset_id is nullable: a subscription adopted from a preset carries the
-- originating preset (ON DELETE SET NULL -- a later preset deletion never cascades
-- into deleting a live subscription); a from-scratch subscription has none.
-- refresh_window_days/retention_override_days are the "per-lane dials" this
-- issue's Proposed Changes names (UMDS/VKS time-window and retention/grace
-- overrides) -- both nullable, so NULL means "use the lane's/#1406's own default",
-- never a fabricated number. Evaluation-job wiring (which rows actually get
-- evaluated, on what schedule) is #1046/#1436's slice, not this one's.
CREATE TABLE IF NOT EXISTS subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    product TEXT NOT NULL,
    lane TEXT NOT NULL,
    line_granularity TEXT NOT NULL,
    anchor_version TEXT NOT NULL,
    preset_id UUID NULL REFERENCES presets (id) ON DELETE SET NULL,
    refresh_window_days INT NULL,
    retention_override_days INT NULL,
    is_enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT subscriptions_lane_check
        CHECK (lane IN ('depot', 'umds', 'photon', 'vmtools', 'vks', 'content-libraries')),
    CONSTRAINT subscriptions_line_granularity_check
        CHECK (line_granularity IN ('subminor', 'minor', 'major')),
    CONSTRAINT subscriptions_refresh_window_days_check CHECK (refresh_window_days IS NULL OR refresh_window_days > 0),
    CONSTRAINT subscriptions_retention_override_days_check CHECK (retention_override_days IS NULL OR retention_override_days > 0)
);

CREATE INDEX IF NOT EXISTS idx_subscriptions_product_lane ON subscriptions (product, lane);
CREATE INDEX IF NOT EXISTS idx_subscriptions_preset_id ON subscriptions (preset_id);

CREATE OR REPLACE TRIGGER trg_subscriptions_updated_at
    BEFORE UPDATE ON subscriptions
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE subscriptions IS
    'Issue #1421: durable per-product/lane "keep this scope current" expression (ADR-0028). Line-membership evaluation is Waypoint.Core.Subscriptions.ISubscriptionLineEvaluator, wrapping the #1039 comparator; evaluation-job wiring is #1046''s slice.';
COMMENT ON COLUMN subscriptions.lane IS
    'The Waypoint.Core.Secrets.RepoStores.All acquisition-lane vocabulary (depot/umds/photon/vmtools/vks/content-libraries), reused rather than duplicated.';
COMMENT ON COLUMN subscriptions.anchor_version IS
    'The vendor version string whose line (per line_granularity) this subscription tracks. Parsed via Waypoint.Core.Versions.ProductVersionParser at evaluation time, never pre-parsed into this row.';

-- Runner grants: deliberately NONE. This issue introduces the model and
-- persistence only -- no runner-claimed job reads or writes subscriptions or
-- presets yet (the evaluation job, #1046, and any lane sync handler that
-- consults a subscription, are both still open). Following the 0100/0107
-- precedent: the first consumer that actually needs runner-side access ships its
-- own GRANT migration alongside its own runner-role-connects test when it lands.
