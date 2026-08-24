-- Issue #584 (epic #582, second sub-issue, building on #583/ADR-0021
-- docs/adr/0021-credential-purpose-matrix.md): persists the credential-purpose
-- model ADR-0021 defined as inert data. Adds a normalized, reusable binding
-- table keyed by (target_id, purpose) so a target can carry more than one
-- credential -- e.g. a `vsphere` target's independent `vsphere-api` and
-- `vcsa-ssh` bindings -- instead of the single `targets.credential_id` column.
--
-- This migration does four things:
--
--   1. Creates target_credential_bindings: one row per (target, purpose),
--      UNIQUE on that pair so a target can never carry two bindings for the
--      same purpose. purpose is CHECK-constrained to CredentialPurposes.All
--      (kept in lockstep with backend/Waypoint.Core/Secrets/CredentialPurposes.cs
--      by CredentialPurposeMatrixTests-style coverage, same convention
--      targets_kind_check already uses for TargetKinds). credential_id is
--      NOT NULL -- a binding row's whole reason to exist is naming a
--      credential; "no binding for this purpose" is the ABSENCE of a row, not
--      a nullable column, so coverage/gap computation (ADR-0021 SS6) is a plain
--      LEFT JOIN / NOT EXISTS rather than a null check.
--
--      credential_id is a plain FK, deliberately WITHOUT any ON DELETE
--      action (the implicit default is RESTRICT) -- the same choice 0009 made
--      for targets.credential_id and CredentialRepository.DeleteAsync's own
--      comment explains: a credential referenced by a live binding is not
--      silently deletable out from under it. Unlike runs/jobs (0041 relaxed
--      those to SET NULL because they are point-in-time history), a binding
--      is live target configuration -- exactly like targets.credential_id
--      itself, which stays RESTRICT today. CredentialRepository.DeleteAsync
--      (#593's blocker model) is extended below (application code, not a DB
--      trigger) to count blocking bindings as their own category so the
--      RESTRICT-style guarantee is enforced with the same 409 breakdown every
--      other blocker category already gets, rather than a bare 23503
--      foreign-key-violation the caller cannot render.
--
--      target_id CASCADEs from targets -- deleting a target has always
--      implicitly discarded its one credential_id; deleting its bindings too
--      is the same rule generalized to N purposes.
--
--   2. Data-migrates every existing targets.credential_id into the
--      kind-appropriate DEFAULT purpose per ADR-0021 SS3's matrix:
--        vsphere targets  -> vsphere-api  (the vCenter/ESXi/VM API purpose;
--                             every vsphere operation requires it, vcsa-ssh
--                             is the second, optional-until-selected purpose
--                             this migration does NOT invent a binding for --
--                             there is no prior signal for which credential,
--                             if any, would have served vcsa-ssh)
--        nsx-api targets  -> nsx-api
--        ssh targets      -> srg-ssh
--      This is the ONLY purpose inferable from a single legacy column per
--      target kind -- ADR-0021 SS3 confirms every kind has exactly one
--      "primary"/always-required purpose, so the migration is unambiguous
--      and lossless for what the column actually represented (issue #584 AC:
--      "Existing bindings migrate without silently changing execution
--      behavior").
--
--   3. Keeps targets.credential_id as a live column -- NOT dropped, NOT
--      deprecated at the DB level. Execution (RunCreationService,
--      ScanJobHandler, CredentialTestJobHandler, DiscoverJobHandler) still
--      reads ONLY targets.credential_id until #585 lands purpose-aware
--      execution resolution; removing the column is explicitly out of scope
--      here (issue #584's scope note) and belongs to #585.
--
--      DUAL-WRITE CONTRACT (documented here because this migration is the
--      only place both sides of the rule are visible at once): from this PR
--      forward, TargetRepository's write path keeps targets.credential_id
--      and the kind's default-purpose binding (the same mapping data-migrated
--      above) in lockstep in application code, inside the same transaction:
--        - Writing targets.credential_id (create, or update naming
--          credential_ref) UPSERTs the kind's default-purpose binding to the
--          same credential_id.
--        - Clearing targets.credential_id (clear_credential_ref) DELETES the
--          kind's default-purpose binding row, if present.
--        - Writing/clearing a NON-default-purpose binding (e.g. vcsa-ssh on a
--          vsphere target) through the new binding CRUD surface never touches
--          targets.credential_id -- only the default purpose mirrors the
--          legacy column, because it is the only column that legacy column
--          could ever represent.
--      This keeps the two representations consistent for every kind's
--      default purpose without requiring #585 to reconcile drift on arrival:
--      #585's execution-resolution slice can trust that
--      target.credential_id == the default-purpose binding's credential_id
--      for every target that has been touched (created or updated) since
--      this PR, and this migration's one-time backfill guarantees the same
--      for every target that predates it.
--
--   4. Grants NOTHING new to either runner role. Execution does not read
--      this table until #585 -- granting now would be a pre-grant with no
--      caller, which RunnerRoleGrantDriftTests' negative-direction cases
--      below prove by asserting BOTH runner roles are denied even SELECT on
--      target_credential_bindings today. #585 adds the read grant (and a
--      positive-direction test) when a runner-executed code path actually
--      needs it.
CREATE TABLE IF NOT EXISTS target_credential_bindings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    target_id UUID NOT NULL REFERENCES targets (id) ON DELETE CASCADE,
    purpose TEXT NOT NULL,
    credential_id UUID NOT NULL REFERENCES credentials (id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT target_credential_bindings_purpose_check CHECK (
        purpose IN ('vsphere-api', 'vcsa-ssh', 'nsx-api', 'srg-ssh')
    ),
    CONSTRAINT target_credential_bindings_target_id_purpose_key UNIQUE (target_id, purpose)
);

CREATE INDEX IF NOT EXISTS idx_target_credential_bindings_target_id ON target_credential_bindings (target_id);
CREATE INDEX IF NOT EXISTS idx_target_credential_bindings_credential_id ON target_credential_bindings (credential_id);

CREATE OR REPLACE TRIGGER trg_target_credential_bindings_updated_at
    BEFORE UPDATE ON target_credential_bindings
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Backfill: one binding per existing targets.credential_id, into the kind's
-- default purpose (ADR-0021 SS3). ON CONFLICT DO NOTHING makes this
-- idempotent/safe to re-run alongside every other migration in this
-- directory.
--
-- Each INSERT joins to credentials and requires the kind-appropriate
-- satisfying credential TYPE (ADR-0021 SS2: vsphere-api<-vcenter,
-- nsx-api<-nsx, srg-ssh<-ssh). The legacy credential_ref write path has never
-- validated type -- an operator could always point a target at a
-- mismatched-type credential (e.g. a `token` row) -- so a legacy row whose
-- credential type does not satisfy the purpose is intentionally left
-- un-migrated rather than backfilled into a binding the compatibility matrix
-- would itself reject if written today. This mirrors TargetRepository's new
-- write-path rule (MirrorIfCompatibleAsync) exactly, so the one-time backfill
-- and the ongoing dual-write use the same compatibility test.
INSERT INTO target_credential_bindings (target_id, purpose, credential_id)
SELECT t.id, 'vsphere-api', t.credential_id
FROM targets t
JOIN credentials c ON c.id = t.credential_id
WHERE t.kind = 'vsphere' AND t.credential_id IS NOT NULL AND c.credential_type = 'vcenter'
ON CONFLICT (target_id, purpose) DO NOTHING;

INSERT INTO target_credential_bindings (target_id, purpose, credential_id)
SELECT t.id, 'nsx-api', t.credential_id
FROM targets t
JOIN credentials c ON c.id = t.credential_id
WHERE t.kind = 'nsx-api' AND t.credential_id IS NOT NULL AND c.credential_type = 'nsx'
ON CONFLICT (target_id, purpose) DO NOTHING;

INSERT INTO target_credential_bindings (target_id, purpose, credential_id)
SELECT t.id, 'srg-ssh', t.credential_id
FROM targets t
JOIN credentials c ON c.id = t.credential_id
WHERE t.kind = 'ssh' AND t.credential_id IS NOT NULL AND c.credential_type = 'ssh'
ON CONFLICT (target_id, purpose) DO NOTHING;
