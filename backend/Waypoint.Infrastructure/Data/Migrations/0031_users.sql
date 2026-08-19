-- Issue #512 (epic #14, split from #32): docs/api-contract.md `/users` -- Admin
-- GET/POST/PUT surface over a local `users` table keyed by `oidc_sub`, the OIDC
-- provider's stable subject claim (Keycloak `sub`; ADR-0004 -- app is a plain OIDC
-- relying party, never a Keycloak admin-API caller). One row per principal who has
-- ever authenticated, upserted at request time by UserDirectoryService off the
-- claims OidcClaimsMappingOptionsSetup (issue #29/#527) already resolves.
--
-- Design interpretation (stated in the #512 PR body and a comment on #512): per
-- ADR-0004, role is IdP-derived -- Keycloak group membership is authoritative, and
-- this backend never calls the Keycloak admin API to write it back. So `role` here is
-- a **mirror** of the token's current role claim, refreshed on every upsert (i.e.
-- "next token evaluation" per #512's acceptance criteria), NOT an independently
-- editable field. `PUT /users/{id}` therefore only ever writes `site_scope` (an
-- app-local field with no IdP equivalent); a request body that also sets `role` is
-- accepted but ignored for the mirror column -- role changes happen in the IdP.
-- `auth_method` records which scheme authenticated the request (`oidc` or `local` --
-- the ADR-0004 rollout dev flag, LocalAuthOptions), and `last_seen_at` is throttled
-- (UserDirectoryService only issues the UPDATE when the existing value is stale by
-- more than an interval) so a busy caller's every single request does not turn into a
-- write.
--
-- No runner grant is added here, mirroring the deliberate omission migration 0030's
-- header comment documents for `schedules`: neither waypoint_compliance_runner nor
-- waypoint_download_runner ever authenticates a caller or serves `/users` -- that is
-- entirely a Waypoint.Api (control-plane) concern, so this table gets NO grant to
-- either runner role (contrast migration 0025, which grants exactly the tables each
-- runner's claim/execute path actually reads/writes).
CREATE TABLE IF NOT EXISTS users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    oidc_sub TEXT NOT NULL,
    username TEXT NOT NULL,
    role TEXT NOT NULL,
    site_scope JSONB NOT NULL DEFAULT '[]'::jsonb,
    auth_method TEXT NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT users_oidc_sub_key UNIQUE (oidc_sub),
    CONSTRAINT users_role_check CHECK (role IN ('Viewer', 'Cyber', 'Operator', 'Admin')),
    CONSTRAINT users_auth_method_check CHECK (auth_method IN ('oidc', 'local'))
);

CREATE INDEX IF NOT EXISTS idx_users_last_seen_at ON users (last_seen_at DESC);

CREATE OR REPLACE TRIGGER trg_users_updated_at
    BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();
