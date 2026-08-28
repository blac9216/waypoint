-- Issue #998 (epic #726), CORRECTED owner decision (2026-08-28, superseding an earlier
-- "minor-level keys" comment posted prematurely on the same issue): verified against the
-- vendor repo, the tree is HETEROGENEOUS -- some product trees declare a minor-scoped
-- version directory (vsphere/7.0, vsphere/8.0) and others declare a major-line-scoped
-- directory (vcf/9.x; NSX/Aria/vIDM follow the same shape, the vendor's own profile
-- titles literally say "9.X"). The catalog product-version key is therefore the
-- vendor's DECLARED VERSION SCOPE, VERBATIM -- whatever that content directory
-- declares -- never a Waypoint-normalized minor-level key and never a patch-level exact
-- identity. Matching that scope against an observed/configured fact is computed at
-- lookup time by Waypoint.Core.Components.VersionScopeMatcher's closed two-form test
-- (this migration changes CATALOG KEYS ONLY; it does not touch matching logic, which
-- lives in application code reviewed by PR #998's own tests).
--
-- Migrations 0064/0067/0069 (issues #959/#967/#977) seeded catalog_product_versions
-- with keys that predate this decision -- some patch-level ("8.0.3"), some already an
-- invented-but-still-wrong exact triple ("9.0.0", "4.1.2", "8.0.0", "3.3.0"). This
-- migration reconciles every one of them to the declared-scope verbatim form the
-- docs/compliance-parity.md provenance matrix documents for that row (this same PR
-- updates the doc's provenance-matrix key-form column to match -- see that file's own
-- diff for the authoritative mapping). Shipped migrations are IMMUTABLE (0064/0067/0069
-- are never edited in place); this is a NEW, idempotent, FK-preserving migration.
--
-- Key-reconciliation table (old seeded key -> new declared-scope key; see this PR's own
-- body for the same table with doc-row citations):
--   vsphere / 8.0.3  -> 8.0   (vSphere `8-0`, exact, minor-scoped)
--   vsphere / 9.0.0  -> 9.0   (vSphere `9-0`, exact, minor-scoped)
--   nsx     / 4.1.2  -> 4.x   (NSX `4-x`, family, major-line-scoped)
--   nsx     / 9.0.0  -> 9.x   (NSX `9-x`, family, major-line-scoped)
--   photon  / 5.0    -> 5.0   (Photon OS `5-0`, exact, minor-scoped -- already verbatim, no change)
--   aria-operations       / 8.0.0 -> 8.x   (Aria Operations `8-x`, family, major-line-scoped)
--   aria-automation       / 8.0.0 -> 8.x   (Aria Automation `8-x`, family, major-line-scoped)
--   aria-suite-lifecycle  / 8.0.0 -> 8.x   (Aria Suite Lifecycle `8-x`, family, major-line-scoped)
--   vidm    / 3.3.0  -> 3.3.x (Workspace ONE Access `3-3-x`, family, major-line-scoped)
--   vcf     / 9.0.0  -> 9.x   (VCF `9-x`, family, major-line-scoped)
--
-- FK graph checked first (migration 0050): catalog_components.product_version_id,
-- catalog_execution_profiles.component_id, catalog_credential_requirements.
-- execution_profile_id, and catalog_benchmark_references.execution_profile_id all
-- reference catalog_product_versions/catalog_components/catalog_execution_profiles by
-- SURROGATE UUID, never by version_key text. Renaming/merging version_key therefore
-- requires zero cascading updates to any dependent row's own identity -- every
-- catalog_components / catalog_execution_profiles / catalog_credential_requirements /
-- catalog_benchmark_references row keeps its existing product_version_id/component_id/
-- execution_profile_id untouched (or is re-pointed to the surviving new-key row's id,
-- see the MERGE loop below), and still resolves correctly through the FK afterward.
--
-- Re-apply-idempotency shape: unlike a typical migration, this one must stay a no-op
-- not only when RE-RUN BY ITSELF, but also when SchemaMigrationTests replays every
-- embedded migration's raw SQL in order (0064, 0067, 0069, THEN this one) against a
-- database that already ran this migration once -- 0064/0067/0069 are immutable and
-- their own idempotency is "ON CONFLICT DO NOTHING against the OLD key", so replaying
-- them after this migration already renamed that key away is NOT a no-op: it happily
-- inserts a FRESH catalog_product_versions row under the old key again (no conflict --
-- the old key no longer exists), which would then collide with the already-renamed
-- catalog_components on catalog_components_null_parent_unique the moment this
-- migration's naive "old row exists, new row doesn't -> rename" logic ran only once.
-- This migration is therefore written as an unconditional, repeatable MERGE for each
-- product: it merges EVERY row currently sitting under a recognized old key into the
-- (possibly freshly created, possibly already-existing) declared-scope row, every single
-- time it runs -- so a 0064-reseeded "8.0.3" row is merged into "8.0" again on the very
-- next 0070 replay, not just the first one.

DO $$
DECLARE
    target_version_id UUID;
    old_row RECORD;
    reconciliation RECORD;
BEGIN
    FOR reconciliation IN
        SELECT * FROM (VALUES
            ('vsphere', '8.0.3', '8.0', 'vSphere 8.0'),
            ('vsphere', '9.0.0', '9.0', 'vSphere 9.0'),
            ('nsx', '4.1.2', '4.x', 'NSX 4.x'),
            ('nsx', '9.0.0', '9.x', 'NSX 9.x'),
            ('aria-operations', '8.0.0', '8.x', 'Aria Operations 8.x'),
            ('aria-automation', '8.0.0', '8.x', 'Aria Automation 8.x'),
            ('aria-suite-lifecycle', '8.0.0', '8.x', 'Aria Suite Lifecycle 8.x'),
            ('vidm', '3.3.0', '3.3.x', 'Workspace ONE Access 3.3.x'),
            ('vcf', '9.0.0', '9.x', 'VMware Cloud Foundation 9.x')
            -- photon / 5.0 is already the declared-scope verbatim form (Photon OS
            -- `5-0`, exact, minor-scoped) -- no rename needed, omitted here (listed in
            -- this file's header reconciliation table only for completeness/
            -- traceability against every provenance-matrix row).
        ) AS t(product_key, old_key, new_key, new_display_name)
    LOOP
        -- Ensure the declared-scope target row exists for this product (idempotent:
        -- inserts it the first time this runs; every later run/replay finds it via the
        -- unique (product_id, version_key) constraint and changes nothing).
        INSERT INTO catalog_product_versions (product_id, version_key, display_name)
        SELECT p.id, reconciliation.new_key, reconciliation.new_display_name
        FROM catalog_products p
        WHERE p.product_key = reconciliation.product_key
        ON CONFLICT (product_id, version_key) DO NOTHING;

        SELECT pv.id INTO target_version_id
        FROM catalog_product_versions pv
        JOIN catalog_products p ON p.id = pv.product_id
        WHERE p.product_key = reconciliation.product_key AND pv.version_key = reconciliation.new_key;

        -- Merge EVERY row still sitting under the old key into the target -- not just
        -- "the first one ever found": a raw-SQL replay of 0064/0067/0069 after this
        -- migration already ran once re-inserts a fresh old-key row (their own
        -- ON CONFLICT no longer sees a conflict, because the old key was renamed away),
        -- so this loop must keep catching that on every future run, not only the first.
        FOR old_row IN
            SELECT pv.id
            FROM catalog_product_versions pv
            JOIN catalog_products p ON p.id = pv.product_id
            WHERE p.product_key = reconciliation.product_key
              AND pv.version_key = reconciliation.old_key
              AND pv.id <> target_version_id
        LOOP
            -- Re-point every dependent catalog_components row from the old id to the
            -- target id, honestly merging rather than duplicating: a component that
            -- already exists under the target (same component_key, both top-level) is
            -- left as-is and the old duplicate is dropped instead of violating
            -- catalog_components_null_parent_unique; a component that exists only under
            -- the old row is re-pointed to survive under the target.
            UPDATE catalog_components old_component
            SET product_version_id = target_version_id
            WHERE old_component.product_version_id = old_row.id
              AND NOT EXISTS (
                  SELECT 1 FROM catalog_components existing
                  WHERE existing.product_version_id = target_version_id
                    AND existing.component_key = old_component.component_key
                    AND existing.parent_component_id IS NOT DISTINCT FROM old_component.parent_component_id
              );

            -- Any old-row component that could NOT be re-pointed above (because an
            -- equivalent already exists under the target) is now an orphaned duplicate
            -- -- its execution profiles/credential requirements/benchmark references
            -- are dropped with it (ON DELETE RESTRICT means the FK graph must be
            -- cleared child-first, deepest table first).
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
    END LOOP;
END $$;
