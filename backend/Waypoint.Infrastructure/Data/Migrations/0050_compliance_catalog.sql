-- Issue #728 (epic #726, Wave 1): the persistent normalized compliance catalog.
-- ADR-0022 ("Closed compliance catalog and atomic content lifecycle") is the
-- governing decision; docs/compliance-parity.md's "Closed capability vocabulary"
-- table and sibling source-capability matrix are the shape this schema must
-- faithfully represent without lossy target-kind inference.
--
-- Scope discipline (epic #726 Wave 1 sequencing): this migration persists the
-- catalog's identity tree, closed capability vocabulary, and read-shape support --
-- NOT content acquisition/sync (#729), XCCDF/candidate pipelines (#730), or
-- baseline activation/rollback (#731). Every table here is catalog-authored data
-- (reviewed product code shipped by appliance update, ADR-0022: "Operators cannot
-- upload executable plugins, scripts, or catalog mappings"); there is no runner
-- write grant because no runner mutates this schema yet -- #729/#730 add the
-- content-sync/candidate tables and their own grants when that pipeline lands.
--
-- Identity tree (each level immutable once referenced by a plan -- ADR-0022 "history
-- and referenced rollback candidates cannot be overwritten in place", ADR-0023 plans
-- reference exact catalog/baseline digests):
--   catalog_source_revisions   -- provenance: which reviewed repo revision/appliance
--                                  update shipped this catalog content
--   catalog_products           -- vendor + product key (data-driven, no code/plugin
--                                  identifiers -- ADR-0013)
--   catalog_product_versions   -- one EXACT version per product (no ranges --
--                                  ADR-0022 "no ranges, nearest-version fallback")
--   catalog_content_releases   -- one exact vendor content revision (e.g.
--                                  "v2r3-stig") for a product version + kind
--   catalog_components         -- one executable component under a product version
--                                  (selector + transport), may be nested
--                                  (parent_component_id) for named sub-services
--   catalog_execution_profiles -- the row that actually binds one component to one
--                                  content release: the "STIG and SRG are distinct
--                                  first-class kinds" unit callers query
--   catalog_credential_requirements -- required credential purposes per execution
--                                       profile (closed CredentialPurposes vocabulary)
--   catalog_benchmark_references    -- XCCDF/benchmark identity for STIG execution
--                                       profiles only (SRGs have none -- ADR-0022)
--   catalog_remediation_definitions -- remediation capability flag/definition per
--                                       execution profile (execution itself is #15;
--                                       this table only records whether/how a
--                                       component supports it, per the issue's AC
--                                       "remediation capability are queryable")
--
-- Closed vocabulary enforcement: `kind` (stig|srg), `transport`
-- (vmware|ssh|nsx-api|vcf-api), and `selector_kind`
-- (vcenter|esxi|vm|service|target) are
-- CHECK constraints, the same "closed set enforced at the database boundary"
-- convention TargetKinds/InventoryItemTypes already establish in C#
-- (Waypoint.Core.Sites.TargetKinds, Waypoint.Core.Discovery.InventoryItemTypes) --
-- see CatalogVocabulary.cs for the mirrored C# closed sets and their fail-closed
-- validation (issue #728 AC "unknown capability vocabulary fails closed with
-- actionable validation").
--
-- Historical-retention protection (issue #728 AC "referenced historical revisions
-- cannot be deleted accidentally"): every FK in this tree uses ON DELETE RESTRICT,
-- not CASCADE. A catalog_execution_profiles row (or anything beneath it) cannot be
-- deleted while any ancestor still references it, and once #728's plan/baseline
-- consumers (#729-#731) exist they add their own RESTRICT-only references down to
-- this tree -- deletion of shipped catalog data is not a supported operation at all
-- today (appliance updates are additive), but the constraint shape is in place from
-- day one so a future prune path cannot silently orphan a plan's frozen reference.

-- catalog_source_revisions ------------------------------------------------------------
-- Provenance record: which reviewed repository revision (git SHA of THIS repo, since
-- ADR-0022 "Waypoint will maintain a versioned execution catalog as reviewed product
-- code in this public repository") produced the catalog rows shipped in one appliance
-- update. Every row below carries a source_revision_id so a future diagnostic can
-- answer "which appliance version introduced/last touched this catalog entry."
CREATE TABLE IF NOT EXISTS catalog_source_revisions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    revision_key TEXT NOT NULL UNIQUE,
    description TEXT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- catalog_products ----------------------------------------------------------------
-- Data-driven product identity (ADR-0013: new products/components are data, never a
-- code/plugin identifier). `vendor` and `product_key` are catalog-authored free text,
-- not an enum -- the risk note in issue #728 explicitly warns against hard-coding only
-- today's vSphere/NSX names into the relational shape.
CREATE TABLE IF NOT EXISTS catalog_products (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_revision_id UUID NOT NULL REFERENCES catalog_source_revisions (id) ON DELETE RESTRICT,
    vendor TEXT NOT NULL,
    product_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_products_vendor_key_unique UNIQUE (vendor, product_key)
);

-- catalog_product_versions ---------------------------------------------------------
-- Exactly one row per exact product version (ADR-0022: "one exact product version to
-- one exact immutable profile version. There are no ranges"). `version_key` is the
-- exact identity discovery/Admin-configuration must match byte-for-byte (e.g.
-- "8.0.3", "9.0.0") -- never a family key like sibling "8-0" (docs/compliance-parity.md
-- "A source key marked family ... is never a product version").
CREATE TABLE IF NOT EXISTS catalog_product_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id UUID NOT NULL REFERENCES catalog_products (id) ON DELETE RESTRICT,
    version_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_product_versions_unique UNIQUE (product_id, version_key)
);

CREATE INDEX IF NOT EXISTS idx_catalog_product_versions_product_id ON catalog_product_versions (product_id);

-- catalog_content_releases ---------------------------------------------------------
-- One exact vendor content revision (sibling "v2r3-stig"/"Y26M05-srg" shape) for a
-- given kind. `kind` is the closed stig|srg vocabulary -- STIG and SRG are distinct
-- first-class kinds (issue #728 AC), never inferred from a path or leaf name.
-- `release_key` is the exact immutable content identity (e.g. "v2r3-stig"); multiple
-- product versions may share a release_key only if the catalog author explicitly
-- reuses the same content package, but each execution_profiles row still pins one
-- exact (component, release) pair -- there is no cross-version test equivalence.
CREATE TABLE IF NOT EXISTS catalog_content_releases (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_revision_id UUID NOT NULL REFERENCES catalog_source_revisions (id) ON DELETE RESTRICT,
    kind TEXT NOT NULL,
    release_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_content_releases_kind_check CHECK (kind IN ('stig', 'srg')),
    CONSTRAINT catalog_content_releases_unique UNIQUE (kind, release_key)
);

-- catalog_components ----------------------------------------------------------------
-- One executable component scoped to a product version. `parent_component_id` builds
-- the named-sub-service tree (e.g. VCSA STIG's EAM/Lookup/PostgreSQL/... services
-- under one vSphere product-version node) so repeated leaf names across different
-- parents (issue #728 AC "repeated leaf names") are distinguished by
-- (product_version_id, parent_component_id, component_key), not by name alone.
-- `selector_kind`/`transport` are the closed capability vocabulary
-- (docs/compliance-parity.md table); `selector_name` carries the named-service value
-- when selector_kind = 'service' (e.g. "eam", "lookup", "sddc-manager-nginx") and is
-- NULL for every other selector: the three generic vSphere object-kind selectors
-- (vcenter|esxi|vm) AND the whole-appliance 'target' selector (docs/compliance-parity.md's
-- `ssh / target` rows -- Aria Operations/Automation/Suite Lifecycle, Workspace ONE Access,
-- Photon OS -- where the component IS the appliance and no sub-service name is invented).
CREATE TABLE IF NOT EXISTS catalog_components (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    product_version_id UUID NOT NULL REFERENCES catalog_product_versions (id) ON DELETE RESTRICT,
    parent_component_id UUID NULL REFERENCES catalog_components (id) ON DELETE RESTRICT,
    component_key TEXT NOT NULL,
    display_name TEXT NOT NULL,
    transport TEXT NOT NULL,
    selector_kind TEXT NOT NULL,
    selector_name TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_components_transport_check CHECK (transport IN ('vmware', 'ssh', 'nsx-api', 'vcf-api')),
    CONSTRAINT catalog_components_selector_kind_check CHECK (selector_kind IN ('vcenter', 'esxi', 'vm', 'service', 'target')),
    CONSTRAINT catalog_components_selector_name_check CHECK (
        (selector_kind = 'service' AND selector_name IS NOT NULL) OR
        (selector_kind <> 'service' AND selector_name IS NULL)
    ),
    CONSTRAINT catalog_components_unique UNIQUE (product_version_id, parent_component_id, component_key)
);

CREATE INDEX IF NOT EXISTS idx_catalog_components_product_version_id ON catalog_components (product_version_id);
CREATE INDEX IF NOT EXISTS idx_catalog_components_parent_component_id ON catalog_components (parent_component_id);

-- catalog_report_groups ---------------------------------------------------------------
-- Closed priority/report-group vocabulary (docs/compliance-parity.md "Priority" row):
-- NSX STIG 1, VCSA STIG 2, vCenter STIG 3, ESXi STIG 4, VM STIG 5, every SRG 6. Stored
-- as data (not a C#-only enum) so a future appliance update can add a report group
-- for a new product family without a schema change -- only new ROWS, matching
-- ADR-0013 "new products/components remain data-driven."
CREATE TABLE IF NOT EXISTS catalog_report_groups (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    group_key TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    priority INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_report_groups_priority_check CHECK (priority BETWEEN 1 AND 6)
);

-- catalog_execution_profiles ------------------------------------------------------
-- The row callers actually query: one component bound to one exact content release,
-- i.e. "STIG and SRG content are distinct first-class kinds" made concrete (issue
-- #728 AC). This is catalog identity/capability data ONLY -- it is deliberately NOT
-- an activated baseline (#731's `baselines` table, sketch in docs/api-contract.md,
-- binds product_version+profile_version+xccdf_version with an activation
-- status/audit trail on top of this row). Distinguishing vendor-derived vs.
-- operator-override vs. activation-state (issue #728 AC) means: every row in this
-- migration is vendor-derived/catalog-authored (is_operator_override is always false
-- today -- ADR-0022 forbids operator catalog mappings entirely), and activation state
-- lives one layer up in #731, never here.
CREATE TABLE IF NOT EXISTS catalog_execution_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    component_id UUID NOT NULL REFERENCES catalog_components (id) ON DELETE RESTRICT,
    content_release_id UUID NOT NULL REFERENCES catalog_content_releases (id) ON DELETE RESTRICT,
    report_group_id UUID NOT NULL REFERENCES catalog_report_groups (id) ON DELETE RESTRICT,
    profile_version TEXT NOT NULL,
    is_operator_override BOOLEAN NOT NULL DEFAULT false,
    output_kind TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_execution_profiles_output_kind_check CHECK (output_kind IN ('hdf', 'hdf_ckl')),
    CONSTRAINT catalog_execution_profiles_operator_override_check CHECK (is_operator_override = false),
    CONSTRAINT catalog_execution_profiles_unique UNIQUE (component_id, content_release_id)
);

CREATE INDEX IF NOT EXISTS idx_catalog_execution_profiles_component_id ON catalog_execution_profiles (component_id);
CREATE INDEX IF NOT EXISTS idx_catalog_execution_profiles_content_release_id ON catalog_execution_profiles (content_release_id);

-- catalog_credential_requirements ------------------------------------------------
-- Required credential purposes per execution profile (issue #728 AC "credential
-- requirements ... queryable"). `purpose` reuses the closed
-- Waypoint.Core.Secrets.CredentialPurposes vocabulary (vsphere-api, vcsa-ssh,
-- nsx-api, srg-ssh) already enforced elsewhere (migration 0043's
-- target_credential_bindings) rather than inventing a parallel vocabulary --
-- ADR-0022 "vcf-api authentication is a catalog requirement whose final purpose is
-- planned under #807" is why 'vcf-api' is deliberately NOT yet in this CHECK: no
-- execution profile in this migration's fixtures declares it, and adding an
-- unresolved purpose now would let an unfinished credential contract leak into a
-- closed vocabulary this migration is supposed to fail closed on.
CREATE TABLE IF NOT EXISTS catalog_credential_requirements (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_profile_id UUID NOT NULL REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    purpose TEXT NOT NULL,
    is_required BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_credential_requirements_purpose_check CHECK (purpose IN ('vsphere-api', 'vcsa-ssh', 'nsx-api', 'srg-ssh')),
    CONSTRAINT catalog_credential_requirements_unique UNIQUE (execution_profile_id, purpose)
);

CREATE INDEX IF NOT EXISTS idx_catalog_credential_requirements_execution_profile_id ON catalog_credential_requirements (execution_profile_id);

-- catalog_benchmark_references ------------------------------------------------------
-- XCCDF/benchmark identity for STIG execution profiles only (ADR-0022: "STIG
-- activation requires one complete compatible vendor profile + DISA XCCDF + exact
-- mapping. SRG activation ... has no XCCDF/CKL"). This table records the catalog's
-- declared exact benchmark identity a STIG execution profile expects to map against;
-- the actual staged XCCDF artifact/mapping-set lifecycle is #730's concern. Enforced
-- 1:1 with a STIG execution profile via the unique FK; an SRG execution profile has
-- no row here at all (queried by absence, not a nullable "not applicable" flag).
CREATE TABLE IF NOT EXISTS catalog_benchmark_references (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_profile_id UUID NOT NULL UNIQUE REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    benchmark_key TEXT NOT NULL,
    benchmark_version TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- catalog_remediation_definitions ---------------------------------------------------
-- Records whether/how a component supports remediation (issue #728 AC "remediation
-- ... capability are queryable"). Remediation EXECUTION is explicitly out of #726's
-- scope (separate epic #15) -- this table is catalog metadata only: a declared
-- capability flag plus a free-text mechanism note, never an executable
-- script/plugin reference (ADR-0013). At most one row per execution profile.
CREATE TABLE IF NOT EXISTS catalog_remediation_definitions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_profile_id UUID NOT NULL UNIQUE REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    is_supported BOOLEAN NOT NULL DEFAULT false,
    mechanism_note TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
