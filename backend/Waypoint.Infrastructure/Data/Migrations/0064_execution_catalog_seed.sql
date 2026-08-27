-- Issue #959 (epic #726), Option C: ships the hand-curated execution-catalog seed
-- docs/compliance-parity.md's "Sibling source-capability provenance matrix" describes
-- but no prior migration ever populated. Before this migration, catalog_products/
-- catalog_components/catalog_execution_profiles are empty on a fresh stack, so a
-- discovered component can never link to a catalog component and every component
-- reports is_compatible=false ("not linked to a known catalog component") even after
-- content imports cleanly (issue #959's part 1, PR #962).
--
-- This is INVENTED-FROM-DOCUMENTATION data authored directly from the parity doc's
-- provenance matrix rows -- never exported vendor content, never a byte copied from
-- any sibling repository. Every literal below (version keys, release keys, component
-- names) mirrors ONLY what docs/compliance-parity.md already documents in this
-- repository.
--
-- Scope: this migration seeds a representative slice of the provenance matrix's
-- reviewed rows -- enough to prove every documented SHAPE (vSphere object-kind split,
-- VCSA named-service split, NSX named-function split, and whole-appliance) end to end
-- -- not a byte-for-byte transcription of all 44 sibling scan components. Expanding
-- the remaining rows (NSX 9-x, the other Aria/vIDM/Photon/VCF rows) is additive
-- catalog-authored data and does not require a schema change; it can land in a
-- follow-up seed migration without touching this one.
--
-- No new tables (SchemaMigrationTests.ExpectedTables is unchanged) and no new runner
-- grants (every row here is catalog-authored, appliance-shipped data written by this
-- migration only, matching migration 0050's "no runner mutates this schema" note).
-- Every INSERT is idempotent (ON CONFLICT DO NOTHING against the natural-key unique
-- constraints migration 0050 already defines), so re-running this migration (or a
-- future appliance update that re-applies it) never duplicates a row.

-- One provenance record for every row this migration inserts.
INSERT INTO catalog_source_revisions (revision_key, description)
VALUES ('issue-959-seed', 'Hand-curated execution-catalog seed authored from docs/compliance-parity.md (issue #959)')
ON CONFLICT (revision_key) DO NOTHING;

-- catalog_report_groups: the closed priority vocabulary docs/compliance-parity.md's
-- "Priority" row defines verbatim (NSX STIG 1; VCSA STIG 2; vCenter STIG 3; ESXi STIG
-- 4; VM STIG 5; every SRG 6).
INSERT INTO catalog_report_groups (group_key, display_name, priority) VALUES
    ('nsx-stig', 'NSX STIG', 1),
    ('vcsa-stig', 'VCSA STIG', 2),
    ('vcenter-stig', 'vCenter STIG', 3),
    ('esxi-stig', 'ESXi STIG', 4),
    ('vm-stig', 'VM STIG', 5),
    ('srg', 'SRG', 6)
ON CONFLICT (group_key) DO NOTHING;

-- catalog_products / catalog_product_versions: exact product versions only (never a
-- family key) -- docs/compliance-parity.md "A source key marked family ... is never a
-- product version"; vSphere 8-0 and NSX 4-x are both marked `exact`/`family` in the
-- matrix respectively, but the CATALOG version key here is the exact identity
-- discovery must match byte-for-byte, distinct from the sibling's own family/exact
-- marker column.
INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name)
SELECT sr.id, v.vendor, v.product_key, v.display_name
FROM catalog_source_revisions sr
CROSS JOIN (VALUES
    ('vmware', 'vsphere', 'VMware vSphere'),
    ('vmware', 'nsx', 'VMware NSX'),
    ('vmware', 'photon', 'VMware Photon OS')
) AS v(vendor, product_key, display_name)
WHERE sr.revision_key = 'issue-959-seed'
ON CONFLICT (vendor, product_key) DO NOTHING;

INSERT INTO catalog_product_versions (product_id, version_key, display_name)
SELECT p.id, pv.version_key, pv.display_name
FROM catalog_products p
CROSS JOIN (VALUES
    ('vsphere', '8.0.3', 'vSphere 8.0.3'),
    ('nsx', '4.1.2', 'NSX 4.1.2'),
    ('photon', '5.0', 'Photon OS 5.0')
) AS pv(product_key, version_key, display_name)
WHERE p.product_key = pv.product_key
ON CONFLICT (product_id, version_key) DO NOTHING;

-- catalog_content_releases: exact vendor content revisions (docs/compliance-parity.md
-- provenance matrix's "Kind / source profile revision" column).
INSERT INTO catalog_content_releases (source_revision_id, kind, release_key, display_name)
SELECT sr.id, r.kind, r.release_key, r.display_name
FROM catalog_source_revisions sr
CROSS JOIN (VALUES
    ('stig', 'v2r3-stig', 'vSphere 8.0 STIG v2r3'),
    ('stig', 'v1r2-stig', 'NSX 4.x STIG v1r2'),
    ('srg', 'v3r3-srg', 'Photon OS 5.0 SRG v3r3')
) AS r(kind, release_key, display_name)
WHERE sr.revision_key = 'issue-959-seed'
ON CONFLICT (kind, release_key) DO NOTHING;

-- catalog_components: vSphere object-kind split (vmware transport, no selector_name)
-- -- docs/compliance-parity.md's "vSphere `8-0` | exact | STIG ... | vCenter; ESXi;
-- VM | `vmware` / object kind" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, c.component_key, c.display_name, 'vmware', c.selector_kind, NULL
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('vcenter', 'vCenter', 'vcenter'),
    ('esxi', 'ESXi', 'esxi'),
    ('vm', 'VM', 'vm')
) AS c(component_key, display_name, selector_kind)
WHERE p.product_key = 'vsphere' AND pv.version_key = '8.0.3'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: VCSA named-service split (ssh transport, selector_name required)
-- -- docs/compliance-parity.md's "VCSA EAM, Lookup, PerfCharts, Photon, PostgreSQL,
-- STS, UI, VAMI, Envoy | `ssh` / named VCSA service" row. Nested under the same vSphere
-- 8.0.3 product version (parent_component_id NULL -- these are top-level named
-- services, not children of the vcenter object-kind component).
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, s.service_key, s.display_name, 'ssh', 'service', s.service_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('eam', 'VCSA EAM'),
    ('lookup', 'VCSA Lookup'),
    ('postgresql', 'VCSA PostgreSQL'),
    ('vami', 'VCSA VAMI')
) AS s(service_key, display_name)
WHERE p.product_key = 'vsphere' AND pv.version_key = '8.0.3'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: NSX named-function split (nsx-api transport) -- "NSX `4-x` |
-- family | STIG ... | Manager, distributed firewall, tier-0 firewall, tier-0 router,
-- tier-1 firewall, tier-1 router | `nsx-api` / named function" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, f.function_key, f.display_name, 'nsx-api', 'service', f.function_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('manager', 'NSX Manager'),
    ('distributed-firewall', 'NSX Distributed Firewall')
) AS f(function_key, display_name)
WHERE p.product_key = 'nsx' AND pv.version_key = '4.1.2'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_components: Photon OS whole-appliance (ssh/target, no selector_name) --
-- "Photon OS `5-0` | exact | SRG ... | Photon OS | `ssh` / target" row.
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, 'photon', 'Photon OS', 'ssh', 'target', NULL
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
WHERE p.product_key = 'photon' AND pv.version_key = '5.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_execution_profiles: binds each component above to its content release +
-- report group. output_kind is 'hdf_ckl' for STIG (complete exact-baseline HDF and
-- CKL, docs/compliance-parity.md "Output" row) and 'hdf' for SRG (HDF only, never
-- CKL/upload).
INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
SELECT cc.id, cr.id, rg.id, ep.profile_version, ep.output_kind
FROM catalog_components cc
JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
JOIN catalog_products p ON p.id = pv.product_id
JOIN (VALUES
    ('vsphere', '8.0.3', 'vcenter', 'v2r3-stig', 'vcenter-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'esxi', 'v2r3-stig', 'esxi-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'vm', 'v2r3-stig', 'vm-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'eam', 'v2r3-stig', 'vcsa-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'lookup', 'v2r3-stig', 'vcsa-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'postgresql', 'v2r3-stig', 'vcsa-stig', 'v2r3', 'hdf_ckl'),
    ('vsphere', '8.0.3', 'vami', 'v2r3-stig', 'vcsa-stig', 'v2r3', 'hdf_ckl'),
    ('nsx', '4.1.2', 'manager', 'v1r2-stig', 'nsx-stig', 'v1r2', 'hdf_ckl'),
    ('nsx', '4.1.2', 'distributed-firewall', 'v1r2-stig', 'nsx-stig', 'v1r2', 'hdf_ckl'),
    ('photon', '5.0', 'photon', 'v3r3-srg', 'srg', 'v3r3', 'hdf')
) AS ep(product_key, version_key, component_key, release_key, report_group_key, profile_version, output_kind)
    ON ep.product_key = p.product_key AND ep.version_key = pv.version_key AND ep.component_key = cc.component_key
JOIN catalog_content_releases cr ON cr.release_key = ep.release_key
JOIN catalog_report_groups rg ON rg.group_key = ep.report_group_key
ON CONFLICT (component_id, content_release_id) DO NOTHING;

-- catalog_credential_requirements: docs/compliance-parity.md "Purpose" column --
-- vmware-transport components require vsphere-api; VCSA named services require BOTH
-- vsphere-api (the enrollment/session context) and vcsa-ssh (the named-service
-- transport itself); NSX named functions require nsx-api; Photon (ssh/target,
-- non-vSphere-managed) requires srg-ssh.
INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required)
SELECT ep.id, req.purpose, true
FROM catalog_execution_profiles ep
JOIN catalog_components cc ON cc.id = ep.component_id
CROSS JOIN LATERAL (
    SELECT 'vsphere-api' AS purpose WHERE cc.transport = 'vmware' OR cc.selector_kind = 'service' AND cc.transport = 'ssh'
    UNION ALL
    SELECT 'vcsa-ssh' WHERE cc.transport = 'ssh' AND cc.selector_kind = 'service' AND cc.component_key IN ('eam', 'lookup', 'postgresql', 'vami')
    UNION ALL
    SELECT 'nsx-api' WHERE cc.transport = 'nsx-api'
    UNION ALL
    SELECT 'srg-ssh' WHERE cc.transport = 'ssh' AND cc.selector_kind = 'target'
) AS req
ON CONFLICT (execution_profile_id, purpose) DO NOTHING;

-- catalog_benchmark_references: STIG execution profiles only (ADR-0022 -- SRGs have no
-- XCCDF/CKL). benchmark_key/version here are catalog-declared identity placeholders
-- for this seed's invented STIG rows, not a real DISA benchmark identifier -- issue
-- #730 owns actually staging XCCDF content against these references.
INSERT INTO catalog_benchmark_references (execution_profile_id, benchmark_key, benchmark_version)
SELECT ep.id, 'seed-' || cr.release_key, ep.profile_version
FROM catalog_execution_profiles ep
JOIN catalog_content_releases cr ON cr.id = ep.content_release_id
WHERE cr.kind = 'stig'
ON CONFLICT (execution_profile_id) DO NOTHING;
