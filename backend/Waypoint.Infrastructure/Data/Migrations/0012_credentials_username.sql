-- Issue #262 (epic #13, M2): a dedicated username column, split out from the
-- human-facing `name` label that DiscoverJobHandler had been overloading as the
-- vSphere SSO login (issue #21 / PR #260's documented stopgap). `name` stays the
-- display label ('Prod vCenter admin'); `username` is the protocol-level login
-- ('administrator@example.internal') a job handler sends to the target.
--
-- Nullable and unconstrained by credential_type: a `token` credential (e.g. the
-- depot token) has no username concept, and existing rows created before this
-- migration have none either -- backfilling from `name` would re-bake the exact
-- conflation this issue exists to undo. Handlers that need a username for a
-- connection-type credential (vCenter/NSX/SSH) validate its presence themselves
-- (see DiscoverJobHandler), the same split CredentialsController already uses for
-- kind-specific validation (sudo_enabled requires ssh, etc.) rather than a DB CHECK.
--
-- Idempotent by construction (IF NOT EXISTS), matching every prior migration in
-- this directory.
ALTER TABLE credentials
    ADD COLUMN IF NOT EXISTS username TEXT NULL;
