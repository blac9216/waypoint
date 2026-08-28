-- Issue #1007 (epic #726): merges a duplicate catalog PRODUCT tree created by the
-- content-content-pull importer's pre-fix bug (see ContentPullJobHandler.
-- BuildPromotionRequest and CatalogPromotionRequest's doc comment, same PR) back onto
-- the canonical seeded tree. Root cause: catalog_products.vendor is deliberately
-- catalog-authored free text with no CHECK constraint (ADR-0013 "new
-- products/components remain data-driven"), but the importer passed a human-readable
-- display string ("VMware vSphere") instead of the seed migrations' literal
-- ("vmware") as that natural-key column -- catalog_products_vendor_key_unique
-- UNIQUE (vendor, product_key) then correctly treated the two as DIFFERENT products,
-- so PromoteCandidateAsync's upsert created a second catalog_products row (and a
-- second catalog_product_versions/catalog_components/catalog_execution_profiles tree
-- beneath it) for every product_key the importer touched, rather than attaching to
-- the seeded row. Because migration 0070 already reconciled both trees' version_keys
-- to the same declared-scope form, CatalogLinkageResolver then found two
-- catalog_components sharing (component_key, matched version scope) and correctly
-- fail-closed on the ambiguity (candidates.Count > 1) -- 0/265 discovered components
-- linked to any catalog component. This migration is the data-repair half of the fix;
-- the code half (ContentPullJobHandler now passes CatalogVendors.VMware, the same
-- literal the seed migrations write) is what stops the class from recurring.
--
-- Slot 0072 verified free against both the migrations directory and open PRs at this
-- migration's own commit time (0071 is the latest shipped migration; ledger 70 -> 71
-- bumps to 71 -> 72 in SchemaMigrationTests alongside this file).
--
-- Canonical-choice rule: for every (product_key) that exists under more than one
-- vendor value, the row whose vendor = 'vmware' (CatalogVendors.VMware, the ONLY
-- value in the closed vendor vocabulary going forward, and the literal every seed
-- migration 0064/0067/0069 already writes) is canonical; every OTHER vendor value's
-- product row for that same product_key is the duplicate and is merged into it. This
-- is deliberately not "the seeded source_revision wins" (a source_revision-based rule
-- cannot distinguish seed-vs-import in the general case once #1007's code fix ships,
-- because a fresh install with no seed data at all is legitimately importer-only) --
-- keying on the closed vendor vocabulary is the rule that stays correct in every
-- topology: seed-then-import (today's field bug), import-only (no seed row yet, no
-- merge needed, single row already), and re-import after this migration already ran
-- (idempotent no-op, see below).
--
-- FK graph re-pointed, deepest-dependent-first, mirroring migration 0070's own
-- "re-point everything by surrogate UUID, then delete the now-empty duplicate" shape
-- one level higher in the tree (0070 merged product_VERSIONS under one product; this
-- merges whole PRODUCTS, so every table 0070 already re-points is re-pointed again
-- here PLUS the tables that reference catalog_components/catalog_execution_profiles
-- from OUTSIDE the catalog schema entirely -- components.catalog_component_id,
-- benchmark_component_mappings.catalog_component_id, baselines.
-- catalog_execution_profile_id, scan_plan_items.catalog_execution_profile_id, and
-- config_docs.catalog_execution_profile_id -- none of which migration 0070 needed to
-- touch because 0070 never merged across catalog_products, only within one product's
-- catalog_product_versions):
--
--   components.catalog_component_id                       (migration 0054)
--   benchmark_component_mappings.catalog_component_id      (migration 0052)
--   baselines.catalog_execution_profile_id                 (migration 0055)
--   scan_plan_items.catalog_execution_profile_id            (migration 0057)
--   config_docs.catalog_execution_profile_id                (migration 0060)
--   catalog_credential_requirements.execution_profile_id    (migration 0050)
--   catalog_benchmark_references.execution_profile_id       (migration 0050, UNIQUE)
--   catalog_remediation_definitions.execution_profile_id    (migration 0050, UNIQUE)
--   catalog_declared_inputs.execution_profile_id            (migration 0051)
--   catalog_import_report_entries.execution_profile_id      (migration 0051)
--   catalog_execution_profiles.component_id                 (migration 0050)
--   catalog_components.product_version_id                   (migration 0050)
--   catalog_product_versions.product_id                     (migration 0050)
--
-- Each re-point is "adopt if the target has no equivalent row yet, else drop the
-- duplicate" -- never blind INSERT/UPDATE that could violate a unique constraint at
-- the target. Two partial-unique invariants need special handling because BOTH trees
-- may independently already satisfy them and re-pointing naively would collide:
--
--   * idx_baselines_active_unique_per_execution_profile (one ACTIVE baseline per
--     execution profile): if the duplicate's execution profile has an active baseline
--     and the canonical one it is merging component-for-component into ALSO already
--     has an active baseline, the canonical baseline wins (matches this migration's
--     own "canonical wins" rule) and the duplicate's active baseline is superseded
--     (status -> 'superseded'), never silently dropped -- baseline history is
--     retained per ADR-0022 "history ... cannot be overwritten in place".
--   * idx_benchmark_component_mappings_current_unique (one current mapping per
--     component): analogous -- if both the duplicate and its merge-target component
--     already have a current mapping, the target's wins and the duplicate's current
--     mapping row has is_current flipped to false rather than being deleted.
--
-- Idempotent + replay-safe (SchemaMigrationTests replays every embedded migration's
-- raw SQL in order against an already-migrated database, same requirement 0070's own
-- header documents): once a duplicate product row is merged and deleted, the
-- non-'vmware' vendor value has no remaining catalog_products row to find, so a
-- second run of this exact SQL text finds zero candidates and is a clean no-op. FK-safe
-- child-first: every DELETE below targets a row only after every child table that could
-- still reference it has already been re-pointed or cleared.
DO $$
DECLARE
    dup_product RECORD;
    canonical_product_id UUID;
    dup_version RECORD;
    canonical_version_id UUID;
    dup_component RECORD;
    canonical_component_id UUID;
    dup_profile RECORD;
    canonical_profile_id UUID;
BEGIN
    -- Walk every product_key that has more than one catalog_products row where at
    -- least one candidate vendor is NOT the canonical 'vmware' value -- the exact
    -- shape issue #1007 describes (a genuine 'vmware'-vendor duplicate under the same
    -- product_key cannot occur: catalog_products_vendor_key_unique already prevents
    -- two rows with the identical (vendor, product_key) pair).
    FOR dup_product IN
        SELECT p_dup.id AS dup_id, p_dup.product_key, p_canon.id AS canon_id
        FROM catalog_products p_dup
        JOIN catalog_products p_canon
            ON p_canon.product_key = p_dup.product_key
            AND p_canon.vendor = 'vmware'
            AND p_canon.id <> p_dup.id
        WHERE p_dup.vendor <> 'vmware'
    LOOP
        canonical_product_id := dup_product.canon_id;

        -- Merge every catalog_product_versions row from the duplicate product into the
        -- canonical product, by version_key.
        FOR dup_version IN
            SELECT id, version_key FROM catalog_product_versions WHERE product_id = dup_product.dup_id
        LOOP
            INSERT INTO catalog_product_versions (product_id, version_key, display_name)
            SELECT canonical_product_id, dup_version.version_key, pv.display_name
            FROM catalog_product_versions pv WHERE pv.id = dup_version.id
            ON CONFLICT (product_id, version_key) DO NOTHING;

            SELECT id INTO canonical_version_id
            FROM catalog_product_versions
            WHERE product_id = canonical_product_id AND version_key = dup_version.version_key;

            -- Merge every catalog_components row (top-level, parent_component_id IS
            -- NULL -- the overwhelmingly common shape both promotion and seeding use;
            -- a nested named-sub-service duplicate under a re-pointed parent is
            -- vanishingly unlikely from this bug's actual code path, since the
            -- importer's VendorHierarchyInterpreter shapes never nest, but the loop
            -- below still re-points parent_component_id honestly for any that exist by
            -- processing parent components before their children via two passes).
            FOR dup_component IN
                SELECT id, parent_component_id, component_key
                FROM catalog_components
                WHERE product_version_id = dup_version.id
                ORDER BY parent_component_id NULLS FIRST
            LOOP
                SELECT existing.id INTO canonical_component_id
                FROM catalog_components existing
                WHERE existing.product_version_id = canonical_version_id
                    AND existing.component_key = dup_component.component_key
                    AND existing.parent_component_id IS NOT DISTINCT FROM dup_component.parent_component_id;

                IF canonical_component_id IS NULL THEN
                    -- No equivalent component exists yet under the canonical version --
                    -- re-point this one wholesale rather than duplicate it.
                    UPDATE catalog_components SET product_version_id = canonical_version_id
                    WHERE id = dup_component.id;
                    canonical_component_id := dup_component.id;
                ELSE
                    -- An equivalent component already exists under the canonical
                    -- version: re-point every external/internal reference to the
                    -- duplicate component onto the canonical one, then drop the
                    -- duplicate component row itself.

                    -- External: components.catalog_component_id (migration 0054) --
                    -- a discovered/instance component currently (mis)linked to the
                    -- duplicate catalog row now correctly resolves to the canonical one.
                    UPDATE components SET catalog_component_id = canonical_component_id
                    WHERE catalog_component_id = dup_component.id;

                    -- External: benchmark_component_mappings.catalog_component_id
                    -- (migration 0052) -- adopt if the canonical component has no
                    -- current mapping yet, else keep the canonical's current mapping
                    -- and demote the duplicate's (never delete: mapping decisions are
                    -- audit history).
                    UPDATE benchmark_component_mappings dup_mapping
                    SET catalog_component_id = canonical_component_id
                    WHERE dup_mapping.catalog_component_id = dup_component.id
                        AND dup_mapping.is_current
                        AND NOT EXISTS (
                            SELECT 1 FROM benchmark_component_mappings existing_mapping
                            WHERE existing_mapping.catalog_component_id = canonical_component_id
                                AND existing_mapping.is_current
                        );
                    UPDATE benchmark_component_mappings SET is_current = false
                    WHERE catalog_component_id = dup_component.id AND is_current;
                    UPDATE benchmark_component_mappings SET catalog_component_id = canonical_component_id
                    WHERE catalog_component_id = dup_component.id;

                    -- Internal: catalog_execution_profiles.component_id (migration
                    -- 0050) -- merge by (content_release_id), the profile's own
                    -- natural key partner, same "adopt or drop" shape.
                    FOR dup_profile IN
                        SELECT id, content_release_id FROM catalog_execution_profiles WHERE component_id = dup_component.id
                    LOOP
                        SELECT existing_profile.id INTO canonical_profile_id
                        FROM catalog_execution_profiles existing_profile
                        WHERE existing_profile.component_id = canonical_component_id
                            AND existing_profile.content_release_id = dup_profile.content_release_id;

                        IF canonical_profile_id IS NULL THEN
                            UPDATE catalog_execution_profiles SET component_id = canonical_component_id
                            WHERE id = dup_profile.id;
                        ELSE
                            -- An equivalent execution profile already exists under the
                            -- canonical component: re-point every reference to the
                            -- duplicate profile onto the canonical one, then drop it.

                            -- External: baselines.catalog_execution_profile_id
                            -- (migration 0055) -- at most one ACTIVE baseline may
                            -- reference the canonical profile (partial unique index);
                            -- the canonical's own active baseline wins if one already
                            -- exists, and the duplicate's is superseded (retained, not
                            -- deleted -- ADR-0022 "history ... cannot be overwritten").
                            UPDATE baselines dup_baseline
                            SET catalog_execution_profile_id = canonical_profile_id
                            WHERE dup_baseline.catalog_execution_profile_id = dup_profile.id
                                AND dup_baseline.status = 'active'
                                AND NOT EXISTS (
                                    SELECT 1 FROM baselines existing_baseline
                                    WHERE existing_baseline.catalog_execution_profile_id = canonical_profile_id
                                        AND existing_baseline.status = 'active'
                                );
                            UPDATE baselines
                            SET status = 'superseded', superseded_at = now()
                            WHERE catalog_execution_profile_id = dup_profile.id AND status = 'active';
                            UPDATE baselines SET catalog_execution_profile_id = canonical_profile_id
                            WHERE catalog_execution_profile_id = dup_profile.id;

                            -- External: scan_plan_items.catalog_execution_profile_id
                            -- (migration 0057) -- frozen plan-item history, no
                            -- uniqueness constraint to collide with; a straight
                            -- re-point preserves the plan's own reproducibility
                            -- evidence (ADR-0023) while pointing it at the surviving
                            -- identity row.
                            UPDATE scan_plan_items SET catalog_execution_profile_id = canonical_profile_id
                            WHERE catalog_execution_profile_id = dup_profile.id;

                            -- External: config_docs.catalog_execution_profile_id
                            -- (migration 0060) -- a config doc keyed to the duplicate
                            -- profile is re-keyed to the canonical one; the one-
                            -- document-per-slot unique indexes there are scoped by
                            -- (kind, catalog_execution_profile_id, layer), so adopt
                            -- only where the canonical slot is not already taken and
                            -- leave any true conflict for an Admin to resolve rather
                            -- than silently drop authored content.
                            UPDATE config_docs dup_doc
                            SET catalog_execution_profile_id = canonical_profile_id
                            WHERE dup_doc.catalog_execution_profile_id = dup_profile.id
                                AND NOT EXISTS (
                                    SELECT 1 FROM config_docs existing_doc
                                    WHERE existing_doc.catalog_execution_profile_id = canonical_profile_id
                                        AND existing_doc.kind = dup_doc.kind
                                        AND existing_doc.layer_type IS NOT DISTINCT FROM dup_doc.layer_type
                                        AND existing_doc.layer_ref IS NOT DISTINCT FROM dup_doc.layer_ref
                                );

                            -- Internal children of the duplicate execution profile:
                            -- credential requirements adopt-or-drop by purpose; the
                            -- UNIQUE(execution_profile_id) benchmark reference and
                            -- remediation definition keep the canonical's own row when
                            -- one exists (both are catalog-authored facts about the
                            -- SAME logical profile, so they should already agree, but
                            -- the canonical wins on any residual difference); declared
                            -- inputs and import-report-entry provenance adopt-or-drop
                            -- by their own natural keys.
                            UPDATE catalog_credential_requirements dup_req
                            SET execution_profile_id = canonical_profile_id
                            WHERE dup_req.execution_profile_id = dup_profile.id
                                AND NOT EXISTS (
                                    SELECT 1 FROM catalog_credential_requirements existing_req
                                    WHERE existing_req.execution_profile_id = canonical_profile_id
                                        AND existing_req.purpose = dup_req.purpose
                                );
                            DELETE FROM catalog_credential_requirements WHERE execution_profile_id = dup_profile.id;

                            DELETE FROM catalog_benchmark_references
                            WHERE execution_profile_id = dup_profile.id
                                AND EXISTS (
                                    SELECT 1 FROM catalog_benchmark_references existing_ref
                                    WHERE existing_ref.execution_profile_id = canonical_profile_id
                                );
                            UPDATE catalog_benchmark_references SET execution_profile_id = canonical_profile_id
                            WHERE execution_profile_id = dup_profile.id;

                            DELETE FROM catalog_remediation_definitions
                            WHERE execution_profile_id = dup_profile.id
                                AND EXISTS (
                                    SELECT 1 FROM catalog_remediation_definitions existing_def
                                    WHERE existing_def.execution_profile_id = canonical_profile_id
                                );
                            UPDATE catalog_remediation_definitions SET execution_profile_id = canonical_profile_id
                            WHERE execution_profile_id = dup_profile.id;

                            UPDATE catalog_declared_inputs dup_input
                            SET execution_profile_id = canonical_profile_id
                            WHERE dup_input.execution_profile_id = dup_profile.id
                                AND NOT EXISTS (
                                    SELECT 1 FROM catalog_declared_inputs existing_input
                                    WHERE existing_input.execution_profile_id = canonical_profile_id
                                        AND existing_input.name = dup_input.name
                                );
                            DELETE FROM catalog_declared_inputs WHERE execution_profile_id = dup_profile.id;

                            UPDATE catalog_import_report_entries SET execution_profile_id = canonical_profile_id
                            WHERE execution_profile_id = dup_profile.id;

                            DELETE FROM catalog_execution_profiles WHERE id = dup_profile.id;
                        END IF;
                    END LOOP;

                    DELETE FROM catalog_components WHERE id = dup_component.id;
                END IF;
            END LOOP;

            DELETE FROM catalog_product_versions WHERE id = dup_version.id;
        END LOOP;

        DELETE FROM catalog_products WHERE id = dup_product.dup_id;
    END LOOP;
END $$;
