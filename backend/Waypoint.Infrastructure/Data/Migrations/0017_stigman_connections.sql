-- Issue #310 (first slice of the #25 split): the global STIG Manager
-- connection. docs/api-contract.md "STIG Manager": "Global default + per-site
-- override; endpoint, oidc client, collection, reachability/token TTL (secret
-- write-only)"; docs/domain-model.md "STIG Manager connection": "Global
-- default connection, optional per-site override". The per-site override
-- already exists (migration 0009's sites.stigman_override JSONB) -- this
-- migration adds only the global side the Postgres schema sketch names:
-- `stigman_connections`.
--
-- Modeled as a singleton table (a single row, id fixed at 1) rather than a
-- one-row-per-something collection: there is exactly one global default, the
-- same "single optional document, not a collection" reasoning 0009's comment
-- gives for stigman_override, and the same shape appliance_state (0001) uses
-- for its own singleton row. GET returns "not configured" (no row) rather
-- than a row of nulls; PUT upserts.
--
-- No secret material here: client_secret rides the existing credential store
-- (ADR-0005/#249) via credential_id -- a token-type credential -- exactly as
-- module.benchmarks.ps1's Get-StigManagerConnection resolves ClientSecret via
-- Resolve-Secret(clientSecretRef) today. This table only carries the
-- non-secret connection shape: endpoint (apiBase), authority (OIDC issuer),
-- collection, client_id, scope.
CREATE TABLE IF NOT EXISTS stigman_connections (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    endpoint TEXT NOT NULL,
    authority TEXT NOT NULL,
    collection TEXT NOT NULL,
    client_id TEXT NOT NULL,
    scope TEXT NOT NULL DEFAULT 'openid stig-manager:stig:read',
    credential_id UUID NULL REFERENCES credentials (id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT stigman_connections_singleton_check CHECK (id = 1)
);

CREATE OR REPLACE TRIGGER trg_stigman_connections_updated_at
    BEFORE UPDATE ON stigman_connections
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
