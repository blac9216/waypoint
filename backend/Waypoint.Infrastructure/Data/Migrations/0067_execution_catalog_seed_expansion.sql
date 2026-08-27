-- Issue #967 (epic #726): expands migration 0064's execution-catalog seed to the
-- remaining 9 of 13 docs/compliance-parity.md "Sibling source-capability provenance
-- matrix" rows. Migration 0064 seeded only a representative slice (vSphere 8.0.3 STIG
-- vmware + VCSA-service rows, NSX 4.1.2 STIG named-function row, Photon OS 5.0 SRG
-- row) -- enough to prove every documented catalog SHAPE, per its own header comment.
-- This migration adds the rest as additive catalog-authored data, per 0064's own
-- deferral note. No schema change; slot 0067 verified free (0064/0066 shipped,
-- 0065/0067 both open, this PR claims 0067) against both the migrations directory and
-- open PRs at commit time.
--
-- INVENTED-FROM-DOCUMENTATION data authored directly from the parity doc's provenance
-- matrix rows -- never exported vendor content, never a byte copied from any sibling
-- repository or any lab observation. The doc's own provenance-matrix preamble is
-- explicit: "Source families cannot be copied into executable catalog entries,
-- expanded by inference, or used for range/nearest-version matching" -- a `family`
-- key form documents only what the sibling claims, never an executable catalog exact
-- version. Every exact `version_key` inserted below (including for rows the doc marks
-- `family`) is therefore a catalog-authored INVENTED exact version, chosen to be
-- plausible for the documented family and consistent with PR #966's own precedent
-- (which invented `4.1.2` as the exact catalog version for the NSX `4-x` `family` row
-- and `8.0.3` for the vSphere `8-0` `exact` row) -- never copied from any real
-- deployment, changelog, or the lab.
--
-- Rows covered (docs/compliance-parity.md's remaining 9):
--   - vSphere `9-0` SRG: vmware object-kind row (vCenter, ESXi, VM) + VCSA
--     named-service row (Envoy, PostgreSQL, VAMI, Photon)
--   - NSX `9-x` SRG: named-function row (Manager, Routing)
--   - Aria Operations `8-x` SRG: whole-appliance row
--   - Aria Automation `8-x` SRG: whole-appliance row
--   - Aria Suite Lifecycle `8-x` SRG: whole-appliance row
--   - Workspace ONE Access `3-3-x` SRG: whole-appliance row
--   - VCF `9-x` SRG: `ssh` named-service row (SDDC Manager nginx/PostgreSQL/Photon,
--     Operations httpd/PostgreSQL/Photon, Operations HCX httpd/Photon, Operations
--     Networks nginx-platform/Ubuntu)
--
-- Row deliberately NOT covered (see ExecutionCatalogSeedDriftGuardTests, which names
-- it explicitly rather than silently passing):
--   - VCF `9-x` SRG `vcf-api` named-service row (SDDC Manager application, Automation
--     application). Migration 0050's catalog_credential_requirements_purpose_check
--     CHECK constraint deliberately excludes 'vcf-api' pending issue #807's not-yet-
--     final vcf-api credential purpose (0050's own header: "adding an unresolved
--     purpose now would let an unfinished credential contract leak into a closed
--     vocabulary this migration is supposed to fail closed on"). Seeding this row's
--     components/execution profiles without a credential requirement would produce an
--     incomplete, non-functional catalog entry that looks runnable but can never
--     resolve a credential; issue #807 must land first. No schema change is in scope
--     for this migration, so this row stays unseeded until #807 resolves.
--
-- Every INSERT is idempotent (ON CONFLICT DO NOTHING against the natural-key unique
-- constraints migration 0050/0051 already define), matching migration 0064's pattern
-- exactly. No new tables (SchemaMigrationTests.ExpectedTables unchanged) and no new
-- runner grants (catalog-authored, appliance-shipped data only).

-- One provenance record for every row this migration inserts.
INSERT INTO catalog_source_revisions (revision_key, description)
VALUES ('issue-967-seed', 'Hand-curated execution-catalog seed expansion authored from docs/compliance-parity.md (issue #967)')
ON CONFLICT (revision_key) DO NOTHING;

-- catalog_products: Aria Operations/Automation/Suite Lifecycle and Workspace ONE
-- Access are new product keys; vsphere/nsx/vcf already exist from migration 0064
-- (INSERT ... ON CONFLICT DO NOTHING handles the vsphere/nsx re-declaration cleanly;
-- vcf is new here).
INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name)
SELECT sr.id, v.vendor, v.product_key, v.display_name
FROM catalog_source_revisions sr
CROSS JOIN (VALUES
    ('vmware', 'vsphere', 'VMware vSphere'),
    ('vmware', 'nsx', 'VMware NSX'),
    ('vmware', 'vcf', 'VMware Cloud Foundation'),
    ('vmware', 'aria-operations', 'VMware Aria Operations'),
    ('vmware', 'aria-automation', 'VMware Aria Automation'),
    ('vmware', 'aria-suite-lifecycle', 'VMware Aria Suite Lifecycle'),
    ('vmware', 'vidm', 'VMware Workspace ONE Access')
) AS v(vendor, product_key, display_name)
WHERE sr.revision_key = 'issue-967-seed'
ON CONFLICT (vendor, product_key) DO NOTHING;

-- catalog_product_versions: exact catalog versions only (see header note on invented
-- exact versions for `family`-marked doc rows).
INSERT INTO catalog_product_versions (product_id, version_key, display_name)
SELECT p.id, pv.version_key, pv.display_name
FROM catalog_products p
CROSS JOIN (VALUES
    ('vsphere', '9.0.0', 'vSphere 9.0.0'),
    ('nsx', '9.0.0', 'NSX 9.0.0'),
    ('vcf', '9.0.0', 'VMware Cloud Foundation 9.0.0'),
    ('aria-operations', '8.0.0', 'Aria Operations 8.0.0'),
    ('aria-automation', '8.0.0', 'Aria Automation 8.0.0'),
    ('aria-suite-lifecycle', '8.0.0', 'Aria Suite Lifecycle 8.0.0'),
    ('vidm', '3.3.0', 'Workspace ONE Access 3.3.0')
) AS pv(product_key, version_key, display_name)
WHERE p.product_key = pv.product_key
ON CONFLICT (product_id, version_key) DO NOTHING;

-- catalog_content_releases: exact vendor content revisions from the provenance
-- matrix's "Kind / source profile revision" column. All 9 remaining rows share the
-- single documented `Y26M05-srg` release key.
INSERT INTO catalog_content_releases (source_revision_id, kind, release_key, display_name)
SELECT sr.id, r.kind, r.release_key, r.display_name
FROM catalog_source_revisions sr
CROSS JOIN (VALUES
    ('srg', 'Y26M05-srg', 'Y26M05 SRG release')
) AS r(kind, release_key, display_name)
WHERE sr.revision_key = 'issue-967-seed'
ON CONFLICT (kind, release_key) DO NOTHING;

-- catalog_components: vSphere 9-0 object-kind split (vmware transport, no
-- selector_name) -- "vSphere `9-0` | exact | SRG ... | vCenter; ESXi; VM | `vmware` /
-- object kind" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, c.component_key, c.display_name, 'vmware', c.selector_kind, NULL
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('vcenter', 'vCenter', 'vcenter'),
    ('esxi', 'ESXi', 'esxi'),
    ('vm', 'VM', 'vm')
) AS c(component_key, display_name, selector_kind)
WHERE p.product_key = 'vsphere' AND pv.version_key = '9.0.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: vSphere 9-0 VCSA named-service split (ssh transport,
-- selector_name required) -- "VCSA Envoy, PostgreSQL, VAMI, Photon | `ssh` / named
-- VCSA service" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, s.service_key, s.display_name, 'ssh', 'service', s.service_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('envoy', 'VCSA Envoy'),
    ('postgresql', 'VCSA PostgreSQL'),
    ('vami', 'VCSA VAMI'),
    ('photon', 'VCSA Photon')
) AS s(service_key, display_name)
WHERE p.product_key = 'vsphere' AND pv.version_key = '9.0.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: NSX 9-x named-function split (nsx-api transport) -- "NSX `9-x`
-- | family | SRG ... | Manager, routing | `nsx-api` / named function" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, f.function_key, f.display_name, 'nsx-api', 'service', f.function_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('manager', 'NSX Manager'),
    ('routing', 'NSX Routing')
) AS f(function_key, display_name)
WHERE p.product_key = 'nsx' AND pv.version_key = '9.0.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: Aria Operations / Aria Automation / Aria Suite Lifecycle /
-- Workspace ONE Access whole-appliance rows (ssh/target, no selector_name).
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, w.component_key, w.display_name, 'ssh', 'target', NULL
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('aria-operations', '8.0.0', 'aria-operations', 'Aria Operations'),
    ('aria-automation', '8.0.0', 'aria-automation', 'Aria Automation'),
    ('aria-suite-lifecycle', '8.0.0', 'aria-suite-lifecycle', 'Aria Suite Lifecycle'),
    ('vidm', '3.3.0', 'vidm', 'Workspace ONE Access')
) AS w(product_key, version_key, component_key, display_name)
WHERE p.product_key = w.product_key AND pv.version_key = w.version_key
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: VCF 9-x ssh named-service row -- "SDDC Manager nginx,
-- PostgreSQL, Photon; Operations httpd, PostgreSQL, Photon; Operations HCX httpd,
-- Photon; Operations Networks nginx platform, Ubuntu | `ssh` / named service" row.
-- Component keys are catalog-authored to disambiguate the doc's per-appliance service
-- name reuse (e.g. "PostgreSQL" appears under both SDDC Manager and Operations).
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, s.service_key, s.display_name, 'ssh', 'service', s.service_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('sddc-manager-nginx', 'SDDC Manager nginx'),
    ('sddc-manager-postgresql', 'SDDC Manager PostgreSQL'),
    ('sddc-manager-photon', 'SDDC Manager Photon'),
    ('operations-httpd', 'Operations httpd'),
    ('operations-postgresql', 'Operations PostgreSQL'),
    ('operations-photon', 'Operations Photon'),
    ('operations-hcx-httpd', 'Operations HCX httpd'),
    ('operations-hcx-photon', 'Operations HCX Photon'),
    ('operations-networks-nginx-platform', 'Operations Networks nginx platform'),
    ('operations-networks-ubuntu', 'Operations Networks Ubuntu')
) AS s(service_key, display_name)
WHERE p.product_key = 'vcf' AND pv.version_key = '9.0.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_execution_profiles: binds each component above to the shared Y26M05-srg
-- content release + report group. All rows here are SRG (docs/compliance-parity.md
-- "Output" column: "HDF only, never CKL/upload"), so output_kind is 'hdf' throughout
-- and report_group_key is always the catch-all 'srg' group (priority 6, "every SRG 6"
-- per the doc's Priority row and migration 0064's already-seeded catalog_report_groups
-- row).
INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
SELECT cc.id, cr.id, rg.id, 'Y26M05', 'hdf'
FROM catalog_components cc
JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
JOIN catalog_products p ON p.id = pv.product_id
JOIN (VALUES
    -- vSphere 9-0 SRG: vmware object-kind row.
    ('vsphere', '9.0.0', 'vcenter', 'Y26M05-srg'),
    ('vsphere', '9.0.0', 'esxi', 'Y26M05-srg'),
    ('vsphere', '9.0.0', 'vm', 'Y26M05-srg'),
    -- vSphere 9-0 SRG: VCSA named-service row.
    ('vsphere', '9.0.0', 'envoy', 'Y26M05-srg'),
    ('vsphere', '9.0.0', 'postgresql', 'Y26M05-srg'),
    ('vsphere', '9.0.0', 'vami', 'Y26M05-srg'),
    ('vsphere', '9.0.0', 'photon', 'Y26M05-srg'),
    -- NSX 9-x SRG: named-function row.
    ('nsx', '9.0.0', 'manager', 'Y26M05-srg'),
    ('nsx', '9.0.0', 'routing', 'Y26M05-srg'),
    -- Aria Operations / Aria Automation / Aria Suite Lifecycle / Workspace ONE Access
    -- SRG: whole-appliance rows.
    ('aria-operations', '8.0.0', 'aria-operations', 'Y26M05-srg'),
    ('aria-automation', '8.0.0', 'aria-automation', 'Y26M05-srg'),
    ('aria-suite-lifecycle', '8.0.0', 'aria-suite-lifecycle', 'Y26M05-srg'),
    ('vidm', '3.3.0', 'vidm', 'Y26M05-srg'),
    -- VCF 9-x SRG: ssh named-service row.
    ('vcf', '9.0.0', 'sddc-manager-nginx', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'sddc-manager-postgresql', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'sddc-manager-photon', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-httpd', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-postgresql', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-photon', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-hcx-httpd', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-hcx-photon', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-networks-nginx-platform', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'operations-networks-ubuntu', 'Y26M05-srg')
) AS ep(product_key, version_key, component_key, release_key)
    ON ep.product_key = p.product_key AND ep.version_key = pv.version_key AND ep.component_key = cc.component_key
JOIN catalog_content_releases cr ON cr.release_key = ep.release_key
JOIN catalog_report_groups rg ON rg.group_key = 'srg'
ON CONFLICT (component_id, content_release_id) DO NOTHING;

-- catalog_credential_requirements: docs/compliance-parity.md "Purpose" column --
-- vmware-transport components require vsphere-api; VCSA named services require BOTH
-- vsphere-api and vcsa-ssh; NSX named functions require nsx-api; every ssh/target
-- (whole-appliance) component requires srg-ssh; the VCF ssh named-service row also
-- requires srg-ssh (doc: "`ssh` / named service | `srg-ssh`" -- unlike the vSphere
-- VCSA named-service row, VCF's ssh row does NOT also require vsphere-api, since VCF
-- appliances are not vCenter-managed the way VCSA services are).
INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required)
SELECT ep.id, req.purpose, true
FROM catalog_execution_profiles ep
JOIN catalog_components cc ON cc.id = ep.component_id
JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN LATERAL (
    SELECT 'vsphere-api' AS purpose WHERE cc.transport = 'vmware'
    UNION ALL
    SELECT 'vsphere-api' WHERE p.product_key = 'vsphere' AND cc.transport = 'ssh' AND cc.selector_kind = 'service'
    UNION ALL
    SELECT 'vcsa-ssh' WHERE p.product_key = 'vsphere' AND cc.transport = 'ssh' AND cc.selector_kind = 'service'
    UNION ALL
    SELECT 'nsx-api' WHERE cc.transport = 'nsx-api'
    UNION ALL
    SELECT 'srg-ssh' WHERE cc.transport = 'ssh' AND cc.selector_kind = 'target'
    UNION ALL
    SELECT 'srg-ssh' WHERE p.product_key = 'vcf' AND cc.transport = 'ssh' AND cc.selector_kind = 'service'
) AS req
ON CONFLICT (execution_profile_id, purpose) DO NOTHING;

-- No catalog_benchmark_references rows: every row this migration seeds is SRG
-- (ADR-0022 -- SRGs have no XCCDF/CKL, so benchmark_key/version is a STIG-only
-- concept). Migration 0064's benchmark_references INSERT already filters
-- `WHERE cr.kind = 'stig'`, which naturally excludes every profile this migration
-- creates (all bound to the 'srg'-kind Y26M05-srg content release) -- no additional
-- statement is needed here.
