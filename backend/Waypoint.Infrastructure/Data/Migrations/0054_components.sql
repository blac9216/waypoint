-- Issue #732 (epic #726 Wave 2): the domain model distinguishing top-level connection
-- targets (a vCenter/NSX Manager/SSH connection boundary, `targets` table) from the
-- concrete executable compliance endpoints/components beneath them (a discovered
-- ESXi host, a discovered VM, a named VCSA sub-service, a whole-appliance SSH
-- component). ADR-0023 ("Stable compliance inventory and immutable component plans")
-- is the governing decision; docs/api-contract.md's planned `/targets/{id}/components`
-- section is the wire shape this schema must support without inventing fields the
-- contract does not already describe.
--
-- Scope discipline (epic #726 Wave 2 sequencing): this migration persists stable
-- component identity, inventory/source provenance (configured vs. discovered facts),
-- lifecycle (active/absent/retired), and catalog-compatibility read support. It does
-- NOT implement discovery-job scheduling changes (still #21/#732's own remainder),
-- plan/run integration (#733/#734), or credential-binding-at-component-granularity
-- (#735-#737 per ADR-0024). Those layers reference `components.id` once they land;
-- this migration only has to make that identity stable and queryable.
--
-- Identity (ADR-0023 "Identity and provenance"): component identity is
-- (parent_target_id, catalog_component_key, vendor_identity) -- NEVER name, address,
-- path, tree position, or sibling family key. `vendor_identity` is the authoritative
-- upstream object identity (a vSphere moref, an NSX component ID, ...) for a
-- catalog-declared component with an independent discoverable upstream object; for a
-- catalog-declared component with NO independent upstream object (a named VCSA
-- sub-service, a whole-appliance SSH target selector), `vendor_identity` is NULL and
-- (parent_target_id, parent_component_id, catalog_component_key) alone is
-- authoritative -- mirrors catalog_components' own parent_component_id +
-- component_key uniqueness (migration 0050) at the instance-identity layer.
--
-- Refresh/lifecycle (ADR-0023 "Refresh and lifecycle"): `lifecycle` is the closed
-- active|absent|retired vocabulary (ComponentLifecycleStates). A successful discovery
-- boundary that no longer observes a previously-active component moves it to
-- `absent` (identity/configuration retained, never deleted); continuous absence past
-- a global threshold moves it to `retired` (still queryable, excluded from `all`-scope
-- expansion per ADR-0023). Rediscovery of the same vendor identity reconnects
-- (`absent`/`retired` -> `active`) rather than creating a sibling row -- the whole
-- point of vendor-identity-keyed upsert. `continuous_absence_since` is set the
-- instant a successful boundary first fails to observe the component and cleared the
-- instant it reappears; the retirement threshold (a global Admin setting, initially
-- seven days) is application-layer policy, not enforced here.
--
-- Facts/provenance (ADR-0023 "Exact product version is mandatory... fails closed;
-- never guesses a winner"): `configured_fact`/`discovered_fact` are independent
-- JSONB observations (each carrying at minimum an exact version plus an observed-at
-- timestamp; shape owned by the application layer, not constrained here beyond
-- "valid JSON or NULL") -- both, one, or neither may be present. `fact_conflict` is a
-- generated readiness signal only when BOTH are present and disagree; the API never
-- collapses them into one winning value at this layer (docs/api-contract.md: "the
-- choice mutates neither source").
--
-- Historical-retention protection, matching migrations 0050/0052's convention: the
-- catalog_component_id FK (the compatible catalog component this component instance
-- currently resolves against, when known) is ON DELETE RESTRICT, and parent_target_id
-- is ON DELETE CASCADE (deleting a target has always cascaded its dependent rows --
-- same convention as inventory_items). parent_component_id self-references with
-- ON DELETE RESTRICT so a parent cannot be removed out from under children (there is
-- no component DELETE at all today -- see component_purge below -- but the
-- constraint shape is in place from day one, same rationale 0050's header gives).
CREATE TABLE IF NOT EXISTS components (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_target_id UUID NOT NULL REFERENCES targets (id) ON DELETE CASCADE,
    parent_component_id UUID NULL REFERENCES components (id) ON DELETE RESTRICT,
    catalog_component_id UUID NULL REFERENCES catalog_components (id) ON DELETE RESTRICT,
    catalog_component_key TEXT NOT NULL,
    vendor_identity TEXT NULL,
    display_name TEXT NOT NULL,
    lifecycle TEXT NOT NULL DEFAULT 'active',
    configured_fact JSONB NULL,
    discovered_fact JSONB NULL,
    fact_conflict BOOLEAN NOT NULL DEFAULT false,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    continuous_absence_since TIMESTAMPTZ NULL,
    retired_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT components_lifecycle_check CHECK (lifecycle IN ('active', 'absent', 'retired')),
    -- A component with a real upstream object is unique per (parent target, catalog
    -- key, vendor identity) -- the primary identity rule (ADR-0023). A component with
    -- NO independent upstream object (vendor_identity IS NULL -- a named VCSA
    -- sub-service or whole-appliance SSH selector) is instead unique per (parent
    -- target, parent component, catalog key), enforced by the partial index below
    -- since a plain UNIQUE constraint treats NULL vendor_identity as distinct every
    -- time (Postgres NULL-is-not-equal-to-NULL), which would silently allow duplicate
    -- sibling rows for the exact case this identity rule exists to prevent.
    CONSTRAINT components_vendor_identity_unique UNIQUE (parent_target_id, catalog_component_key, vendor_identity)
);

-- Enforces uniqueness for the no-independent-upstream-object case (vendor_identity
-- NULL) that the table-level UNIQUE constraint above cannot: parent identity + catalog
-- component key must be authoritative even without a vendor identity (ADR-0023: "For
-- a catalog-declared service with no independent upstream object, parent identity
-- plus catalog component key is authoritative"). COALESCE parent_component_id into a
-- nil UUID sentinel so two top-level (no parent component) NULL-vendor-identity rows
-- under the same target are still compared, not silently exempted by a second NULL.
CREATE UNIQUE INDEX IF NOT EXISTS idx_components_no_vendor_identity_unique
    ON components (parent_target_id, COALESCE(parent_component_id, '00000000-0000-0000-0000-000000000000'::uuid), catalog_component_key)
    WHERE vendor_identity IS NULL;

CREATE INDEX IF NOT EXISTS idx_components_parent_target_id ON components (parent_target_id);
CREATE INDEX IF NOT EXISTS idx_components_parent_component_id ON components (parent_component_id);
CREATE INDEX IF NOT EXISTS idx_components_catalog_component_id ON components (catalog_component_id);
CREATE INDEX IF NOT EXISTS idx_components_lifecycle ON components (lifecycle);

CREATE OR REPLACE TRIGGER trg_components_updated_at
    BEFORE UPDATE ON components
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- component_observations --------------------------------------------------------------
-- Immutable discovery/configuration provenance (docs/api-contract.md
-- `/components/{id}/observations`, ADR-0023 "Discovery and Admin configuration supply
-- catalog-declared facts as independent, timestamped provenance"). Every write to
-- components.configured_fact or components.discovered_fact -- whether from a
-- discovery refresh boundary or an Admin PUT -- appends one row here rather than
-- overwriting history, so a later dispute ("when did this component's version
-- change?") is answerable from data, not inferred from updated_at alone. Append-only
-- by convention (matches job_events/audit_log's intent) but not DB-trigger-enforced
-- in this slice -- no code path updates or deletes a row, and adding the same
-- reject-mutation trigger those two tables use is a natural, low-risk follow-up once
-- a second writer exists (#733's plan compiler will also read, never write, this
-- table).
CREATE TABLE IF NOT EXISTS component_observations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    component_id UUID NOT NULL REFERENCES components (id) ON DELETE CASCADE,
    source TEXT NOT NULL,
    observed_fact JSONB NOT NULL,
    outcome TEXT NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT component_observations_source_check CHECK (source IN ('configured', 'discovered')),
    CONSTRAINT component_observations_outcome_check CHECK (outcome IN ('recorded', 'conflict', 'absent'))
);

CREATE INDEX IF NOT EXISTS idx_component_observations_component_id ON component_observations (component_id, observed_at DESC);

-- No new runner grants: nothing in the compliance-runner or download-runner process
-- writes this schema in this slice. Discovery-job scheduling changes that would move
-- inventory materialization into the runner are explicitly deferred (issue #732's own
-- "NOT this slice" list); today's write path is API-side only (component upsert on
-- manual/API-triggered refresh, and the Admin configured_fact PUT), mirroring how
-- inventory_items (migration 0011) started API-side before any runner grant existed.
