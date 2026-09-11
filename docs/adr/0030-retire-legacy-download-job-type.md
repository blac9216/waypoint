# ADR-0030: Retire the legacy `download` job type and `POST /downloads`

Status: Accepted
Date: 2026-09-06

## Context

The M1 `download` job type wraps `Save-WebFile` around an `external_id` treated as if
it were a directly fetchable URL. Issue #968 found the contract was never real: no
authenticated Broadcom depot artifact was ever downloadable through it, because real
depot access requires `vcf-download-tool`'s own authenticated session and layout, not
a bare URL fetch. `POST /downloads` and the `download` job type have therefore been a
fixture-only path since before this epic. The download-parity design (Epic #16, owner
grill decision 6) makes vendor acquisition tool-driven end to end (`binaries download
--id`, issue #795/#1479) and gives mirror lanes (Photon, VMware Tools, VKS) their own
sync job types built on the same `Save-WebFile` primitive, rather than routing either
through the generic `download` type.

## Decision Drivers

- A job type with no real implementation behind it is worse than no job type: it looks
  like a supported path in the schema/allowlist/UI while silently doing nothing useful
  against a real depot.
- Vendor acquisition and mirror-lane sync are architecturally different operations
  (tool-driven session vs. direct HTTP fetch) and should not share one job type just
  because both eventually call a download primitive.
- `Save-WebFile` itself is not the problem — it remains the right primitive for mirror
  lanes; only the generic `download` job type and its API surface are being removed.

## Considered Options

1. **Keep `download` as a thin dispatcher, route it to the right handler internally.**
   Preserves API compatibility, but perpetuates the fiction that one job type covers
   both tool-driven acquisition and direct-fetch mirroring, and keeps `#968`'s
   dead-on-arrival contract nominally "supported."
2. **Retire the job type and the endpoint outright** (this decision). Vendor
   acquisition becomes `binaries-download` (already shipped, issue #1479/#1482);
   mirror lanes get their own sync job types as each lane lands (Wave 5, Epic #16).
   `#968` closes as superseded by this design rather than fixed.

## Decision

The `download` job type and `POST /downloads` are retired: removed from the
`jobs_job_type_check` constraint and `JobCapabilities.Download`'s allowlist, the
enqueue path, `ToolGatedDownloadJobHandler`'s decorative gate, and any queue-UI wiring
that depends on them (tracked as issue #1040, not yet landed at the time of this ADR —
see `docs/explanation/architecture.md` and `docs/explanation/domain-model.md` for current build status).
`Save-WebFile` survives as the mirror-lane primitive, its behavioral contract pinned by
tests (issue #1036's parity fixtures). Issue #968 closes as superseded by this ADR, not
fixed forward. The M1 `artifacts` volume's role, tied to the retired job type, is
retired alongside it (see ADR-0029 for what replaces it).

## Consequences

- A migration must drop the `download` value from `jobs_job_type_check` and any rows
  that reference it need an explicit disposition (delete, or retain as inert history —
  issue #1040's implementation records which).
- Any client (UI or external) that still calls `POST /downloads` breaks; this is a
  deliberate API removal, recorded in the Wave 0 API reconciliation (issue #1034), not
  a deprecation window — the endpoint was never functionally real.
- `#572`'s version-comparator class of bugs no longer has a `download`-job-shaped
  workaround to route around; the comparator work (issue #1039) is the actual fix.
