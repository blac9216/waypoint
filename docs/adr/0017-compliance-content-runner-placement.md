# ADR-0017: Compliance-content pull/import execute in the compliance-runner

Status: Accepted

Supersedes the `content-pull`/`content-import` job-type placement in
[ADR-0013](0013-control-plane-and-runners.md) §2's `download-runner` bullet.

## Context

ADR-0013 §2 provisionally listed `content-pull`/`content-import` under
`download-runner`'s "later content-library, repository, and managed-content jobs"
bullet, written before either job type had a design. Issue #40's 2026-08-11
architecture-direction comment settled the actual design: compliance content (the
VMware DoD compliance-automation repo — pinned tag/branch, recorded commit, profile
inventory) is consumed exclusively by compliance execution (`scan` reads profiles from
it) and produces the `profiles` inventory that only the compliance domain interprets.
It shares no dependency, credential type, or storage mount with the download domain's
depot/tool/bundle concerns — `download-runner` has no reason to host git or parse
InSpec profile metadata.

## Decision

`content-pull` and `content-import` are `compliance-runner` job types, claimed under
`JobCapabilities.Compliance`, not `JobCapabilities.Download`. Per ADR-0014 §7's
storage-follows-least-privilege rule, the compliance-content working tree is a
compliance-runner-only mount, read-only to `scan`/`discover`/`credential-test` and
writable only by `content-pull`/`content-import` execution.

`download-runner` retains `catalog-index`, `download`, and the remaining "later"
content-library/bundle/update types (ADR-0013 §2 otherwise unchanged).

## Rationale

- Colocating compliance content with the compliance-runner keeps the mount, the
  consuming job types (`scan`), and the producing job types (`content-pull`) in one
  process's trust boundary, matching ADR-0014 §7's per-domain mount assignment instead
  of cutting across it.
- No shared code, credential type, or vendor tool ties compliance-content management to
  the download domain; grouping by "some future admin screen might list them together"
  would have been organizational, not architectural.

## Consequences

- `Waypoint.Core.Jobs.JobCapabilities.Compliance` gains `content-pull`/`content-import`;
  `JobCapabilities.Download` loses them. `JobCapabilities.All` (their union) is
  unchanged, so `jobs_job_type_check` requires no migration.
- The compliance-content working tree and any future content-import bundle staging
  path are compliance-runner mounts and DB grants (migration pattern of
  `0025_runner_db_roles.sql`), not download-runner ones.
- `docs/domain-model.md`, `docs/architecture.md`, and `docs/api-contract.md` prose
  describing runner job-type assignment must read "compliance-runner: discover,
  credential-test, scan, remediate, content-pull, content-import" going forward.
