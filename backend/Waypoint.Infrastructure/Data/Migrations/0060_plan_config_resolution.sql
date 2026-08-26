-- Issue #735 (epic #726 Wave 2: "Resolve and snapshot component inputs, attestations,
-- and remediation inputs"). ADR-0024 "Control-granular settings and snapshots" is the
-- governing decision: "Input, Attestation, and future Remediation are three
-- independently versioned setting kinds keyed by stable baseline control identity...
-- Planning snapshots every effective setting needed by each control, including the
-- source layer/version, value or secret reference/digest, attestation actor/provenance
-- and expiry, and an explicit missing/inapplicable state." This slice keys config
-- documents to the stable catalog execution-profile identity migration 0057 already
-- freezes onto each plan item, and freezes the resolved documents' ids/versions/
-- provenance into that same plan item.
--
-- Two additive changes, no parallel table:
--
-- 1. config_docs gains a nullable catalog_execution_profile_id FK alongside the
--    existing free-text `profile` column (migration 0013's "profiles/benchmarks land
--    later" placeholder). `profile` is untouched -- existing rows, the resolve
--    endpoint, and the runtime attest stage (ScanJobHandler.ExecuteAttestStageAsync,
--    #306) all keep working exactly as before against the free-text key; this slice's
--    planning-time resolution ADDITIONALLY matches on the stable catalog identity when
--    a doc has been keyed to one, which is what makes "connect config documents to the
--    exact profile/component execution item that consumes them" (issue #735 Summary)
--    possible without a coarse fixed AttestationProfile name. RESTRICT (not CASCADE):
--    a config-doc keyed to a catalog execution profile is authored content an operator
--    must consciously re-key, never something a catalog re-import should silently
--    orphan or delete.
--
-- 2. scan_plan_items gains the resolved-snapshot columns ADR-0024 requires: which
--    Input documents applied (id/version/layer/digest per declared input name -- issue
--    #831's declared_inputs_json this migration's items already carry), the resolved
--    Attestation (id/version/layer/applied/expired -- the planning-side replacement for
--    ScanJobHandler's fixed ScanOptions.AttestationProfile key), and a digest of both so
--    ScanPlanDigest's determinism contract (issue #734 AC-4) extends to the resolved
--    config layer. Both are JSONB, matching required_purposes_json/declared_inputs_json's
--    existing convention on this same table -- a scan_plan_item's config resolution has
--    no independent query shape of its own yet (no per-control catalog exists this
--    slice; see ADR-0024 "per-control is future work"). Nullable/defaulted so a legacy
--    plan item (recorded before this migration) round-trips as "no config resolution
--    recorded", not a constraint violation.
--
-- No runner grant: this slice's write path is API-side only
-- (RunCreationService/ScanPlannerService, same as migration 0057) and the
-- compliance-runner's ScanJobHandler still resolves attestations live at attest-stage
-- time against ScanOptions.AttestationProfile (this issue's own "Remainder" list:
-- "ScanJobHandler/psm1 runtime materialization... stays as-is this slice"). A runner
-- grant is deferred to whichever future slice makes the runner path read these columns.

ALTER TABLE config_docs
    ADD COLUMN IF NOT EXISTS catalog_execution_profile_id UUID NULL
        REFERENCES catalog_execution_profiles (id) ON DELETE RESTRICT;

CREATE INDEX IF NOT EXISTS idx_config_docs_catalog_execution_profile_id
    ON config_docs (catalog_execution_profile_id)
    WHERE catalog_execution_profile_id IS NOT NULL;

-- A config-doc keyed to a stable catalog identity is still scoped uniquely per
-- (kind, catalog_execution_profile_id, layer) -- the same one-document-per-slot
-- invariant migration 0013 already enforces for the free-text profile key, mirrored
-- onto the new key so a profile can be keyed BOTH ways during the authoring-UI
-- transition (deferred remainder of #735) without two catalog-keyed docs ever
-- competing for the same slot.
CREATE UNIQUE INDEX IF NOT EXISTS idx_config_docs_catalog_global_identity
    ON config_docs (kind, catalog_execution_profile_id)
    WHERE layer_type = 'global' AND catalog_execution_profile_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_config_docs_catalog_scoped_identity
    ON config_docs (kind, catalog_execution_profile_id, layer_type, layer_ref)
    WHERE layer_type <> 'global' AND catalog_execution_profile_id IS NOT NULL;

ALTER TABLE scan_plan_items
    ADD COLUMN IF NOT EXISTS input_resolutions_json JSONB NOT NULL DEFAULT '[]',
    ADD COLUMN IF NOT EXISTS attestation_resolution_json JSONB NULL,
    ADD COLUMN IF NOT EXISTS config_resolution_digest TEXT NULL;
