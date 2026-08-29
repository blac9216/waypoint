-- Issue #1080 (epic #726), live validation round 11 finding: migration 0070 reconciled
-- the vSphere 9.x product-version seed to key '9.0' with key form 'exact' -- but that
-- was itself wrong. docs/compliance-parity.md's provenance matrix said "vSphere 9.0 /
-- exact" because it modeled a top-level `vsphere/9.0/` vendor directory that does not
-- exist: issue #1079 (this same round) proved upstream `master` has NO top-level
-- `vsphere/9.0` tree at all -- the 9.x vSphere/vCenter/ESXi/VM/VCSA content lives under
-- `vcf/9.x`, whose vendor-declared scope is the major-line-scoped `9.x` (key form
-- 'family'), exactly like the `nsx 9.x` and `vcf 9.x` rows already in the same seed.
--
-- Concretely: a real VCF 9.1 ESXi host discovers its observed version as `9.1.0`.
-- Waypoint.Core.Components.VersionScopeMatcher's closed two-form test (unchanged by
-- this migration -- context only) matches an `N.M` key ('9.0') only when the observed
-- version starts with exactly that major.minor, so `9.1.0` matched neither `9.0`, `8.0`
-- nor `7.0`; every discovered 9.1 component therefore stayed catalog-unlinked even
-- after #1079's content-import fix landed. Re-keying to '9.x' (the same precedented
-- major-line scope-key form the `nsx`/`vcf` rows already use -- no new key form, no
-- version range, no nearest-version fallback) makes `9.1.0` match correctly.
--
-- This is catalog KEYS ONLY, same class of change as 0070: no schema shape changes, no
-- new runner grants. Shipped migrations (0064/0067/0069/0070) are immutable and are
-- never edited in place; this is a new, idempotent, FK-preserving migration that
-- re-uses 0070's own MERGE idiom, scoped to the single vsphere/9.0 -> vsphere/9.x
-- rename (see 0070's header for why a naive one-shot rename is not idempotent-safe
-- against SchemaMigrationTests' raw-SQL migration replay: 0067 unconditionally
-- re-inserts a fresh '9.0.0'->'9.0' row on every replay via 0070, so this migration
-- must also merge on every run, not just the first).
--
-- Slot verified free against the migrations directory (ends at 0073 on main) and open
-- PRs at this migration's own commit time: 0074 (PR #1076) and 0075 (issue #784) are
-- claimed and unpushed; 0076 was assigned to this issue by epic #726 coordination.

DO $$
DECLARE
    target_version_id UUID;
    old_row RECORD;
BEGIN
    INSERT INTO catalog_product_versions (product_id, version_key, display_name)
    SELECT p.id, '9.x', 'vSphere 9.x'
    FROM catalog_products p
    WHERE p.product_key = 'vsphere'
    ON CONFLICT (product_id, version_key) DO NOTHING;

    SELECT pv.id INTO target_version_id
    FROM catalog_product_versions pv
    JOIN catalog_products p ON p.id = pv.product_id
    WHERE p.product_key = 'vsphere' AND pv.version_key = '9.x';

    FOR old_row IN
        SELECT pv.id
        FROM catalog_product_versions pv
        JOIN catalog_products p ON p.id = pv.product_id
        WHERE p.product_key = 'vsphere' AND pv.version_key = '9.0' AND pv.id <> target_version_id
    LOOP
        UPDATE catalog_components old_component
        SET product_version_id = target_version_id
        WHERE old_component.product_version_id = old_row.id
          AND NOT EXISTS (
              SELECT 1 FROM catalog_components existing
              WHERE existing.product_version_id = target_version_id
                AND existing.component_key = old_component.component_key
                AND existing.parent_component_id IS NOT DISTINCT FROM old_component.parent_component_id
          );

        DELETE FROM catalog_benchmark_references
        WHERE execution_profile_id IN (
            SELECT ep.id FROM catalog_execution_profiles ep
            JOIN catalog_components cc ON cc.id = ep.component_id
            WHERE cc.product_version_id = old_row.id
        );
        DELETE FROM catalog_credential_requirements
        WHERE execution_profile_id IN (
            SELECT ep.id FROM catalog_execution_profiles ep
            JOIN catalog_components cc ON cc.id = ep.component_id
            WHERE cc.product_version_id = old_row.id
        );
        DELETE FROM catalog_execution_profiles
        WHERE component_id IN (SELECT id FROM catalog_components WHERE product_version_id = old_row.id);
        DELETE FROM catalog_components WHERE product_version_id = old_row.id;

        DELETE FROM catalog_product_versions WHERE id = old_row.id;
    END LOOP;
END $$;
