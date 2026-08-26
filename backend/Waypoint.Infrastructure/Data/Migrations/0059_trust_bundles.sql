-- Issue #753 (epic #726, ADR-0025 "Connection-scoped trust"): the first slice of
-- managed CA trust -- the durable trust store and scoped policy-binding model. This
-- migration adds two tables:
--
--   trust_bundles         -- one Admin-uploaded CA certificate/chain, stored as public
--                             material (never encrypted, never treated as a secret --
--                             docs/security.md "Managed CA trust is public material,
--                             not a secret"). Immutable once created: a "replacement"
--                             upload creates a NEW row and supersedes the old one
--                             (status -> 'superseded'), it never mutates an existing
--                             row's PEM/metadata columns in place. Subject/issuer/
--                             fingerprint/validity metadata is parsed and stored at
--                             upload time so list/detail reads never re-parse PEM.
--   trust_policies         -- one scoped binding of a target/service connection to
--                             either a trust_bundle (mode='bundle') or an explicit,
--                             reasoned, audited skip-verification decision
--                             (mode='bypass', bypass_reason required). Never a
--                             process-global default (ADR-0025, docs/security.md) --
--                             every row is scoped to exactly one (scope_type,
--                             scope_id) pair, enforced by the partial unique indexes
--                             below mirroring migration 0052's
--                             idx_benchmark_component_mappings_current_unique
--                             "one current row" idiom. Superseding a policy (a later
--                             PUT for the same scope) flips the old row to
--                             'superseded' rather than mutating it in place, so a
--                             PlannedComponentItem created under #735-#737 can freeze
--                             an exact trust_policies.id/version reference that never
--                             silently changes underneath an in-flight or historical
--                             run (docs/security.md "Planning freezes the policy
--                             identity/version, not live state").
--
-- Scope discipline (this PR's first slice of #753, matching #732/#733/#734/#731's own
-- migration-header "NOT this slice" convention): this migration does NOT wire any
-- runtime client (PowerCLI/NSX/SSH-adjacent/STIG Manager/content sync), does NOT
-- materialize trust for a runner session, does NOT snapshot a trust-policy reference
-- into a PlannedComponentItem (that lands with #735-#737's plan/job tables), and does
-- NOT add a frontend configuration screen. No new runner grants: every write in this
-- slice is Admin-only API-side (upload/replace/delete/policy CRUD); a runner grant is
-- deferred to whichever future issue first makes a runner path read these tables to
-- materialize a per-client verification context.
--
-- trust_bundles -----------------------------------------------------------------------
-- `pem_chain` is the exact validated PEM text as uploaded (one or more CERTIFICATE
-- blocks) -- validation (format, size, chain, duplicate-fingerprint, expiry,
-- private-key rejection) happens in application code (TrustBundleValidator) before
-- this INSERT; the migration only enforces the shape invariants that are cheap and
-- meaningful at the database boundary. `fingerprint_sha256` is the leaf certificate's
-- SHA-256 fingerprint (hex, lowercase, no separators) and is the duplicate-detection
-- key: re-uploading byte-identical (or same-leaf-fingerprint) material is rejected as
-- a duplicate by application code before reaching here, but the unique index below is
-- the fail-closed backstop against a racing concurrent upload, scoped to only
-- non-superseded rows so a superseded bundle's fingerprint can be re-uploaded fresh
-- (e.g. re-adding a previously-replaced CA) without a phantom uniqueness conflict.
CREATE TABLE IF NOT EXISTS trust_bundles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    label TEXT NOT NULL,
    pem_chain TEXT NOT NULL,
    subject TEXT NOT NULL,
    issuer TEXT NOT NULL,
    fingerprint_sha256 TEXT NOT NULL,
    not_before TIMESTAMPTZ NOT NULL,
    not_after TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL DEFAULT 'active',
    superseded_by_id UUID NULL REFERENCES trust_bundles (id) ON DELETE RESTRICT,
    superseded_at TIMESTAMPTZ NULL,
    uploaded_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT trust_bundles_status_check CHECK (status IN ('active', 'superseded')),
    CONSTRAINT trust_bundles_fingerprint_format_check CHECK (fingerprint_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT trust_bundles_superseded_fields_check CHECK (
        (status = 'superseded' AND superseded_at IS NOT NULL) OR (status = 'active' AND superseded_by_id IS NULL AND superseded_at IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_trust_bundles_status ON trust_bundles (status);

-- Fail-closed duplicate guard scoped to live (non-superseded) rows only -- see comment
-- above; this is the same partial-unique "at most one current row" idiom migrations
-- 0052/0055 already use.
CREATE UNIQUE INDEX IF NOT EXISTS idx_trust_bundles_active_fingerprint_unique
    ON trust_bundles (fingerprint_sha256)
    WHERE status = 'active';

-- trust_policies ------------------------------------------------------------------------
-- `scope_type`/`scope_id` name the target or service connection this policy binds --
-- deliberately a loosely-typed pair (not an FK) at this slice's stage because the exact
-- closed set of scopable connections (target, per-component service, STIG Manager
-- connection, content-sync source) is still being defined across #735-#737/#785; the
-- CHECK below fixes the vocabulary to what THIS slice's controller actually accepts
-- (`target`) plus the STIG Manager global/site connections this repo already ships
-- (`stigman-global`, `stigman-site`), and any future scope kind is a additive CHECK
-- widening, never a column-shape change. `mode = 'bundle'` requires trust_bundle_id and
-- forbids bypass_reason; `mode = 'bypass'` requires a non-empty bypass_reason and
-- forbids trust_bundle_id -- ADR-0025 "An Admin may instead authorize
-- certificate-verification bypass ... The decision is explicit, reasoned, versioned,
-- and audited," made concrete as a CHECK so an apparently-successful application-layer
-- bug can never persist an unreasoned bypass. Only one row per scope may be
-- `status = 'current'` (partial unique index below); replacing a scope's policy flips
-- the old row to 'superseded' in the same transaction that inserts the new current row
-- (TrustPolicyRepository.SetPolicyAsync), so a frozen `PlannedComponentItem` reference
-- to an old row's id always resolves to the exact historical decision, never a
-- currently-live one.
CREATE TABLE IF NOT EXISTS trust_policies (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_type TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    mode TEXT NOT NULL,
    trust_bundle_id UUID NULL REFERENCES trust_bundles (id) ON DELETE RESTRICT,
    bypass_reason TEXT NULL,
    status TEXT NOT NULL DEFAULT 'current',
    superseded_at TIMESTAMPTZ NULL,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT trust_policies_scope_type_check CHECK (scope_type IN ('target', 'stigman-global', 'stigman-site')),
    CONSTRAINT trust_policies_mode_check CHECK (mode IN ('bundle', 'bypass')),
    CONSTRAINT trust_policies_status_check CHECK (status IN ('current', 'superseded')),
    CONSTRAINT trust_policies_mode_fields_check CHECK (
        (mode = 'bundle' AND trust_bundle_id IS NOT NULL AND bypass_reason IS NULL)
        OR (mode = 'bypass' AND trust_bundle_id IS NULL AND btrim(bypass_reason) <> '')
    ),
    CONSTRAINT trust_policies_superseded_fields_check CHECK (
        (status = 'superseded' AND superseded_at IS NOT NULL) OR (status = 'current' AND superseded_at IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_trust_policies_scope ON trust_policies (scope_type, scope_id);
CREATE INDEX IF NOT EXISTS idx_trust_policies_trust_bundle_id ON trust_policies (trust_bundle_id);

-- At most one CURRENT policy per scope (ADR-0025 "never inferred or inherited ...
-- never becomes a default" made concrete: exactly one live decision per named scope,
-- same partial-unique idiom as trust_bundles above and migrations 0052/0055).
CREATE UNIQUE INDEX IF NOT EXISTS idx_trust_policies_current_unique_per_scope
    ON trust_policies (scope_type, scope_id)
    WHERE status = 'current';

-- Delete-safety (issue #753 AC "delete-safety (delete blocked while referenced --
-- RESTRICT)"): a trust_bundle referenced by ANY trust_policy row (current or
-- superseded -- historical policy references must remain resolvable) cannot be
-- deleted while that reference exists; the FK above already enforces RESTRICT at the
-- database boundary, so TrustBundleRepository.DeleteAsync's own pre-check exists only
-- to return a clean, actionable 409 rather than surfacing a raw 23503 to the API
-- caller.
