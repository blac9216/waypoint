-- Issue #733 (epic #726 Wave 2: "Persist inventory-item and component scope as an
-- immutable run selection"). ADR-0023 is the governing decision -- "Requested scope is
-- immutable and is either top-level `all` expansion or an explicit stable-component
-- set... For either mode, requested identities or boundaries that refresh cannot
-- validate become explicit coverage omissions" -- and this migration is the first
-- durable record of that freeze: what the operator ASKED for (requested_scope_json,
-- the raw `target_scope` request body) versus what actually ran
-- (resolved_component_ids, the exact stable Component.Id set
-- Waypoint.Infrastructure.Runs.ScopeResolutionService computed) plus every omission
-- and why (omissions_json), so run history can display requested versus resolved scope
-- (issue #733 AC) without re-deriving it from current, possibly-since-changed
-- component/catalog state.
--
-- Scope discipline (issue #733's own "NOT this slice" list, mirrored from #732's
-- migration 0054 header): this table does NOT replace the shipped target-granular
-- `runs.scope` column or `jobs` fan-out -- those stay exactly as #585/#586 left them.
-- Component-granular job/attempt fan-out is #735-#737 (ADR-0024); the frontend
-- Start-a-Scan wizard wiring and `/runs/plan-preview` discovery-refresh integration are
-- explicitly deferred remainders of #733 itself. This migration only makes the
-- requested/resolved scope decision itself durable and queryable, one row per scan run,
-- so that later work has an immutable snapshot to build the full plan/job layer on top
-- of instead of re-deciding scope resolution from scratch.
--
-- One row per run (issue #733 AC "persistence of requested scope + resolved selection
-- snapshot ... for history/audit"): run_id is UNIQUE, not just indexed, and ON DELETE
-- CASCADE -- like `job_credential_bindings` (migration 0044) and unlike
-- catalog/content history tables, this snapshot has no independent meaning once its
-- owning run is gone (`runs` rows are themselves retained forever per migration 0042's
-- purge design; nothing in this codebase hard-deletes a `runs` row in practice, so the
-- CASCADE is a safety property, not an expected code path).
--
-- resolved_component_ids is a UUID[] rather than a join table: this is a small,
-- write-once, read-whole snapshot array (matches the "digest-addressed" immutability
-- style migrations 0052/0055 already use for other frozen sets), not a
-- growing/queried-by-member relation -- there is no code path that needs
-- "which runs included component X" as an indexed reverse lookup in this slice, and
-- adding one is a natural follow-up once #735-#737's component-job layer actually
-- reads this table.
--
-- No new runner grants: this slice's write path is API-side only, exactly like
-- migration 0054's components/component_observations tables -- ScopeResolutionService
-- runs inside RunCreationService's existing control-plane request path, never inside a
-- runner process. The compliance-runner does not read this table in this slice either
-- (it still executes against the shipped `runs.scope`/`jobs` target-granular fan-out);
-- a runner grant is deferred to whichever of #735-#737 first makes a runner path
-- consume the resolved snapshot.
CREATE TABLE IF NOT EXISTS run_scope_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL UNIQUE REFERENCES runs (id) ON DELETE CASCADE,
    requested_mode TEXT NOT NULL,
    requested_scope_json JSONB NOT NULL,
    resolved_component_ids UUID[] NOT NULL DEFAULT '{}',
    omissions_json JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT run_scope_snapshots_requested_mode_check CHECK (requested_mode IN ('all', 'explicit'))
);

CREATE INDEX IF NOT EXISTS idx_run_scope_snapshots_run_id ON run_scope_snapshots (run_id);
