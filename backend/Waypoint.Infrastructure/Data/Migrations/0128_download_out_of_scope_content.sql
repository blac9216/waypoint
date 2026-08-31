-- Issue #1440 (epic #1182 "Subscriptions, retention & scheduling", split from
-- design record #1047, approved design #16 section 2; blocked by #1406/migration
-- 0107; slot 0128 -- the next free slot after #1436's 0127 at the time this
-- migration was authored): the persisted half of the review-list mechanism for
-- out-of-scope content (the other half, orphans, already exists as
-- unknown_catalog_files, migration 0100/#1488). Same insert-or-touch-last-seen,
-- no-delete-path shape as unknown_catalog_files -- this table has deliberately no
-- DELETE/remove path anywhere its own repository exposes (same Q11 "alert instead
-- of drop" decision unknown_catalog_files already establishes), enforced by
-- ReviewListServiceTests' structural no-delete-method proof.
--
-- download_out_of_scope_content -----------------------------------------------------
-- One row per depot_artifacts row a caller has identified as outside every
-- subscription's scope (a tracked, catalogued artifact nothing currently
-- subscribes to -- distinct from an orphan, which has no depot_artifacts row at
-- all). FK to depot_artifacts(id) ON DELETE CASCADE, mirroring
-- download_retained_content_state's own identity-anchor convention (0107's
-- header). Real out-of-scope discovery needs a Subscription entity that does not
-- exist yet (#1421 is still open) -- same deferred-discovery seam
-- RetentionSweepService's own doc comment documents for its candidate list, and
-- the same reasoning: IReviewListService.ReportOutOfScopeAsync takes the
-- depot_artifact_id as an explicit caller-supplied input rather than querying for
-- it, so the safety guarantee ("never auto-removed") is provable today,
-- independent of the still-missing discovery mechanism.
CREATE TABLE IF NOT EXISTS download_out_of_scope_content (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    depot_artifact_id UUID NOT NULL REFERENCES depot_artifacts (id) ON DELETE CASCADE,
    reason TEXT NOT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT download_out_of_scope_content_depot_artifact_id_key UNIQUE (depot_artifact_id)
);

CREATE INDEX IF NOT EXISTS idx_download_out_of_scope_content_last_seen_at
    ON download_out_of_scope_content (last_seen_at);

COMMENT ON TABLE download_out_of_scope_content IS
    'Issue #1440: depot_artifacts rows a caller has identified as outside every subscription''s scope -- review-list-only, never auto-removed. Insert-or-touch-last-seen; no delete path (Q11: alert instead of drop).';
COMMENT ON COLUMN download_out_of_scope_content.reason IS
    'Free-text explanation the reporting caller supplies (e.g. "no subscription references this product/version"); not machine-parsed.';

-- Runner grants: deliberately NONE. No consumer -- neither runner role -- reads or
-- writes this table yet; real out-of-scope discovery (a scope-evaluation pass
-- keyed off the Subscription entity, #1421) is future work, and per this repo's
-- 0100/#1484 precedent ("the sync job that reads this table grants itself what it
-- needs when it lands"), that future consumer ships its own GRANT migration.
-- Today IReviewListService.ReportOutOfScopeAsync runs in the API process only
-- (same connection-string-gated composition root as every other Downloads-domain
-- repository -- see ServiceCollectionExtensions), which needs no GRANT since it
-- already owns the schema.
