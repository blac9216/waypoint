-- Issue #691 (epic #667): the assisted VCF 9.1 Software Depot enrollment state
-- machine ("tool unavailable" -> "depot ID unavailable" -> "awaiting portal
-- registration" -> "activation code stored" -> "validated"/"auth failing").
--
-- depot_enrollment is a SINGLETON, mirroring appliance_state (migration 0001): one
-- Waypoint appliance has exactly one managed vcf-download-tool identity, so there is
-- exactly one enrollment record, not a per-credential or per-operator one.
--
-- Deliberately split from the encrypted Activation Code credential (issue #690's
-- 'depot-activation-code' row in `credentials`/`credential_secrets`):
--   - depot_id and paired_asset_id are NOT secret. The Software Depot ID is
--     operator-facing information Waypoint displays/copies for the Broadcom portal
--     registration step (knowledge.broadcom.com/external/article/441033); the
--     Activation Code's embedded asset_id is compared against it for pairing
--     validation but is not the code itself. Neither belongs in the envelope-
--     encrypted credential_secrets table (ADR-0005's ciphertext-only contract), and
--     persisting them here means the API can read/display them directly without a
--     decrypt-for-one-call round trip through ICredentialSecretStore.
--   - state is this migration's own enrollment lifecycle, independent of
--     credentials.health (issue #690's valid/auth_failing/unknown vocabulary) --
--     'auth_failing' here specifically means the stored code was rejected by a
--     bounded noninteractive tool validation call, which is a narrower, enrollment-
--     specific signal than the credential's own generic health.
--   - depot_id_generated_at/paired_at/reset_at give the frontend and audit trail a
--     timeline without inferring it from credentials.rotated_at, which belongs to a
--     different table/row entirely and can be rotated independently (e.g. re-pasting
--     the exact same already-valid code).
CREATE TABLE IF NOT EXISTS depot_enrollment (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    state TEXT NOT NULL DEFAULT 'tool_unavailable',
    depot_id TEXT NULL,
    depot_id_generated_at TIMESTAMPTZ NULL,
    paired_asset_id TEXT NULL,
    paired_at TIMESTAMPTZ NULL,
    last_validation_failure TEXT NULL,
    reset_at TIMESTAMPTZ NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT depot_enrollment_singleton_check CHECK (id = 1),
    CONSTRAINT depot_enrollment_state_check CHECK (state IN (
        'tool_unavailable',
        'depot_id_unavailable',
        'awaiting_portal_registration',
        'activation_code_stored',
        'validated',
        'auth_failing'
    ))
);

INSERT INTO depot_enrollment (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE OR REPLACE TRIGGER trg_depot_enrollment_updated_at
    BEFORE UPDATE ON depot_enrollment
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- 'depot-enrollment' is a new Simple-shape job type (ADR-0008/0015 pattern, mirrors
-- 'tool-install' and 'credential-test'): the download-runner invokes the installed
-- vcf-download-tool noninteractively to (a) obtain the Software Depot ID and (b)
-- validate a stored Activation Code. Both operations run through the ordinary
-- jobs/job_events pipeline so they get the same lease/cancellation/audit machinery
-- every other job type has, rather than a bespoke synchronous code path.
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment'
    ));

-- Download-runner grant: the depot-enrollment handler reads/writes depot_enrollment
-- (its own dedicated table, not credentials) and resolves the stored Activation Code
-- credential the same way ManagedToolInstallJobHandler's depot-fetch path does
-- (issue #690's FindByTypeAsync/DecryptAsync grants from migration 0039/0025 already
-- cover that; no additional credentials grant is needed here).
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'waypoint_download_runner') THEN
        RAISE EXCEPTION 'role "waypoint_download_runner" does not exist -- run deploy/postgres/initdb/01-runner-roles.sh (fresh pgdata) or create it manually before applying this migration';
    END IF;
END
$$;

GRANT SELECT, UPDATE ON depot_enrollment TO waypoint_download_runner;
