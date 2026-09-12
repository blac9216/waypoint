# Schema migration scheme and ledger

Waypoint applies raw-SQL migrations from
`backend/Waypoint.Infrastructure/Data/Migrations/*.sql` through the hand-rolled
`NpgsqlSchemaMigrator`. The migrator records each applied file in a
`schema_migrations(version, applied_at)` table and applies, on every run, exactly the
set of embedded files **not yet recorded** (set difference). It orders files lexically
(ordinal) purely to make application deterministic; correctness never depends on the
order, only on the set difference.

## Filename scheme

- **New migrations** use a UTC timestamp prefix: `YYYYMMDDHHMMSS_<slug>.sql` (e.g.
  `20260912143000_add_widget_index.sql`). Generate the prefix at authoring time with a
  UTC clock (`date -u +%Y%m%d%H%M%S`). The timestamp makes independently-authored
  branches pick distinct prefixes without a shared counter, which removes the
  slot-collision failure mode that a hand-incremented sequence caused (issue #1845).
- **Existing `0001`–`0134`** keep their zero-padded numeric prefixes untouched. The two
  schemes coexist: a numeric prefix sorts lexically before any `2026…` timestamp prefix
  (`'0' < '2'` ordinally), so the mixed set orders old-then-new, and the set-difference
  application tolerates a lower-prefixed file being added and applied after a
  higher-prefixed one has already been recorded.

## Guard (replaces the old `ExpectedMigrationCount`)

`SchemaMigrationTests` no longer asserts a hand-maintained migration **count**. Instead:

- **Set equality** — after a fresh apply, the set of `.sql` files on disk equals the set
  of versions recorded in `schema_migrations`: nothing on disk is left unrecorded, and
  nothing recorded is missing on disk.
- **Duplicate-prefix detection** — two migration files sharing the same prefix (the
  substring before the first `_`) fail the guard, catching an accidental slot/timestamp
  collision.

When adding a migration you no longer bump any counter; the set-equality guard adapts
automatically and the duplicate-prefix guard protects against collisions.

## Migration ledger (historical, per-file notes)

The notes below were relocated verbatim from the `SchemaMigrationTests` doc-comment
where they were prose, not test logic (issue #1845, Step 1). Step 2 will relocate the
load-bearing notes into the ADR/rationale docs that cite them by number.

0042 adds run_purges + run_purge_tombstones -- the durable retryable purge lifecycle and append-only audit tombstone for the admin-only terminal-compliance-run purge, plus runs.purged_at, 'purge' in jobs_job_type_check/runs_run_type_check, relaxes schedules.last_run_id from RESTRICT to ON DELETE SET NULL, and the compliance-runner's run_purges progress-reporting grant, issue #594, 0043 adds target_credential_bindings -- the normalized purpose-specific credential binding table (ADR-0021), backfills existing targets.credential_id references into the kind-appropriate default-purpose binding, and documents the dual-write contract keeping targets.credential_id and the default-purpose binding consistent until #585 removes the legacy column -- no new runner grants, issue #584, 0044 adds job_credential_bindings -- the immutable per-job per-purpose credential snapshot ledger (ADR-0021 SS5) RunCreationService's scan fan-out populates, with the compliance-runner SELECT-only grant and the jobs.credential_id dual-write/fallback contract documented in the migration header, issue #585, 0045 re-keys run_secrets from one row per run to one row per (run, target, purpose) -- additive columns/indexes only, the unconditional per-terminal-completion DELETE stays run_id-scoped so it covers both shapes with no code change, plus job_credential_bindings.is_run_secret so a job's per-purpose snapshot can name an ad hoc (run_secrets-backed) source instead of a stored credential_id, issue #586, 0046 adds runs.history_deleted_at + run_history_deletion_tombstones -- generic operational-history deletion for TERMINAL runs, structurally separate from run_purges/run_purge_tombstones (a deliberate sibling table, not a shared one) and deferring to that domain purge for scan/remediate runs, entirely API-side with no new runner grants, issue #592, 0047 extends credentials_credential_type_check with 'depot-activation-code' and 'legacy-download-token' (issue #690's non-destructive split of the ambiguous 'depot-token' well-known type -- 'depot-token' itself is RETAINED, not dropped, so pre-existing rows stay valid and visibly legacy; no data rewritten, no new runner grants), issue #691, 0048 adds depot_enrollment -- the singleton (mirrors appliance_state) non-secret Software Depot ID + assisted-enrollment state-machine table, adds 'depot-enrollment' to jobs_job_type_check/runs_run_type_check for the noninteractive tool-invocation job, and grants waypoint_download_runner SELECT/UPDATE on the new table, issue #687, 0049 adds catalog_pull_state -- the singleton (mirrors depot_enrollment) connected-catalog-pull attempt/success tracking table, adds 'catalog-pull' to jobs_job_type_check/runs_run_type_check for the distinct connected vendor-catalog-pull job (separate from the local credential-free catalog-index re-index), and grants waypoint_download_runner SELECT/INSERT/UPDATE on the new table, issue #687, 0050 adds the normalized compliance catalog (issue #728, ADR-0022): catalog_source_revisions, catalog_products, catalog_product_versions, catalog_content_releases, catalog_components, catalog_report_groups, catalog_execution_profiles, catalog_credential_requirements, catalog_benchmark_references, and catalog_remediation_definitions -- the versioned identity tree and closed capability vocabulary for STIG/SRG execution profiles, all FKs ON DELETE RESTRICT so a plan-referenced historical revision cannot be deleted, no new runner grants (every row is catalog-authored, appliance-shipped data), issue #728, 0051 adds catalog_import_reports + catalog_import_report_entries (persisted SemanticImportReport headers/entries -- accepted/warning/rejected disposition per profile key) and catalog_declared_inputs (per-execution-profile declared InSpec inputs, closing issue #728's last queryable-fields AC) -- the issue #729 persistence slice; grants waypoint_compliance_runner SELECT/INSERT on the two report tables and SELECT/INSERT/UPDATE on every 0050 catalog identity-tree table plus catalog_declared_inputs, since ContentPullJobHandler's semantic-import pass now writes candidate promotions from the compliance-runner process, issue #729, 0052 adds 0052_xccdf_benchmark_revisions.sql: benchmark_revisions, benchmark_rules, and benchmark_component_mappings -- immutable digest-addressed DISA XCCDF/STIG benchmark revisions and rules plus the exact component-to-benchmark-revision mapping and its versioned audit history (one current row per component via a partial unique index, prior decisions superseded rather than overwritten), no new runner grants (Admin-only mapping writes are API-layer, deferred to issue #730's remainder PR), issue #730, 0053 fixes issue #832 (same defect class as PR #831's NULL-parent catalog_components race): catalog_execution_profiles' promotion write becomes an atomic INSERT ... ON CONFLICT (component_id, content_release_id) DO UPDATE against 0050's own catalog_execution_profiles_unique constraint (no new index needed -- both natural-key columns are NOT NULL, so the existing plain UNIQUE constraint already has no NULL-distinctness gap), replacing the prior check-then-insert; adds the missing UPDATE grant on catalog_execution_profiles to waypoint_compliance_runner (0051 only granted SELECT/INSERT, matching the check-then-insert code that existed then) so the new DO UPDATE branch does not ship green under the owner role and then 42501 live under the runner role, issue #832, 0054 adds components + component_observations (issue #732, epic #726 Wave
2, ADR-0023): stable compliance endpoint/component identity beneath a top-level
target, independent configured/discovered exact-version facts with an explicit
fact_conflict readiness signal, active/absent/retired lifecycle (rediscovery
reconnects, continuous absence is application-policy-timed retirement), and
append-only observation provenance -- no new runner grants (this slice's write
path is API-side only; discovery-job wiring is deferred), issue #732, 0055 adds content_revisions + baselines (issue #731, ADR-0022 capstone
"Stage, diff, activate, and retain compliance content revisions atomically"):
content_revisions is one immutable digest-addressed staged filesystem snapshot
per (source_commit, content_digest), and baselines is one atomically-activatable
coherent set (content revision + catalog execution profile + optional benchmark
revision) with a partial unique index enforcing at most one active baseline per
execution profile -- the activation boundary ADR-0022 requires to be exclusive.
Grants waypoint_compliance_runner SELECT/INSERT plus column-scoped
UPDATE (source_commit) on content_revisions (ContentRevisionStager's idempotent
ON CONFLICT DO UPDATE write path) and SELECT on baselines; activation/rollback
never run as a runner role, issue #731, 0056 adds run_scope_snapshots (issue
#733, epic #726 Wave 2, ADR-0023): one immutable row per scan run recording the
requested `target_scope` (tri-state all/explicit) alongside the exact resolved
stable-component id set and every scope omission with its reason -- the
requested-versus-resolved audit freeze `RunCreationService.CreateScanRunAsync`
writes via the new `ScopeResolutionService`/`RunScopeSnapshotRepository`, ON
DELETE CASCADE off `runs.id` (matching job_credential_bindings' convention),
no new runner grants (API-side only, mirroring migration 0054), issue #733,
0057 adds scan_plans + scan_plan_items (issue #734, epic #726 Wave 2, ADR-0023/
0024): the immutable, digest-addressed execution plan compiled from a run's
resolved component scope -- one scan_plans header row per run (plan schema
version, a link to migration 0056's run_scope_snapshots row, the deterministic
plan_digest, a human-readable explanation, and skips_json for every candidate
component that did not become an accepted item) plus one scan_plan_items row
per ACCEPTED execution item (exact catalog execution profile/baseline/benchmark-
revision identity, transport/selector, report group/priority, required
credential purposes, and declared input names) -- written once by
`RunCreationService.CreateScanRunAsync` via the new `ScanPlannerService`/
`ScanPlanRepository`, every plan-item FK ON DELETE RESTRICT (a frozen plan must
never be invalidated by later catalog/baseline changes) except `scan_plan_id`
itself which cascades off its owning plan/run, no new runner grants (API-side
only, mirroring migrations 0054/0056; #735-#737 own the runner-consumed
component-job layer built on top of this), issue #734, 0058 grants
waypoint_compliance_runner SELECT/INSERT/UPDATE on components and INSERT on
component_observations (issue #732 discovery-wiring remainder):
DiscoverJobHandler now calls the rewritten atomic
ComponentRepository.UpsertDiscoveredAsync (issue #840's ON CONFLICT rewrite
against migration 0054's two existing unique indexes -- no new schema needed)
after its existing InventoryRepository write, so a real discovery pass
materializes `components` rows instead of only repository-seeded test rows,
issue #732 / #840,
0059 adds trust_bundles + trust_policies (issue #753, epic #726, ADR-0025
"Connection-scoped trust"): trust_bundles is one immutable Admin-uploaded CA
certificate/chain (public material, never a secret) with subject/issuer/
fingerprint/validity metadata parsed at upload time and immutable supersede-not-
mutate replacement, and trust_policies is one scoped (scope_type, scope_id)
binding to either a trust_bundle or an explicit, reasoned, audited
skip-verification bypass, never a process-global default -- partial unique
indexes enforce at most one ACTIVE bundle per fingerprint and at most one
CURRENT policy per scope, mirroring migrations 0052/0055's "one current row"
idiom. No new runner grants (this slice is Admin-only API CRUD; runtime
consumption is this issue's stated remainder), issue #753, 0060 keys
config_docs to the stable catalog_execution_profile_id identity (nullable,
additive alongside the existing free-text profile column) and adds
scan_plan_items.input_resolutions_json/attestation_resolution_json/
config_resolution_digest -- issue #735 (epic #726 Wave 2, ADR-0024
"Control-granular settings and snapshots"): resolves each plan item's Input/
Attestation config-doc snapshot Global -> Site -> Target at plan-compile time,
keyed to the plan item's own catalog execution profile rather than a single
fixed ScanOptions.AttestationProfile name, and freezes the resolution into the
plan item alongside its other frozen fields. No new runner grant (this slice's
write path is API-side only, mirroring migration 0057; ScanJobHandler's
runtime attestation resolution is unchanged this slice), issue #735, 0061 adds
jobs.scan_plan_item_id (issue #737 first slice, epic #726 Wave 2 capstone,
ADR-0024 "one Postgres component job" per accepted plan item): a nullable
ON DELETE RESTRICT link from a fanned-out 'scan' job to the exact immutable
scan_plan_items row it executes. NULL for the legacy target-granular fan-out
path, which RunCreationService keeps unchanged; non-NULL only for a
target_scope-driven run's plan-item-granular fan-out. No new runner grant
(jobs already grants waypoint_compliance_runner SELECT/INSERT/UPDATE via
migration 0025; ScanJobHandler's component-granular execution consuming
scan_plan_items is this issue's stated remainder), issue #737 -- 0062 adds
the append-only `upload_attempts` table (issue #744, epic #726 Wave 4
first slice): a per-attempt audit row (endpoint/collection/attempt_number/
status/error_detail) recorded by ScanUploadCoordinator alongside its existing
jobs.upload_status/upload_detail summary write, so a job's full upload attempt
history (first pass and every stigman-upload-retry call) is queryable rather
than overwritten. New runner grant: SELECT, INSERT on upload_attempts to
waypoint_compliance_runner (append-only; the runner never updates/deletes a
recorded attempt), issue #744 -- 0063 adds the immutable domain-owned result
model (issue #745, epic #726 Wave 4, ADR-0024/0025): component_results (one row
per job attempt against a scan_plan_item, closed status vocabulary
completed/execution_error/skipped, CAT I/II/III open + passed/not_applicable/
not_reviewed/skipped counts), component_result_findings (one row per XCCDF-
mapped control finding, closed status vocabulary distinguishing pass/fail/
not_applicable/not_reviewed/execution_error/skipped -- epic #726 §6's exactly-
once Not_Reviewed rule), and component_result_artifacts (kind/path/digest/size
for raw/attested HDF, CKL, summary, log). All three append-only, no UPDATE path.
New runner grant: SELECT, INSERT on all three tables to
waypoint_compliance_runner, issue #745 -- 0066 (slot claimed for this PR;
0064/0065 reserved by parallel agents at branch time, verified free at commit
time against both the migrations directory and open PRs) wires ADR-0019
retention/purge into the migration 0062/0063 evidence tables: append-only
block-mutation triggers (mirroring 0021/0042's attestation_snapshots pattern)
on component_results, component_result_findings, component_result_artifacts,
and upload_attempts, each with a narrow session-local-GUC carve-out
(waypoint.purge_run_id / waypoint.purge_job_ids) that only RunPurgeService's
own purge transaction ever sets -- no new tables, no new runner grants (purge
deletion is exclusively API-side, same owner-privileged connection every other
purge step already uses), closing PR #961's stated "purge currently RESTRICTs"
gap, issue #963 -- 0064 (the slot 0066 above's own comment already noted as
"reserved by parallel agents at branch time"; re-verified free against both the
migrations directory and open PRs at this PR's own commit time) adds no new
tables: it seeds the hand-curated execution catalog (issue #959 Option C, epic
#726) from docs/explanation/compliance-parity.md's documented provenance-matrix rows into
0050's existing catalog_source_revisions/catalog_products/
catalog_product_versions/catalog_content_releases/catalog_components/
catalog_report_groups/catalog_execution_profiles/
catalog_credential_requirements/catalog_benchmark_references tables -- a
representative slice covering every documented shape (vSphere object-kind
split, VCSA named-service split, NSX named-function split, Photon
whole-appliance), invented-from-documentation data only, every INSERT
idempotent via ON CONFLICT DO NOTHING against 0050's existing natural-key
constraints, no new runner grants (seed-only, same "no runner mutates this
schema" convention as 0050), issue #959 -- 0067 (slot verified free against
both the migrations directory and open PRs at this PR's own commit time;
0065 remains reserved by a different issue) expands 0064's seed to 9 of the
remaining provenance-matrix rows (vSphere 9-0 SRG vmware + VCSA-service rows,
NSX 9-x SRG named-function row, Aria Operations/Automation/Suite Lifecycle SRG
whole-appliance rows, Workspace ONE Access SRG whole-appliance row, VCF 9-x SRG
ssh named-service row), same invented-from-documentation/idempotent-ON-CONFLICT/
no-new-grants pattern as 0064. The VCF 9-x `vcf-api` named-service row was
deliberately NOT seeded by 0067: migration 0050's catalog_credential_requirements
purpose CHECK constraint excluded 'vcf-api' pending issue #807, issue #967 --

0068 (issue #974): adds `inventory_items.version`, additive alongside the
existing `build` column -- no new tables, no runner grant changes --

0069 (issue #977, epic #726; slot verified free against both the migrations
directory and open PRs at this PR's own commit time, rebased onto main after
PR #978 claimed and merged slot 0068) closes the vcf-api gap: #807 closed and
its ADR-0024 resolved the vcf-api credential purpose, so 0069 widens 0050's
CHECK (DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT idiom, matching migration
0022's precedent for widening a closed-vocabulary CHECK) to admit 'vcf-api' and
seeds the 13th and final provenance-matrix row (VCF 9-x `vcf-api` named-service:
SDDC Manager application, Automation application), same
invented-from-documentation/idempotent-ON-CONFLICT/no-new-grants pattern as
0064/0067 --

0070 (issue #998, epic #726, PR #1004): reconciles every seeded
catalog_product_versions.version_key from the pre-decision patch-level/exact
forms ("8.0.3", "9.0.0", ...) to the vendor's DECLARED VERSION SCOPE verbatim
("8.0", "9.x", ...) -- catalog keys only; the scope-matching logic itself lives
in Waypoint.Core.Components.VersionScopeMatcher, no schema shape changes, no
new runner grants --

0071 (issue #1002, epic #726; slot 0070 was reserved for issue #998's catalog
seed/matcher work per epic #726 coordination and is claimed by PR #1004's
merged migration above; 0071 verified free against both the migrations
directory and open PRs at this migration's own commit time, re-verified after
rebasing onto the merged #1003/#1004) removes
migration 0052's admin-stated benchmark_component_mappings.is_srg_no_benchmark
column and its mutual-exclusivity CHECK: SRG participation in benchmark mapping
is now a DERIVED read-state (Waypoint.Api joins the component's bound catalog
content kind), never stored, never admin-settable. Least-lossy history shape:
any row that had is_srg_no_benchmark = true gets its historical fact folded into
its own free-text `reason` column BEFORE the column drops, so an old mapping
decision's audit trail still explains itself; no new runner grants, no other
schema changes, issue #1002 --

0072 (issue #1007, epic #726; slot verified free against both the migrations
directory and open PRs at this migration's own commit time): merges a
duplicate catalog PRODUCT tree the content-pull importer's pre-fix bug created
(ContentPullJobHandler passed a display string instead of the seed migrations'
literal 'vmware' as catalog_products.vendor, defeating the
catalog_products_vendor_key_unique upsert) back onto the canonical
'vmware'-vendor row -- re-points every dependent table down through
catalog_product_versions/catalog_components/catalog_execution_profiles AND the
external tables that reference them (components, benchmark_component_mappings,
baselines, scan_plan_items, config_docs), deterministic adopt-or-drop per table,
no other schema changes, no new runner grants, issue #1007 --
0073 (issue #1016, epic #726; slot verified free against both the migrations
directory and open PRs at this migration's own commit time): adds
content_pull_checks/content_pull_check_results (the content-pull check-phase
fan-out/reconcile linkage, owner decision 2026-08-28: reuse the job-queue's
existing parallelism) and 'content-check' to jobs_job_type_check, no other
schema changes --
0074 (issue #743, epic #726; slot verified free against both the migrations
directory and open PRs at this migration's own commit time): adds
catalog_components.requires_sudo/sudo_requires_password (the catalog's declared
sudo policy) and scan_plan_items.requires_sudo/sudo_requires_password (the
plan-time freeze of that policy, nullable for pre-#743 rows), plus seed
reconciliation UPDATEs restating docs/explanation/compliance-parity.md's documented sudo
shapes for the photon/vidm/vcf-sddc-manager rows earlier migrations seeded, no
new tables or grants --
0075 (issue #784, epic #726; slot verified free against both the migrations
directory and open PRs at this migration's own commit time -- 0074 was
claimed by an in-flight, not-yet-pushed lane on issue #743): adds
run_retention_holds -- the presence-based Admin retention-hold table an
Admin-only reasoned action (RunRetentionHoldService/RunsController) inserts
into/deletes from, gating RunPurgeService.PurgeRunAsync's new hold-exclusion
check. No new runner grants (deliberately withheld -- see the migration's own
header); every transition is audited through the EXISTING audit_log table, no
new audit table --
0076 (issue #1080, epic #726; slots 0074/0075 claimed and unpushed by PR #1076
and issue #784 at this migration's own commit time): re-keys the vSphere 9.x
catalog_product_versions row from the exact key '9.0' to the major-line-scoped
key '9.x', matching the vendor's real declared scope (issue #1079 proved there
is no top-level `vsphere/9.0` vendor directory) so a real observed version like
'9.1.0' matches via VersionScopeMatcher's existing closed two-form test -- no
schema shape changes, no new runner grants, reuses migration 0070's own
idempotent-merge idiom --
0077 (issue #1081, epic #726 section 3): widens the inventory_items type check
constraint to admit 'vcenter' so the appliance itself gets an inventory row --
idempotent DROP IF EXISTS + re-ADD, no column change, no new runner grants --
0078 (issue #1062, epic #726 sections 6/7; slot 0077 claimed and unpushed by
issue #1081 at this migration's own commit time): adds retention_policy -- the
singleton (mirrors appliance_state) Admin-configurable evidence-retention-period
row (default 180 days / ~6 months) backing the new automated purge sweep, no new
runner grants (API-only, same posture as run_retention_holds) --
0079 (issue #1063, epic #726 section 3): adds
`inventory_items.instance_uuid` -- a VM's authoritative vSphere instance
UUID, recorded alongside the existing moref-keyed identity so identically named
VMs stay deconflictable across discovery passes -- no new runner grants (the
table's existing grants already cover the new column) --
0080 (issue #1144, epic #726/#1177): adds
`component_results.execution_error_count` -- the sixth and final
per-finding-status count column, so a component whose controls all mapped to
`execution_error` no longer reads all-zero on the run rollup. Backfills the
column for every pre-existing row from that row's own immutable
`component_result_findings`, taking migration 0066's append-only
`trg_component_results_block_update` trigger's ONE sanctioned exception:
disabled and re-enabled around the single backfill UPDATE statement, inside this
migration's own transaction. No new runner grants (migration 0063's table-level
GRANT already covers the new column) --
0081 (issue #1140, epic #1177): widens `component_results_status_check` to
admit `completed_zero_controls` -- a completed attempt that evaluated ZERO
controls now carries its own status instead of reading as a plain
`completed`. Backfills every pre-existing `completed` row that matches
the same zero-verdict predicate the rollup's `evaluated_zero_component_count`
FILTER already used at read time, taking migration 0080's same one-statement
disable/re-enable of `trg_component_results_block_update`. No new runner
grants at all: the status widening needs none (a CHECK-constrained column's
existing table-level GRANT already covers every value the column may hold), and
the sibling `coverage_incomplete` join in
`JobQueueRepository.RunSummaryProjectionSql` needs none either -- no runner
calls `GetRunAsync`/`ListRunsAsync`/`ListRunHistoryAsync` in
production (issue #1303). The CHECK widening runs BEFORE the backfill, pinned by
Migration0081_PreExistingZeroVerdictCompletedRow_IsBackfilledAfterTheCheckWidens --
0090 (issue #1391, epic #1185 "Content libraries", split from design record #37
-- see its closing comment for the four-child A/B/C/D breakdown -- approved
design #16 section 6; slot pre-assigned 2026-08-30) adds
`content_libraries` -- the content-library REGISTRY: one row per named,
flat-on-disk VCSP library and the single directory it owns, derived
(`RootPath/{name}`) rather than operator-supplied. `name` is still
operator input, though, so path-traversal is foreclosed by an ENFORCED
single-path-segment check plus a resolved-path root-prefix check in
`ContentLibraryRepository.ResolveDiskPath` (the layer that touches the
filesystem), not merely by the controller's input regex or by construction of
the derivation alone (round 1 of PR #1649 corrected this paragraph, which
originally claimed the latter). Deliberately inert -- no VCSP `lib.json`/
`items.json` file semantics (#1393) or item rows (#1396) land here, only
the minimal CRUD API (POST/GET/DELETE) every later step resolves "which
library, which path" through. No new runner grants (this repo's #556
grant-hygiene convention, 0059/0078/0107 precedent): every write and read is
Admin/Viewer API-side; the nearest future runner-side consumer is #1057
(depot-fed add-to-library), which must ship its own GRANT migration when it
lands --
0099 (issue #1479, pre-assigned slot -- deliberate gap from 0081, not a
bug) reserves the `binaries-download` run/job type in both
`jobs_job_type_check` and `runs_run_type_check`; no new tables, so no
new runner grants --
0107 (issue #1406, epic #1182 "Subscriptions, retention & scheduling", split
from design record #1047, approved design #16 section 2; slot pre-assigned
2026-08-30, gap 0082-0106 reserved by concurrently in-flight sibling issues):
adds `download_retention_policies` (per-scope grace-window/dial-default
retention configuration, seeded with a singleton 'default' scope row) and
`download_retained_content_state` (per-artifact tracked/grace/pinned/
pending-purge/purged lifecycle state + pin metadata, FK to
`depot_artifacts`) -- the retention DOMAIN MODEL only, no sweep job
(#1436), manual-download dial (#1440), or API surface (#1453). Distinct
bounded context from the unrelated compliance-domain `retention_policy`
(0078)/`run_retention_holds` (0075) -- table names prefixed
`download_` to disambiguate. No new runner grants YET -- there is no
consumer to grant to; #1436, as filed, is a genuine
`waypoint_download_runner`-claimed job (creates
`RetentionSweepJobHandler` and a `DownloadRunnerJobTypes` constant),
not an API-process-owned service like 0075/0078, so it must ship its own
GRANT migration when it lands (0100/#1484 precedent). State-transition
legality is enforced in
`Waypoint.Core.Downloads.RetainedContentStateTransitions`, not a DB
trigger --
0111 (issue #1480, epic #1184, split from #1054 (closed as design record
2026-08-30); research lane #1031; slot pre-assigned 2026-08-30, verified
free against both the migrations directory and open PRs at authoring time and
re-verified in round 2 -- #1509's PR #1791 holds 0130 and #1389's PR #1770
holds 0113, both still in flight; #1765's 0109 has since landed):
adds `vks_library_items`, the shared dual-backend VKS/VKR item
identity/dimension model (depot-fed and public-mirror) parsed by the
single name grammar #1031 measured against 138/138 live public items across
four coexisting naming eras. `naming_era` CHECK admits the four eras
plus `'unparsed'` -- an unrecognized name is still stored, never
dropped (#1031 Risk). `etag` is a change token, explicitly NOT a
checksum (#1031: 93/138 items share one etag across four files of very
different sizes; one item's served .mf MD5 differed from its own published
etag) -- proven never conflated with `sha256` by
`VksChangeTokenGuardTests`' type-level and grep-based guards. Model-
only slice: no sync logic. Grants `waypoint_download_runner`
`SELECT, INSERT, UPDATE` (no DELETE) -- proven both directions by
`VksLibraryIndexRunnerRoleGrantTests` (this repo's #556 convention).
No separate API grant: `Waypoint.Api` connects as the table owner --
0117 (pre-assigned slot, issue #1470) adds esx_acquisition_subscriptions --
named ESX acquisition presets selecting a subset of the
lcm.esx.supported.host.platforms vendor vocabulary, TEXT[] selection validated
by the API at write time (never a schema-level enum), disable-in-place
(enabled=false UPDATE, never a DELETE) so a preset's history survives; no new
runner grant (the sync job that reads this table, #1484, grants itself what it
needs when it lands) --
0100 (issue #1488, epic #1180, split from design record #1038; slots 0099/0117
claimed by parallel migrations at this migration's own commit time): rekeys
`depot_artifacts`'s identity from the two incompatible legacy
`external_id` namespaces (offline disk-walk relative path vs. connected-pull
bare filename) to a single `relative_path` column via an idempotent
`RENAME COLUMN` -- every pre-existing row from EITHER legacy namespace keeps
its data untouched, no reconciliation between the two namespaces attempted here
(that is presence-sweep behavior, #1503/#1512). Adds `size_bytes` (the other
half of the catalog identity pair) and `last_verified_at` (presence field),
both left unset by the generic upsert path in this slice. Adds
`unknown_catalog_files` -- files present on disk with no matching catalog
identity, insert-or-touch-last-seen only, no delete path (design decision Q11:
alert instead of drop) -- with a new `waypoint_download_runner` grant
(SELECT/INSERT/UPDATE, no DELETE) mirroring migration 0025's existing
`depot_artifacts` grant to the same role. --
0118 (issue #1403, epic #1181, split from the design record #1161; slot
pre-assigned 2026-08-30 while sibling migrations 0099/0100/0107/0117 were
in-flight on other branches -- the resulting 0081-to-0118 numbering gap is
expected, not a collision, since NpgsqlSchemaMigrator orders by filename, not
contiguous numbers): adds `oci_bundles` (one staged imgpkg-shaped OCI
bundle tar and its computed depot-registry destination) and
`push_target_consumers` (a configured depot-registry push target,
carrying a `write_mode_enabled` placeholder safety flag for #1441's
enable/disable bracket) -- model-only, no acquisition (#1413) or push (#1441)
logic. No new runner grants: no runner process reads or writes either table
yet, so #1413/#1441 each add the GRANTs their own writer needs alongside the
runner-role-connects test proving them (this repo's #556 convention) --
0103 (issue #1517, epic #1180, split from design record #1043; slot
pre-assigned 2026-08-30, inside the same 0082-0106 numbering gap 0107's own
comment above reserves for concurrently in-flight sibling issues): widens
`credentials_credential_type_check` to admit `repo-basic-auth` (a
`CredentialTypes` value -- distinct bounded context from the unrelated
`CredentialPurposes`/ADR-0021 matrix -- so it rides the EXISTING
`credentials` table/API unmodified: no new create/rotate code, per issue
#1517's own AC) and adds `repo_credential_bindings` (which repo store --
depot/umds/photon/vmtools/vks/content-libraries, the closed set
`deploy/nginx/conf.d/default.conf`'s #1502 location tree actually serves
-- a `repo-basic-auth` credential authenticates for), UNIQUE on
`store` alone (one purpose in this context, unlike
`target_credential_bindings`'s `(target_id, purpose)` pair) and
counted as its own `RepoCredentialBindings` delete-blocker category
(the identical shape #584/migration 0043 established for
`TargetCredentialBindings`, not a new pattern). No new runner grant:
exactly one consumer today, the API process itself -- the HONEST no-grant
rationale (no consumer yet), not the wrong one issue #1406's review round 1
finding 5 corrected (a future runner-claimed consumer, if #1510 as filed
turns out to need one, ships its own GRANT migration, 0100/#1484 precedent) --
proven by `RunnerRoleGrantDriftTests`' negative-direction cases for both
runner roles (SELECT and INSERT) --
0127 (issue #1436, epic #1182; slot 0127 -- the next free slot named on epic
#1182's 2026-08-30 decision thread): no new tables -- widens
`jobs_job_type_check`/`runs_run_type_check` to admit
`retention-sweep` (the sweep's own job type, `RetentionSweepJobHandler`)
and grants `waypoint_download_runner` exactly `SELECT, UPDATE` on
`download_retained_content_state` and `SELECT` on
`download_retention_policies` -- the two operations the sweep actually
performs (it transitions existing rows, it never inserts one -- see
`RetentionSweepService`'s doc comment), proven alongside a "must still
fail" negative by `RetentionSweepRunnerRoleGrantTests` (this repo's #556
convention) --
0128 (issue #1440, epic #1182; slot 0128 -- the next free slot after
#1436's 0127): adds `download_out_of_scope_content` -- the
out-of-scope half of the review-list mechanism (the orphan half already
exists as `unknown_catalog_files`, 0100/#1488), same insert-or-touch,
no-delete-path shape. No new runner grants -- no consumer, runner or
otherwise, exists yet; real out-of-scope discovery is deferred to #1421 --
0091 (issue #1447, epic #1183, split from design record #38, approved
design #16 section 4; slot pre-assigned 2026-08-30 on #38's closing
comment): adds `esx_patch_store_index` (a cumulative model, keyed on
#1446's content-identity SHA-256, of every metadata bundle a reconciliation
pass has ever seen at a store root -- a bundle absent from one run is never
row-deleted, since that absence is what makes "missing" detection possible)
and `esx_patch_store_discrepancies` (missing/orphan findings as
first-class rows, one per `(store_root, discrepancy_type, key)`;
`resolved_at` is bookkeeping on the alert's own lifecycle only -- rows
are never deleted, and no orphan disk content is ever removed by
`EsxPatchStoreReconciler`, design decision Q11/issue #1447 AC3). Grants
`waypoint_download_runner` `SELECT, INSERT, UPDATE` (no DELETE) on
both tables -- the reconciler runs runner-side (only a runner has
filesystem access to a mounted patch store, ADR-0013/0014) -- proven both
directions by `EsxPatchStoreIndexRunnerRoleGrantTests` (this repo's
#556 convention) --
0109 (issue #1392, epic #1184, split from #1053; slot pre-assigned
2026-08-30): adds `vmtools_artifact_index` (one row per real artifact
found by a recursive crawl of the public VMware Tools mirror, upsert-keyed
on `(relative_path, etag)` so an unchanged re-crawl never duplicates a
row; `is_latest_alias` marks rows under a `latest/`/`<major>latest/`
subtree without ever treating them as "current" -- research #1030 finding 1)
and `vmtools_esx_version_mapping` (the upstream `versions` file's
join key, one row per parsed data line, `sequence_in_file` preserving
the file's own newest-first-by-ESXi-build order so an unparseable Tools
version can still fall back to release-recency ordering; malformed rows are
never persisted, only surfaced as parser warnings, the #1446 lesson).
Metadata-only migration: `self_hash_sha256`/`signature_available`
are nullable placeholders for a later child. Grants
`waypoint_download_runner` `SELECT, INSERT, UPDATE` (no DELETE) on
both tables -- the crawler and versions-file parser both run runner-side --
0129 (issue #1705, part of validation epic #1704; slot 0129 -- the next free
slot after #1440's 0128, verified against both the migrations directory and
open PRs at authoring time): widens `depot_artifacts_status_check` to
admit `'missing'` -- the #1503 presence sweep's absent-from-disk result,
which the original slice-1 constraint never allowed, aborting the whole
`catalog-index` job on the first entry a real, partial depot produces. No
grant changes (a CHECK widening touches no privileges); the full resulting
vocabulary is `Waypoint.Core.Catalog.DepotArtifactStatuses.All`, proven
against this constraint's parsed SQL by
`DepotArtifactStatusesConstraintDriftTests` --
0113 (issue #1389, epic #1185 "Content libraries", split from design record
#1056; slot pre-assigned 2026-08-30, verified unused on main and in every open
PR at branch time): adds `content_library_folders` (a self-referencing
operator-defined virtual folder tree per library, DB metadata only -- never
written to disk -- with a partial unique index closing the NULL-parent
root-name-uniqueness gap the composite sibling-name UNIQUE constraint alone
cannot cover, see the migration's own header) and
`content_library_item_folders` (single-parent item-to-folder assignment;
`item_id` has NO foreign key -- issue #1391/PR #1649 shipped no items
table, so it carries the caller-supplied Guid identity
`Waypoint.Core.ContentLibraries.ContentLibraryItemWrite.Id` already
establishes, pending #1396). No new runner grants (this repo's #556
grant-hygiene convention, 0090 precedent): folder create/rename-move and item
assignment are Operator+ API-side, folder delete is Admin, reads are Viewer+,
all via `ContentLibraryFoldersController`; no runner ever gets a grant on
either table, proven both directions by `RunnerRoleGrantDriftTests`.
0104 (issue #1421, epic #1182, split from design record #1045; approved
design #16 section 2, ADR-0028; slot pre-assigned 2026-08-30, inside the same
0082-0106 numbering gap 0107's own header reserves for concurrently
in-flight sibling issues): adds `presets` (shipped read-only rows and
operator clone-to-custom rows, `source_preset_id` naming lineage) and
`subscriptions` (product/lane tracked at a subminor/minor/major
`line_granularity` -- ADR-0028 names exactly these three widths;
review round 1 removed a fourth `whole-release` value the issue's own
AC3 had modelled in error, per the 2026-09-07 AC amendment -- anchored at a
version string, nullable `preset_id`, and per-lane UMDS/VKS
refresh-window and retention-override dial columns) -- domain model and
persistence only, no evaluation-job wiring (#1046), API surface
(#1450/#1453), or cross-lane supersession (#1437). `subscriptions.lane`
reuses the existing `Waypoint.Core.Secrets.RepoStores.All`
acquisition-lane vocabulary rather than a new one; both tables'
`line_granularity` CHECK constraints match
`Waypoint.Core.Subscriptions.SubscriptionLineGranularityValues.All`, and
`presets.stack`'s CHECK matches
`Waypoint.Core.Subscriptions.PresetStacks.All` (round 1 finding F2 --
the migration originally introduced that vocabulary with no mirroring C#
constant), both proven by `SubscriptionsConstraintDriftTests` (this
repo's #1517/RepoCredentialBindingConstraintDriftTests convention). Two
lineage CHECKs (`presets_lineage_requires_custom_check`,
`presets_source_preset_id_not_self_check`) enforce, in schema, the
invariant the table's own comments state but the original migration left
unenforced (round 1 finding F4). No new runner grants -- no runner-claimed
job reads or writes either table yet; the first consumer that needs
runner-side access ships its own GRANT migration (0100/0107 precedent) --
0130 (issue #1509, epic #1184, split from #1052; slot pre-assigned 2026-08-30
as 0108, renumbered to 0130 at rebase time -- see the migration file's own
header comment for why): adds `photon_repo_index` (one row per discovered
Photon RPM repo directory, keyed on (version, variant, arch);
`has_repodata=false` marks a `photon_snapshots`-shaped directory with
no `repodata/repomd.xml` -- indexed, never an error, research #1029
finding 5), `photon_image_index` (image-tree files per version/channel/kind
-- schema only, no writer until the image-discovery job, this issue's
documented remainder, lands), and `photon_subscription_config` (unpopulated
preset shape). Grants `waypoint_download_runner`
`SELECT, INSERT, UPDATE` (no DELETE) on `photon_repo_index` only --
`PhotonRepoDiscoveryJobHandler` is the only consumer this migration ships
alongside; the other two tables get no grant yet (0118's `oci_bundles`
precedent for the same shape of gap), proven both directions by
`PhotonRepoIndexRunnerRoleGrantTests` --
0131 (issue #1464, epic #1183; slot 0131 -- the issue body's pre-assigned
0119 was reassigned at pick time since 0117-0130 had landed or were claimed
by then, verified against the migrations directory and open PRs
immediately before use): adds `consumer_views` -- named operator-defined
ESX platform-set views (`name` unique, ordered `platforms` TEXT[],
`is_default`). AT MOST one default is enforced by the partial unique
index `idx_consumer_views_default_unique` (database); EXACTLY one at any
time is enforced by a seeded default row plus the API/repository refusing to
delete the row currently holding the default or clear its `is_default`
(409 `default_required`), while marking a DIFFERENT row default MOVES the
flag atomically inside one transaction (PR #1816 round 2) -- see the
migration's own header for the full "at most" vs "exactly" split. Platform-key
values are validated against the static vocabulary in
`Waypoint.Core.Downloads.ConsumerViewPlatformVocabulary` at the API layer
only, never a schema CHECK, matching migration 0117's
`esx_acquisition_subscriptions.selected_platforms` precedent. No new
runner grants -- Admin-only API-side model/CRUD slice, no generation or
serving logic reads this table yet -- 0132 (issue #1783) added
`depot_artifacts.bundle_id`, bumping 96 -> 97; 0134 (issue #1804) adds
`depot_artifacts.superseded_at` (nullable timestamp), the never-delete
marker `RekeyManyAsync`'s collision path sets on a stale legacy row it
folds into a surviving new-identity row -- see that migration's own header
comment for why a status VALUE was rejected in favor of a column -- bumping
97 -> 98 (0133, carried by PR #1831's `content_library_items` migration,
had not merged as of this rebase; 0134 was free then, taken since -- see below); 0131
itself, described above, bumps it again, 98 -> 99; and 0135 (issue #1790) adds the
Photon image-discovery runner grant -- originally authored as 0134, renumbered
to 0135 at rebase time because PR #1842 above merged first and also claimed
slot 0134 -- bumping 99 -> 100.

0133 (issue #1396, epic #1185; slot 0133 -- uncontested: main has taken
0131 and 0134 and PR #1844 owns 0135, so no renumbering was needed at this
rebase): adds `content_library_items`, the content-library item identity
table, plus the FK it closes on `content_library_item_folders.item_id`
(0113 shipped that column deliberately without one, per its own header -- see
0133's header for why the FK is CASCADE rather than RESTRICT). No new runner
grants -- no runner process reads or writes this table today (0090/0113
precedent); #1057 ships its own GRANT migration when it lands. Bumps
100 -> 101.
