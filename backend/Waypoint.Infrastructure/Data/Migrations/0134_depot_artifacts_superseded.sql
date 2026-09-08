-- Issue #1804 (spawned by #1512; documented remainder of #1784/PR #1805): reconciles
-- the narrower collision ordering RekeyAsync's own doc comment left unhandled -- the
-- offline presence sweep already created a row at a pull's new depot-relative
-- identity BEFORE the first post-#1784 connected pull ever ran, so the pull's rename
-- (a plain UPDATE ... WHERE relative_path = $1 AND NOT EXISTS (...)) found the TO
-- identity already taken and correctly no-op'd, leaving the stale legacy row in
-- place forever (no migration or one-time backfill reconciles it, and the rename
-- never retries).
--
-- IDepotArtifactRepository.RekeyManyAsync's collision path folds the legacy row's
-- still-valid facts (sha256/size_bytes/last_verified_at, COALESCE-only -- never
-- clobbering a fact the surviving row already has, the same convention UpsertAsync
-- already uses) into the surviving new-identity row, then marks the legacy row
-- superseded rather than deleting it: IDepotArtifactRepository has no Delete/Remove/
-- Purge-named member, enforced structurally by
-- ReviewListServiceTests.Interface_HasNoDeleteOrRemoveOrPurgeMethod against every
-- interface in this repository's dependency graph including this one (design #16
-- section 2's never-auto-remove policy) -- a status VALUE was rejected as the
-- marker because depot_artifacts_status_check is a closed vocabulary (migration
-- 0129) whose single source of truth, DepotArtifactStatusesConstraintDriftTests,
-- is owned by issue #1832's in-flight batch; a nullable timestamp column sidesteps
-- that constraint entirely rather than contending for it.
--
-- superseded_at is NULL for every row until a rekey collision marks one -- no writer
-- other than RekeyManyAsync ever sets it, and nothing ever clears it back to NULL
-- (matching the never-auto-remove policy: a superseded row stays superseded).
-- DepotArtifactRepository.ListAsync (the catalog/API read surface every consumer
-- lists through) unconditionally filters WHERE superseded_at IS NULL so a
-- superseded legacy row never appears as a duplicate; GetByIdAsync deliberately does
-- NOT filter on it, since a superseded row's id can still be the FK target of an
-- existing download_retained_content_state row (retention, issue #1436) minted
-- before this migration ever ran, and that lookup must keep resolving.
--
-- MIGRATION SLOT: 0134 is the next free slot after 0133 (verified against this
-- tree's Data/Migrations directory AND `gh pr list --state open --json
-- number,files` at authoring time -- 0131/0132/0133 are carried by open,
-- not-yet-merged PRs #1816/#1807/#1831). If any of those merges first with a
-- colliding slot, whoever merges second renumbers this file (and
-- SchemaMigrationTests.ExpectedMigrationCount), per the convention migration 0037
-- already documents.
ALTER TABLE depot_artifacts
    ADD COLUMN IF NOT EXISTS superseded_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN depot_artifacts.superseded_at IS
    'Issue #1804: set once, by RekeyManyAsync''s collision path, when this row is a stale pre-#1784 legacy-identity row whose facts were folded into a surviving new-identity row that already existed (the offline presence sweep having created it before this row''s first post-#1784 pull). Never cleared, never deleted (design #16 section 2). NULL for every ordinary row. ListAsync filters WHERE superseded_at IS NULL; GetByIdAsync does not, so an existing by-id FK reference (e.g. download_retained_content_state.depot_artifact_id) keeps resolving.';
