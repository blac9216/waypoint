-- Issue #19 (epic #13, M2): sites and targets, the domain foundation the
-- M1 schema forward-referenced but did not create (see 0001's comments on
-- `runs.site_id` and `jobs.target_id` — both stayed plain UUID columns with
-- no FK specifically so this migration could add the referenced tables
-- without an ALTER on either of them). docs/domain-model.md "Site" /
-- "Target"; docs/api-contract.md "Sites, targets, inventory" and its
-- Postgres schema sketch: `sites` · `targets` (site_id, kind, connection
-- jsonb, credential_id, discovery_status).
--
-- Idempotent by construction, matching every prior migration in this
-- directory (IF NOT EXISTS / OR REPLACE), so re-running this file against an
-- already-migrated database is a no-op.

-- sites --------------------------------------------------------------------
-- The top-level grouping ("an enclave's VMware estate" — domain-model.md).
-- stigman_override carries the per-site STIG Manager connection override
-- (docs/api-contract.md "STIG Manager": "Global default + per-site
-- override") as JSONB rather than a separate table: it is a single optional
-- document per site, not a collection, and its shape is planning-grade
-- pending the #13 STIG Manager slice — the same "don't invent structure
-- ahead of the real shape" reasoning 0007's comment gives for
-- depot_artifacts.metadata (ADR-0002).
CREATE TABLE IF NOT EXISTS sites (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    description TEXT NULL,
    stigman_override JSONB NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT sites_name_key UNIQUE (name)
);

CREATE OR REPLACE TRIGGER trg_sites_updated_at
    BEFORE UPDATE ON sites
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- targets --------------------------------------------------------------------
-- A scannable/manageable endpoint within a site (domain-model.md "Target").
-- Multiple targets of the same kind are allowed under one site (e.g. two
-- vCenters) — there is deliberately no UNIQUE(site_id, kind).
--
-- connection is JSONB (hostname/address + any kind-specific fields, e.g.
-- vSphere's optional datacenter path) for the same reason sites'
-- stigman_override is: the exact per-kind shape is still planning-grade
-- (docs/api-contract.md: "field lists name the load-bearing data, not every
-- column"), and ADR-0002 keeps vendor/connection shapes JSONB rather than
-- forcing a premature column-per-field schema. It NEVER carries secret
-- material — connection secrets are referenced via credential_id only
-- (enforced at the API layer: TargetCreateRequest/TargetUpdateRequest reject
-- any payload naming a secret-shaped key, see TargetsController).
--
-- credential_id is a plain FK to credentials (RESTRICT, the same choice
-- CredentialRepository.DeleteAsync's comment explains for jobs/runs: a
-- referenced credential is not deletable, so a target's credential_ref never
-- silently dangles).
CREATE TABLE IF NOT EXISTS targets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    site_id UUID NOT NULL REFERENCES sites (id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    connection JSONB NOT NULL DEFAULT '{}'::jsonb,
    credential_id UUID NULL REFERENCES credentials (id),
    discovery_status TEXT NOT NULL DEFAULT 'never_discovered',
    last_refreshed TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT targets_kind_check CHECK (kind IN ('vsphere', 'nsx-api', 'ssh')),
    CONSTRAINT targets_discovery_status_check CHECK (
        discovery_status IN ('never_discovered', 'discovering', 'discovered', 'failed')
    ),
    CONSTRAINT targets_site_id_name_key UNIQUE (site_id, name)
);

CREATE INDEX IF NOT EXISTS idx_targets_site_id ON targets (site_id);

CREATE OR REPLACE TRIGGER trg_targets_updated_at
    BEFORE UPDATE ON targets
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
