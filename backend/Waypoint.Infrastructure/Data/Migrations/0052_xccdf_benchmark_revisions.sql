-- Issue #730 (epic #726 Wave 1): first-class ingestion, versioning, querying, and
-- mapping of DISA XCCDF/STIG benchmarks to executable catalog components. ADR-0022
-- ("Closed compliance catalog and atomic content lifecycle") is the governing
-- decision; docs/compliance-parity.md's closed capability vocabulary table is the
-- shape this schema represents.
--
-- Scope discipline (issue #730's own "will not fit one PR" split): this migration
-- persists SAFELY-PARSED, DIGEST-ADDRESSED XCCDF benchmark revisions and their rules,
-- plus the component-to-benchmark-revision mapping and its versioned audit history.
-- It does NOT implement source acquisition/sync scheduling, semantic-equivalence
-- reconciliation, candidate staging/diff, or baseline activation -- those remain
-- #729/#731 concerns layered on top. Every mapping row here records provenance
-- (system-suggested vs. explicit Admin override) but the Admin-only write API and
-- HTTP surface are deferred to the remainder PR named in issue #730's body.
--
-- Immutability and digest addressing (issue #730 AC "multiple revisions ... coexist
-- and are digest-addressed"): a benchmark_revisions row is never updated in place.
-- Re-importing byte-identical XCCDF content is idempotent by (benchmark_id,
-- content_digest) rather than creating a duplicate revision; importing a genuinely
-- different XCCDF for the same (benchmark_id, version, release) creates a new,
-- independently addressable revision row -- ADR-0022's "different complete artifacts
-- claiming the same identity/version" conflict is surfaced by the repository/importer
-- layer querying this table, not resolved in schema.
--
-- Historical-retention protection, matching migration 0050's convention: every FK in
-- this tree uses ON DELETE RESTRICT so a revision/rule referenced by a mapping or a
-- future plan can never be silently orphaned by a delete.

-- benchmark_revisions ---------------------------------------------------------------
-- One immutable, digest-addressed DISA XCCDF/STIG (or SRG "no published benchmark"
-- marker -- see benchmark_component_mappings below) revision. `benchmark_key` is the
-- exact DISA benchmark identity (e.g. an invented "VMW_vSphere_8-0_STIG" shape);
-- `version`/`release` are the exact XCCDF version/release pair (e.g. "2"/"3" for a
-- "V2R3"-shaped identity) -- never a range or family. `source` records where this
-- revision was observed from (manual upload vs. a configured STIG Manager connection)
-- as provenance only; it never implies precedence between sources (ADR-0022 "never
-- merge fragments or prefer arrival time"). `content_digest` is the SHA-256 of the
-- normalized parsed content (see importer) and is the addressing key alongside
-- `benchmark_id`: two imports with the same digest are the same revision.
CREATE TABLE IF NOT EXISTS benchmark_revisions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    benchmark_key TEXT NOT NULL,
    title TEXT NOT NULL,
    version TEXT NOT NULL,
    release TEXT NOT NULL,
    source TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    rule_count INTEGER NOT NULL DEFAULT 0,
    lifecycle_state TEXT NOT NULL DEFAULT 'staged',
    imported_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT benchmark_revisions_source_check CHECK (source IN ('manual_upload', 'stig_manager')),
    CONSTRAINT benchmark_revisions_lifecycle_state_check CHECK (lifecycle_state IN ('staged', 'active', 'superseded', 'rejected')),
    CONSTRAINT benchmark_revisions_rule_count_check CHECK (rule_count >= 0),
    -- Digest addressing (issue #730 AC): re-importing byte-identical content for the
    -- same benchmark identity must resolve to the SAME row, not a duplicate.
    CONSTRAINT benchmark_revisions_digest_unique UNIQUE (benchmark_key, content_digest)
);

CREATE INDEX IF NOT EXISTS idx_benchmark_revisions_benchmark_key ON benchmark_revisions (benchmark_key);
CREATE INDEX IF NOT EXISTS idx_benchmark_revisions_lifecycle_state ON benchmark_revisions (lifecycle_state);

-- benchmark_rules ---------------------------------------------------------------------
-- One rule/vulnerability within one immutable benchmark revision. `rule_id`/`vuln_id`
-- mirror XCCDF's own identifiers (e.g. invented "SV-000001r1_rule" /"V-000001" shapes)
-- exactly as declared in the source document -- never renumbered or re-derived.
-- `severity` is the closed CAT vocabulary DISA XCCDF documents (low|medium|high,
-- XCCDF's own "low/medium/high" spelling of CAT III/II/I). A revision's rules are
-- immutable once the revision is imported: correcting a rule means importing a new
-- revision, never editing this table in place.
CREATE TABLE IF NOT EXISTS benchmark_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    benchmark_revision_id UUID NOT NULL REFERENCES benchmark_revisions (id) ON DELETE RESTRICT,
    rule_id TEXT NOT NULL,
    vuln_id TEXT NOT NULL,
    severity TEXT NOT NULL,
    title TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT benchmark_rules_severity_check CHECK (severity IN ('low', 'medium', 'high')),
    CONSTRAINT benchmark_rules_unique UNIQUE (benchmark_revision_id, rule_id)
);

CREATE INDEX IF NOT EXISTS idx_benchmark_rules_benchmark_revision_id ON benchmark_rules (benchmark_revision_id);

-- benchmark_component_mappings --------------------------------------------------------
-- Exact catalog-component-to-benchmark-revision mapping (issue #730 AC "exact
-- component mappings replace target-kind metadata inference"). Exactly one CURRENT
-- mapping row per `catalog_component_id` (partial unique index below): the previous
-- mapping is never overwritten in place, it is superseded by inserting a new row and
-- marking the old one non-current, giving a versioned audit history entirely from
-- table state rather than a separate mutable "current mapping" pointer.
--
-- `status` distinguishes an unambiguous exact match (`mapped`), a system-suggested
-- match awaiting confirmation (`suggested`), an unresolved multi-candidate match
-- (`ambiguous`), and no candidate found (`unmapped`) -- issue #730 AC "unmatched/
-- ambiguous rules are queryable" at the mapping-set level; `benchmark_revision_id` is
-- therefore nullable (no target to point at yet) but every row still names the
-- component it concerns, so an unmapped/ambiguous component remains queryable rather
-- than simply absent.
--
-- `is_admin_override` distinguishes an explicit Admin decision from a system
-- suggestion (issue #730 AC "explicit Admin mapping/override with versioned audit
-- history"). `is_srg_no_benchmark` is the explicit "SRG content has no published DISA
-- benchmark" marker (issue #730 AC) -- set only for components whose bound catalog
-- content release is `srg` kind; such a row is deliberately never auto-mapped by name
-- similarity (mutually exclusive with a non-null benchmark_revision_id, enforced
-- below) and is not itself an "ambiguous"/"unmapped" defect.
CREATE TABLE IF NOT EXISTS benchmark_component_mappings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    catalog_component_id UUID NOT NULL REFERENCES catalog_components (id) ON DELETE RESTRICT,
    benchmark_revision_id UUID NULL REFERENCES benchmark_revisions (id) ON DELETE RESTRICT,
    status TEXT NOT NULL,
    is_srg_no_benchmark BOOLEAN NOT NULL DEFAULT false,
    is_admin_override BOOLEAN NOT NULL DEFAULT false,
    is_current BOOLEAN NOT NULL DEFAULT true,
    ambiguous_candidate_count INTEGER NOT NULL DEFAULT 0,
    reason TEXT NULL,
    actor TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT benchmark_component_mappings_status_check CHECK (status IN ('mapped', 'suggested', 'ambiguous', 'unmapped')),
    CONSTRAINT benchmark_component_mappings_srg_exclusive_check CHECK (
        NOT (is_srg_no_benchmark AND benchmark_revision_id IS NOT NULL)
    ),
    CONSTRAINT benchmark_component_mappings_mapped_requires_revision_check CHECK (
        (status = 'mapped' AND benchmark_revision_id IS NOT NULL) OR (status <> 'mapped')
    ),
    CONSTRAINT benchmark_component_mappings_ambiguous_count_check CHECK (ambiguous_candidate_count >= 0)
);

CREATE INDEX IF NOT EXISTS idx_benchmark_component_mappings_component_id ON benchmark_component_mappings (catalog_component_id);
CREATE INDEX IF NOT EXISTS idx_benchmark_component_mappings_revision_id ON benchmark_component_mappings (benchmark_revision_id);

-- Exactly one CURRENT mapping per component -- the versioned-audit-history invariant:
-- superseding a mapping must flip the old row's is_current to false in the same
-- transaction that inserts the new one, never leaving two current rows for one
-- component.
CREATE UNIQUE INDEX IF NOT EXISTS idx_benchmark_component_mappings_current_unique
    ON benchmark_component_mappings (catalog_component_id)
    WHERE is_current;
