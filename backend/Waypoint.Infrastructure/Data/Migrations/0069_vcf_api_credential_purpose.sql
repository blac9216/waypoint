-- Issue #977 (epic #726): widens migration 0050's
-- catalog_credential_requirements_purpose_check CHECK constraint to admit the
-- 'vcf-api' credential purpose, and seeds the 13th (final) docs/compliance-parity.md
-- provenance-matrix row -- VCF `9-x` SRG's `vcf-api` named-service row (SDDC Manager
-- application, Automation application) -- which PR #973 (migration 0067) deliberately
-- left unseeded because the CHECK constraint excluded 'vcf-api' pending issue #807.
--
-- #807 is now CLOSED. Its deliverable, ADR-0024, resolves the vcf-api credential
-- purpose: "The closed catalog declares the purpose(s) required by each planned
-- component, including a distinct compatible purpose for catalog-declared `vcf-api`
-- work before that work can execute." The exact literal token, 'vcf-api', is chosen to
-- stay single-sourced with Waypoint.Core.Secrets.CredentialPurposes.VcfApi (added by
-- this same PR) and matches the existing naming convention every other purpose in this
-- CHECK already follows (vsphere-api/nsx-api mirror their own transport name exactly;
-- vcf-api does the same, matching catalog_components_transport_check's 'vcf-api'
-- transport literal it has always admitted). No schema change beyond this one CHECK
-- widening is required; migration 0069 slot verified free against both the migrations
-- directory and open PRs at commit time (0068 claimed by open PR #978, not yet merged;
-- this migration assumes it lands first and claims the next free slot).
--
-- Constraint-alteration idiom: Postgres cannot ALTER a CHECK constraint in place, so
-- this migration DROPs and re-CREATEs it with the widened vocabulary -- the exact
-- DROP CONSTRAINT IF EXISTS / ADD CONSTRAINT idiom migration 0022
-- (credentials_credential_type_check) already established for widening a
-- closed-vocabulary CHECK (existing rows are unaffected; DROP/CREATE CONSTRAINT never
-- touches data, only the constraint definition). IF EXISTS also makes this statement
-- itself idempotent under this test suite's raw-SQL re-apply idiom.
ALTER TABLE catalog_credential_requirements
    DROP CONSTRAINT IF EXISTS catalog_credential_requirements_purpose_check;

ALTER TABLE catalog_credential_requirements
    ADD CONSTRAINT catalog_credential_requirements_purpose_check
        CHECK (purpose IN ('vsphere-api', 'vcsa-ssh', 'nsx-api', 'srg-ssh', 'vcf-api'));

-- Seed the 13th provenance-matrix row: VCF `9-x` SRG `vcf-api` named-service
-- components (SDDC Manager application, Automation application). Follows PR
-- #966/#973's invented-from-documentation / idempotent-ON-CONFLICT / no-new-grants
-- pattern exactly -- INVENTED-FROM-DOCUMENTATION data authored directly from the
-- parity doc's provenance matrix row, never exported vendor content, never a byte
-- copied from any sibling repository or lab observation. Reuses the 'vcf' product,
-- '9.0.0' product version, and 'Y26M05-srg' content release migration 0067 already
-- seeded (this row is the OTHER transport split of the same VCF `9-x` family node --
-- 0067 seeded the `ssh` named-service row; this migration seeds the `vcf-api`
-- named-service row).
INSERT INTO catalog_source_revisions (revision_key, description)
VALUES ('issue-977-seed', 'Hand-curated execution-catalog seed for the VCF 9-x vcf-api named-service row (issue #977)')
ON CONFLICT (revision_key) DO NOTHING;

-- catalog_components: VCF 9-x vcf-api named-service split (vcf-api transport,
-- selector_name required) -- "SDDC Manager application; Automation application" |
-- `vcf-api` / named service" row. Component keys are catalog-authored to disambiguate
-- from 0067's ssh-transport component keys for the same appliances (e.g.
-- 'sddc-manager-nginx' vs. this row's 'sddc-manager-api').
INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
SELECT pv.id, NULL, s.service_key, s.display_name, 'vcf-api', 'service', s.service_key
FROM catalog_product_versions pv
JOIN catalog_products p ON p.id = pv.product_id
CROSS JOIN (VALUES
    ('sddc-manager-api', 'SDDC Manager application'),
    ('automation-api', 'Automation application')
) AS s(service_key, display_name)
WHERE p.product_key = 'vcf' AND pv.version_key = '9.0.0'
ON CONFLICT (product_version_id, component_key) WHERE parent_component_id IS NULL DO NOTHING;

-- catalog_execution_profiles: binds each component above to the shared Y26M05-srg
-- content release + report group, matching migration 0067's exact pattern (SRG-only,
-- output_kind 'hdf', report_group_key 'srg').
INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
SELECT cc.id, cr.id, rg.id, 'Y26M05', 'hdf'
FROM catalog_components cc
JOIN catalog_product_versions pv ON pv.id = cc.product_version_id
JOIN catalog_products p ON p.id = pv.product_id
JOIN (VALUES
    ('vcf', '9.0.0', 'sddc-manager-api', 'Y26M05-srg'),
    ('vcf', '9.0.0', 'automation-api', 'Y26M05-srg')
) AS ep(product_key, version_key, component_key, release_key)
    ON ep.product_key = p.product_key AND ep.version_key = pv.version_key AND ep.component_key = cc.component_key
JOIN catalog_content_releases cr ON cr.release_key = ep.release_key
JOIN catalog_report_groups rg ON rg.group_key = 'srg'
ON CONFLICT (component_id, content_release_id) DO NOTHING;

-- catalog_credential_requirements: docs/compliance-parity.md "Purpose" column for this
-- row reads "catalog-declared API purpose (#807)" -- ADR-0024 resolves that to the
-- 'vcf-api' purpose this migration's CHECK widening just admitted. Every vcf-api
-- transport component requires it, and only it (unlike the vSphere VCSA ssh row, VCF's
-- vcf-api row has no second, vsphere-api-style purpose -- these are not vCenter-
-- managed services).
INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required)
SELECT ep.id, 'vcf-api', true
FROM catalog_execution_profiles ep
JOIN catalog_components cc ON cc.id = ep.component_id
WHERE cc.transport = 'vcf-api'
ON CONFLICT (execution_profile_id, purpose) DO NOTHING;

-- No catalog_benchmark_references rows: this row is SRG (ADR-0022 -- SRGs have no
-- XCCDF/CKL), same as every row migration 0067 seeded.
