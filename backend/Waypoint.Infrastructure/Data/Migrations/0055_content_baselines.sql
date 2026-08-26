-- Issue #731 (epic #726 Wave 1 capstone: "Stage, diff, activate, and retain
-- compliance content revisions atomically"). ADR-0022 is the governing decision;
-- docs/api-contract.md's planned "Catalog, content sources, and exact-version
-- baselines" section already sketches this exact `baselines` shape:
-- "(product_version, profile_version, xccdf_version, status, activated_at/by)".
--
-- Scope discipline (this PR's first slice of #731, per the issue's own "API exposure
-- may be a stated remainder" and epic sequencing): this migration persists two new
-- tables that make staged content immutable and activation atomic:
--
--   content_revisions -- one immutable, digest-addressed staged filesystem snapshot
--                         of vendor/XCCDF content (the directory
--                         ComplianceContentOptions.ContentPath/revisions/<digest>
--                         ContentPullJobHandler now writes into instead of mutating
--                         the live working tree in place). A pull/import failure never
--                         touches an existing row here -- it simply never inserts one.
--   baselines         -- one coherent activation unit: exactly one content_revision +
--                         one catalog_execution_profile (+ optional benchmark_revision
--                         for STIG) treated as a single atomic pointer swap. Only a
--                         `status = 'active'` row is scan-eligible; activation and
--                         rollback are the ONLY operations that may create a new active
--                         row or supersede an old one (ADR-0022 "the activation
--                         boundary is exclusive" -- sync/import/parser code writes only
--                         immutable observations and staged rows, never `baselines`).
--
-- Retention (issue #731 AC "historical run references prevent premature revision
-- cleanup"): every FK below is ON DELETE RESTRICT, matching migrations 0050/0052's
-- convention -- a content_revisions/baselines row referenced by anything can never be
-- silently orphaned by a delete. `gc_eligible` is an explicit boolean marker only (no
-- GC job exists yet in this slice) so a future reaper has a queryable signal without
-- ever deleting rows itself here.
--
-- Atomicity (issue #731 AC "activation is atomic across semantic catalog and content
-- paths" + "a scan always resolves files from one immutable activated revision"):
-- exactly one CURRENT active baseline per catalog_execution_profile_id is enforced by
-- the partial unique index below, mirroring migration 0052's
-- idx_benchmark_component_mappings_current_unique idiom -- superseding a baseline
-- flips the old row's status to 'superseded' in the SAME transaction that activates
-- the new one (BaselineActivationService), never leaving two active rows for one
-- execution profile.

-- content_revisions -----------------------------------------------------------------
-- One immutable staged filesystem snapshot. `content_digest` is the SHA-256 of the
-- staged directory tree (see ContentRevisionStager); re-staging byte-identical content
-- for the same source is idempotent by (source_commit, content_digest) rather than
-- creating a duplicate revision directory. `status` tracks the revision's own
-- lifecycle independent of whether any baseline currently references it as active --
-- a revision can be 'staged' (parsed/validated, not yet part of any active baseline),
-- 'activated' (at least one current active baseline references it),
-- 'superseded' (was activated, no longer is), or 'rejected' (validation failed,
-- retained for diagnostics per ADR-0022 "immutable source observations ... will be
-- retained", never deleted).
CREATE TABLE IF NOT EXISTS content_revisions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_commit TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    staged_relative_path TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'staged',
    gc_eligible BOOLEAN NOT NULL DEFAULT false,
    staged_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT content_revisions_status_check CHECK (status IN ('staged', 'activated', 'superseded', 'rejected')),
    CONSTRAINT content_revisions_digest_unique UNIQUE (source_commit, content_digest)
);

CREATE INDEX IF NOT EXISTS idx_content_revisions_status ON content_revisions (status);

-- baselines ---------------------------------------------------------------------------
-- One atomically-activatable coherent set (issue #731 AC "activation is atomic across
-- semantic catalog and content paths"). `benchmark_revision_id` is nullable -- SRG
-- baselines have no XCCDF (ADR-0022) -- but a STIG execution profile's baseline is
-- expected (by application-level validation in BaselineActivationService, not a CHECK
-- here, since "STIG-backed" is a catalog_benchmark_references join, not a column on
-- this row) to carry one. `status` mirrors content_revisions' vocabulary but is scoped
-- to the ACTIVATION unit, not the underlying revision (one content_revision may be
-- referenced by baselines for several different execution profiles, e.g. multiple
-- components shipped in the same vendor pull).
CREATE TABLE IF NOT EXISTS baselines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    content_revision_id UUID NOT NULL REFERENCES content_revisions (id) ON DELETE RESTRICT,
    catalog_execution_profile_id UUID NOT NULL REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    benchmark_revision_id UUID NULL REFERENCES benchmark_revisions (id) ON DELETE RESTRICT,
    status TEXT NOT NULL DEFAULT 'staged',
    activated_at TIMESTAMPTZ NULL,
    activated_by TEXT NULL,
    superseded_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT baselines_status_check CHECK (status IN ('staged', 'active', 'superseded', 'rejected')),
    CONSTRAINT baselines_active_requires_activation_fields_check CHECK (
        (status = 'active' AND activated_at IS NOT NULL AND activated_by IS NOT NULL) OR (status <> 'active')
    )
);

CREATE INDEX IF NOT EXISTS idx_baselines_content_revision_id ON baselines (content_revision_id);
CREATE INDEX IF NOT EXISTS idx_baselines_catalog_execution_profile_id ON baselines (catalog_execution_profile_id);
CREATE INDEX IF NOT EXISTS idx_baselines_benchmark_revision_id ON baselines (benchmark_revision_id);

-- Exactly one ACTIVE baseline per execution profile -- "a compatible component has at
-- most one active baseline" (ADR-0022) made concrete at the database boundary, the
-- same partial-unique-index idiom as migration 0052's current-mapping invariant.
CREATE UNIQUE INDEX IF NOT EXISTS idx_baselines_active_unique_per_execution_profile
    ON baselines (catalog_execution_profile_id)
    WHERE status = 'active';

-- Runner grant: the compliance-runner process stages revisions during content-pull
-- (INSERT into content_revisions only -- it never writes baselines; activation and
-- rollback are Admin-only API actions performed through the owner connection,
-- ADR-0022 "the activation boundary is exclusive: sync/import/parser/reconciliation
-- operations write only immutable source observations and staged candidate state").
-- The column-scoped UPDATE (source_commit) exists ONLY because the idempotent
-- staging write is INSERT ... ON CONFLICT (source_commit, content_digest) DO UPDATE
-- SET source_commit = EXCLUDED.source_commit (a no-op touch that returns the
-- existing row) and Postgres requires UPDATE privilege on every column a DO UPDATE
-- arm names -- the runner still cannot flip status/gc_eligible/staged_relative_path,
-- which stay owner-only. SELECT on both tables lets a future scan-resolution path
-- (remainder of this issue, see PR body) read the active baseline without a further
-- grant migration.
GRANT SELECT, INSERT, UPDATE (source_commit) ON content_revisions TO waypoint_compliance_runner;
GRANT SELECT ON baselines TO waypoint_compliance_runner;
