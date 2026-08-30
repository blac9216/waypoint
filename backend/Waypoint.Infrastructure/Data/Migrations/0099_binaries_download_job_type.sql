-- Issue #1479 (epic #1181, split from #795's design record): reserves the
-- 'binaries-download' run/job type this M1 slice's new catalog-selection-to-enqueue
-- endpoint (POST /api/v1/downloads/binaries) uses -- the scan-style run -> per-item-job
-- fanout model (2026-08-28 grill decision Q18) for the connected VCFDT
-- `binaries download --id ...` path, distinct from the legacy URL-template 'download'
-- job type (#1040 removes that path entirely; out of scope here).
--
-- This slice only creates queued jobs -- it does NOT add 'binaries-download' to
-- Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed (the download-runner's actual
-- claim allowlist). That is deliberate, not an oversight, and it is the inverse of
-- issue #619's documented failure mode: #619 was a handler that *was* registered
-- (ManagedToolInstallJobHandler for 'tool-install') while the allowlist was never
-- updated to include it, so those jobs queued successfully and then sat queued
-- forever because no runner ever claimed them. Here no 'binaries-download' handler
-- exists yet, so allowlisting it now -- before #1482 registers one -- would instead
-- fail CI immediately:
-- EveryRegisteredJobHandlerIsClaimableTests.DownloadRunnerAllowlist_NamesOnlyJobTypesWithARegisteredHandler
-- asserts every allowlisted type has a registered handler. The job-handler sibling
-- (#1482) registers Waypoint.Core.Jobs.IJobHandler for 'binaries-download' and adds it
-- to DownloadRunnerJobTypes.Allowed in that same change, so the type only becomes
-- claimable the instant a handler exists to claim it.
--
-- Waypoint.Core.Jobs.JobCapabilities.Download DOES gain 'binaries-download' in this
-- change -- that set is the closed reserved-capability list (ADR-0013 Sec 2), not the
-- narrower per-host claim allowlist, and already carries several "later" types with no
-- handler yet (bundle-export, bundle-import, content-library-sync, update) per its own
-- doc comment.
ALTER TABLE jobs
    DROP CONSTRAINT IF EXISTS jobs_job_type_check;

ALTER TABLE jobs
    ADD CONSTRAINT jobs_job_type_check
    CHECK (job_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'content-check', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download'
    ));

ALTER TABLE runs
    DROP CONSTRAINT IF EXISTS runs_run_type_check;

ALTER TABLE runs
    ADD CONSTRAINT runs_run_type_check
    CHECK (run_type IN (
        'scan', 'remediate', 'discover', 'download', 'catalog-index',
        'bundle-export', 'bundle-import', 'content-library-sync',
        'content-pull', 'content-import', 'update', 'credential-test',
        'tool-install', 'purge', 'depot-enrollment', 'catalog-pull', 'binaries-download'
    ));
