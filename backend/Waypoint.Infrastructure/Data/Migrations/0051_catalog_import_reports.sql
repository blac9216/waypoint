-- Issue #729 (epic #726 Wave 1 remainder), persistence slice: import-report storage
-- and declared profile inputs. ADR-0022 "immutable source observations, artifacts,
-- digests, parse results, and provenance will be retained" -- this migration persists
-- the deterministic SemanticImportReport (Waypoint.Core.ComplianceContent.SemanticImport)
-- that PR #823's importer already computes in-process but never wrote to storage, plus
-- candidate promotion's one missing catalog fact: declared inputs (name/type/required),
-- content-derived from inspec.yml and not represented anywhere in migration 0050's
-- schema. Declared inputs are the last #728 AC ("declared and consumed inputs ...
-- queryable") this migration also closes.
--
-- Scope discipline: this migration does NOT add new identity-tree tables -- candidate
-- promotion reuses 0050's catalog_source_revisions/products/product_versions/
-- components/content_releases/execution_profiles/report_groups via
-- ICatalogRepository's existing upsert methods (additive ingestion: an accepted
-- candidate upserts by natural key, it never mutates an already-active execution
-- profile's identity). Two new tables only:
--   catalog_import_reports          -- one row per SemanticImportReport (header:
--                                       source commit/digest, counts, recorded_at)
--   catalog_import_report_entries   -- one row per accepted/warning/rejected entry in
--                                       that report, so operators can inspect exactly
--                                       what one import run did (issue #729 deliverable
--                                       5 "accepted entries, warnings, and rejected
--                                       entries")
-- plus one new table for the #728 declared-inputs remainder:
--   catalog_declared_inputs         -- one row per declared InSpec input
--                                       (name/type/required) for an execution profile
--
-- Immutability/history: catalog_import_reports rows are never updated in place (a
-- re-import of byte-identical content produces a report with the same source_digest,
-- which is deliberately NOT unique-constrained here -- two distinct pull attempts over
-- the same content are two distinct provenance events, ADR-0022 "immutable source
-- observations ... will be retained", not deduplicated at the report-header level;
-- deduplication happens one level down, at candidate PROMOTION, via 0050's existing
-- natural-key upserts). All FKs use ON DELETE RESTRICT/CASCADE-from-parent-report-only,
-- matching 0050's "referenced historical revisions cannot be deleted accidentally"
-- convention: entries cascade with their owning report (they have no independent
-- identity outside it), but a report row itself is never deleted by anything in this
-- migration.

-- catalog_import_reports --------------------------------------------------------------
-- One row per SemanticImportReport (one content-pull's semantic-import pass).
-- source_commit/source_digest mirror SemanticImportReport's deterministic fields
-- (issue #729 deliverable 5). Counts are denormalized from the entry rows below purely
-- for cheap list-view rendering; the entries table remains the source of truth.
CREATE TABLE IF NOT EXISTS catalog_import_reports (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_commit TEXT NOT NULL,
    source_digest TEXT NOT NULL,
    accepted_count INTEGER NOT NULL,
    warning_count INTEGER NOT NULL,
    rejected_count INTEGER NOT NULL,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_catalog_import_reports_recorded_at ON catalog_import_reports (recorded_at DESC);

-- catalog_import_report_entries --------------------------------------------------------
-- One row per SemanticImportAccepted/SemanticImportWarning/SemanticImportRejected entry
-- (issue #729 deliverable 5). `disposition` is the closed accepted|warning|rejected
-- vocabulary; `profile_key` is the vendor-repository-relative path identity
-- (SemanticCandidate.ProfileKey); `reason` carries the warning message or rejection
-- reason (NULL for an accepted entry with nothing to report); `execution_profile_id` is
-- populated only for an accepted entry that promotion successfully turned into a
-- catalog_execution_profiles row (NULL until/unless promoted, and permanently NULL for
-- warning/rejected entries, which are never promoted).
CREATE TABLE IF NOT EXISTS catalog_import_report_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id UUID NOT NULL REFERENCES catalog_import_reports (id) ON DELETE CASCADE,
    disposition TEXT NOT NULL,
    profile_key TEXT NOT NULL,
    reason TEXT NULL,
    execution_profile_id UUID NULL REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_import_report_entries_disposition_check CHECK (disposition IN ('accepted', 'warning', 'rejected')),
    CONSTRAINT catalog_import_report_entries_execution_profile_check CHECK (
        execution_profile_id IS NULL OR disposition = 'accepted'
    )
);

CREATE INDEX IF NOT EXISTS idx_catalog_import_report_entries_report_id ON catalog_import_report_entries (report_id);
CREATE INDEX IF NOT EXISTS idx_catalog_import_report_entries_profile_key ON catalog_import_report_entries (profile_key);

-- catalog_declared_inputs ---------------------------------------------------------------
-- Declared InSpec inputs (issue #728 AC "declared and consumed inputs ... queryable",
-- issue #729 AC "profile title, version, declared inputs ... are populated from source
-- metadata"). One row per (execution_profile_id, name) -- InspecManifestInput's
-- name/type/required, content-derived and never operator-authored (ADR-0022:
-- "Operators cannot upload executable plugins, scripts, or catalog mappings" extends to
-- their declared-input contracts, which arrive only via the reviewed importer).
-- `input_type` is free text (InSpec's own type vocabulary -- string/numeric/array/...
-- -- is not closed here; the importer captures whatever inspec.yml declares) rather than
-- a CHECK-constrained closed set, unlike migration 0050's transport/selector/kind
-- vocabulary, which the catalog itself defines.
CREATE TABLE IF NOT EXISTS catalog_declared_inputs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    execution_profile_id UUID NOT NULL REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT,
    name TEXT NOT NULL,
    input_type TEXT NULL,
    is_required BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT catalog_declared_inputs_unique UNIQUE (execution_profile_id, name)
);

CREATE INDEX IF NOT EXISTS idx_catalog_declared_inputs_execution_profile_id ON catalog_declared_inputs (execution_profile_id);

-- Runner grants (ADR-0017/#729: content-pull/content-import execute in
-- compliance-runner). The compliance-runner writes import reports and promotes
-- candidates as part of the same content-pull job that already upserts
-- compliance_content/profiles (0035); it needs write access to the two new report
-- tables and to every 0050 catalog identity-tree table plus the new declared-inputs
-- table it promotes candidates into. Catalog rows remain "catalog-authored" in the
-- sense that only the reviewed importer (product code, not an operator-controlled
-- upload) produces them (ADR-0022), but the WRITER is now the compliance-runner
-- process, not just migration-time seeding -- 0050's original "no runner write grant
-- because no runner mutates this schema yet" note is superseded for exactly the tables
-- this migration's promotion path touches.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_compliance_runner') THEN
        RAISE EXCEPTION 'role "waypoint_compliance_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, INSERT ON catalog_import_reports TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON catalog_import_report_entries TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_source_revisions TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_products TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_product_versions TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_content_releases TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_components TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_report_groups TO waypoint_compliance_runner;
GRANT SELECT, INSERT ON catalog_execution_profiles TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_credential_requirements TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_benchmark_references TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_remediation_definitions TO waypoint_compliance_runner;
GRANT SELECT, INSERT, UPDATE ON catalog_declared_inputs TO waypoint_compliance_runner;
