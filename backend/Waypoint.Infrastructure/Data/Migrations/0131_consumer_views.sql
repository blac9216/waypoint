-- Issue #1464 (epic #1183, split from design record #1162's closing comment):
-- ConsumerView -- the named platform-set definitions operators will later use to
-- drive filtered ESX metadata generation (Child B) and serving (Child D). This
-- migration lands only the shared model/persistence; no generation or serving logic
-- exists yet. Slot 0131 -- the issue body's pre-assigned 0119 was reassigned at pick
-- time (2026-09-07, see the issue's own scope-note comment) because 0117-0130 landed
-- or were claimed by in-flight branches by then; verified against both the
-- migrations directory and `gh pr list --state open` immediately before use, same
-- discipline as 0117's and 0129's own header notes.
--
-- consumer_views --------------------------------------------------------------------
-- One row per named view: `name` (operator-chosen, unique), `platforms` (an ordered
-- TEXT[] of platform keys, empty explicitly allowed -- see below), and `is_default`
-- marking the single row that represents the unfiltered/default view -- modeled as a
-- boolean singleton on an ordinary row, never a special-cased absence (issue #1464
-- AC, Proposed Changes: "model it as a well-known sentinel row or a boolean
-- singleton, not a special-cased absence").
--
-- "Exactly one default at any time" is TWO separate guarantees layered together, and
-- neither alone is sufficient:
--   1. AT MOST one -- enforced HERE via the partial unique index below
--      (idx_consumer_views_default_unique), the same "at most one X" idiom as
--      migration 0055's active-baseline-per-profile index and 0059's
--      current-trust-policy-per-scope index. This forbids a SECOND default; it does
--      nothing to forbid ZERO defaults.
--   2. AT LEAST one -- enforced by seeding the well-known default row below
--      (id 00000000-0000-0000-0000-000000000001, name 'Default (unfiltered)') and by
--      the API/repository refusing to delete that row or clear its `is_default` while
--      it is the sole default (409 `default_required`,
--      ConsumerViewsController.Delete/Update via
--      ConsumerViewRepository.DeleteAsync/UpdateAsync) -- so the default can only be
--      MOVED (mark a different row default, which the "at most one" guard above still
--      polices), never removed outright.
-- The API additionally rejects a second default with 409 `default_already_set`
-- before ever reaching the database (belt and suspenders --
-- ConsumerViewsController.EnsureNoOtherDefaultAsync), so all of these are
-- independently provable in CI per the issue's own AC.
--
-- Platform-key validation (values must be members of the static #1156-reconciliation
-- vocabulary -- e.g. `embeddedEsx-7.0-INTL` -- see
-- Waypoint.Core.Downloads.ConsumerViewPlatformVocabulary) happens at the API layer
-- only, matching esx_acquisition_subscriptions' (migration 0117) precedent: the set
-- of valid values is not known to this schema, so `platforms` stays a plain TEXT[]
-- with no CHECK against it. An EMPTY `platforms` array is explicitly allowed
-- (ConsumerViewsController.ValidateAgainstVocabulary returns early on an empty list)
-- and means "all platforms, no filtering" -- the unfiltered vendor store the seeded
-- default row represents; a non-default view with an empty set is equally legal and
-- has the identical meaning for that view.
--
-- why: the vocabulary here is a static list hand-copied from #1156's reconciliation,
-- not sourced from a shared parser -- if #1039 (version comparator, Wave 1) later
-- ships a shared platform-key comparator, this validation should be revisited to
-- reuse it instead of duplicating the vocabulary (see the doc comment on
-- Waypoint.Core.Downloads.ConsumerViewPlatformVocabulary).
--
-- No runner grant in this migration: consumer views are an API-only, Admin-managed
-- dial (decision R2-10: "serving/auth dials" are Admin) with no download-runner or
-- compliance-runner consumer yet -- Children B/D, which will read this table to drive
-- generation/serving, are out of scope here and grant themselves whatever runner
-- access they need when they land, same "no grant in the model slice" precedent as
-- migration 0117's esx_acquisition_subscriptions and 0059's trust_policies.
CREATE TABLE IF NOT EXISTS consumer_views (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    platforms TEXT[] NOT NULL DEFAULT '{}',
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT consumer_views_name_not_blank_check CHECK (btrim(name) <> '')
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_consumer_views_name_unique ON consumer_views (name);

-- AT MOST one default/unfiltered view at any time (issue #1464 AC, half 1 of 2 -- see
-- the header). Never more than one row may have is_default = true; a zero-default
-- state is a separate, non-database concern -- see the seeded row and API/repository
-- refusals below for the "at least one" half.
CREATE UNIQUE INDEX IF NOT EXISTS idx_consumer_views_default_unique
    ON consumer_views (is_default)
    WHERE is_default;

-- AT LEAST one default/unfiltered view at any time (issue #1464 AC, half 2 of 2): the
-- well-known shipped default row. Fixed id so tests and any future migration can
-- reference it unambiguously; ON CONFLICT DO NOTHING makes this idempotent across
-- re-runs. Empty `platforms` means "all platforms, no filtering" (see above).
INSERT INTO consumer_views (id, name, platforms, is_default)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default (unfiltered)', '{}', true)
ON CONFLICT (id) DO NOTHING;

COMMENT ON TABLE consumer_views IS
    'Issue #1464: operator-defined named ESX platform-set views. AT MOST one row may have is_default = true (idx_consumer_views_default_unique, database); EXACTLY one at any time is enforced by the seeded default row (id 00000000-0000-0000-0000-000000000001) plus the API/repository refusing to delete it or clear its is_default while it is the sole default (409 default_required). Model/API only -- no generation or serving logic reads this table yet.';
COMMENT ON COLUMN consumer_views.platforms IS
    'Ordered platform keys (e.g. embeddedEsx-7.0-INTL), validated at write time against the static vocabulary in Waypoint.Core.Downloads.ConsumerViewPlatformVocabulary -- never a schema-level CHECK, matching esx_acquisition_subscriptions.selected_platforms (migration 0117). Empty is explicitly allowed and means "all platforms, no filtering."';
COMMENT ON COLUMN consumer_views.is_default IS
    'Marks the single view representing the unfiltered/default store. Modeled as a boolean singleton on an ordinary row, never a special-cased absence -- a zero-default state is unreachable via the API/repository (see idx_consumer_views_default_unique for at-most-one and the seeded row plus delete/clear refusals for at-least-one).';

CREATE OR REPLACE TRIGGER trg_consumer_views_updated_at
    BEFORE UPDATE ON consumer_views
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
