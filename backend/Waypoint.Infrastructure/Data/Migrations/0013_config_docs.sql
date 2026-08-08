-- Issue #265 (epic #13/#22, first slice of the #22 split): the storage half of the
-- three-layer STIG config document store -- inputs / attestations / remediation-inputs
-- at Global -> Site -> Target scope. docs/domain-model.md "STIG configuration
-- documents": "SAF attestation YAML, InSpec input YAML, remediation input files --
-- stored as documents in Postgres ... Every save creates a version with author +
-- timestamp." docs/api-contract.md "Config documents (three-layer)" and the Postgres
-- schema sketch: `config_docs` (kind, profile, layer_type, layer_ref) ·
-- `config_versions` (doc_id, vN, author, ts, body).
--
-- No resolve endpoint, no expiry semantics here -- that's #266, the second slice.
--
-- Idempotent by construction (IF NOT EXISTS / OR REPLACE), matching every prior
-- migration in this directory.

-- config_docs ----------------------------------------------------------------
-- One row per (kind, profile, layer) document identity; the mutable "latest version
-- pointer" lives on this row (current_version) while every version's actual body is
-- append-only in config_versions below -- a save never rewrites an existing version
-- row, it inserts vN+1 and repoints current_version.
--
-- layer_type/layer_ref together encode "global" | "site:{id}" | "target:{id}"
-- (docs/api-contract.md `/config-docs` filter: "layer (global|site:{id}|target:{id})")
-- without a polymorphic FK: layer_ref is NULL for the global layer, and otherwise
-- points at sites.id or targets.id depending on layer_type. There is deliberately no DB
-- FK to sites/targets (a polymorphic reference can't be a single FK without a
-- discriminated-union feature Postgres doesn't have); the API layer validates
-- layer_ref against the right table before insert.
--
-- profile is a free-text identifier (the compliance-content profile name/id) rather
-- than an FK -- profiles/benchmarks land later in #13 (see docs/api-contract.md
-- "Profiles & benchmarks"), so this mirrors depot_artifacts.metadata's "don't invent
-- structure ahead of the real shape" reasoning (ADR-0002).
--
-- UNIQUE(kind, profile, layer_type, layer_ref) is the one-document-per-slot identity:
-- there is exactly one "attestation doc for profile X at target Y", never two competing
-- rows for the same slot. Postgres treats NULLs as distinct for UNIQUE, which is
-- exactly what layer_type='global' (layer_ref NULL) needs -- there is still only ever
-- one global row per (kind, profile) because layer_type collapses the NULL, but a
-- second index below enforces that explicitly since the composite UNIQUE alone would
-- allow multiple NULL layer_ref rows under layer_type='global'.
CREATE TABLE IF NOT EXISTS config_docs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    kind TEXT NOT NULL,
    profile TEXT NOT NULL,
    layer_type TEXT NOT NULL,
    layer_ref UUID NULL,
    current_version INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT config_docs_kind_check CHECK (kind IN ('input', 'attestation', 'remediation-input')),
    CONSTRAINT config_docs_layer_type_check CHECK (layer_type IN ('global', 'site', 'target')),
    CONSTRAINT config_docs_layer_ref_check CHECK (
        (layer_type = 'global' AND layer_ref IS NULL) OR
        (layer_type IN ('site', 'target') AND layer_ref IS NOT NULL)
    ),
    CONSTRAINT config_docs_current_version_check CHECK (current_version >= 0)
);

-- layer_ref NULL under layer_type='global' still needs "one row per (kind, profile)"
-- enforced -- a plain UNIQUE(kind, profile, layer_type, layer_ref) would not catch a
-- second global row because Postgres UNIQUE treats NULL <> NULL. This partial unique
-- index closes that gap for the global layer specifically; the non-global slots are
-- covered by the composite UNIQUE below since layer_ref is NOT NULL there.
CREATE UNIQUE INDEX IF NOT EXISTS idx_config_docs_global_identity
    ON config_docs (kind, profile)
    WHERE layer_type = 'global';

CREATE UNIQUE INDEX IF NOT EXISTS idx_config_docs_scoped_identity
    ON config_docs (kind, profile, layer_type, layer_ref)
    WHERE layer_type <> 'global';

CREATE INDEX IF NOT EXISTS idx_config_docs_profile ON config_docs (profile);
CREATE INDEX IF NOT EXISTS idx_config_docs_layer ON config_docs (layer_type, layer_ref);

CREATE OR REPLACE TRIGGER trg_config_docs_updated_at
    BEFORE UPDATE ON config_docs
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- config_versions --------------------------------------------------------------
-- Append-only version history (docs/domain-model.md: "Every save creates a version
-- with author + timestamp -- 'who changed the attestation that waived this finding' is
-- an auditor question the tool must answer"). A save INSERTs the next version, never
-- UPDATEs an existing one -- there is deliberately no UPDATE/DELETE path exposed above
-- this table; PostgreSQL enforces nothing at the DDL level against mutation because the
-- append-only discipline is a repository-layer contract (ConfigDocRepository never
-- issues UPDATE/DELETE against this table), the same convention audit_log's
-- append-only comment (0006) documents for that table.
--
-- body_yaml carries the raw YAML text byte-for-byte as submitted (docs/api-contract.md
-- AC: "valid YAML round-trips byte-stable") -- validated for well-formedness at the API
-- layer before insert, not reparsed/reformatted here.
CREATE TABLE IF NOT EXISTS config_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doc_id UUID NOT NULL REFERENCES config_docs (id) ON DELETE CASCADE,
    version INTEGER NOT NULL,
    author TEXT NOT NULL,
    body_yaml TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT config_versions_version_check CHECK (version >= 1),
    CONSTRAINT config_versions_doc_id_version_key UNIQUE (doc_id, version)
);

CREATE INDEX IF NOT EXISTS idx_config_versions_doc_id ON config_versions (doc_id, version DESC);
