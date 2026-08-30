# Download parity fixture matrix: sibling contract catalog

Split A of design record #16/#1036 (issue #1394), under the *Download & depot
parity* milestone's docs/research/conformance epic (#1186). This is the living
checklist that translates `vcf-docker-download`'s
[`tests/INTEGRATION-RUNBOOK.md`](https://github.com/blac9216/vcf-docker-download/blob/main/tests/INTEGRATION-RUNBOOK.md)
(TC-01…TC-30, EP-01…EP-06, ID-01…ID-03 — 39 cases) into Waypoint-side contract statements,
each mapped to the owning lane epic. `vcf-docker-download` is a sibling
repository under the same copyright holder — **owner-authored, not vendored** —
being absorbed and retired as parity lands (see `AGENTS.md`'s License &
Borrowing Policy).

Every row states what the **Waypoint** surface must do, not a restatement of
the sibling's PowerShell steps — the sibling's CLI flags, log-line text, and
`Docker run` invocations are implementation detail of a program Waypoint is
replacing, not a contract Waypoint inherits verbatim.

This document is a map, not a test suite: no test code lives here. The
concrete fixtures and integration tests land in the child issues below, each
scoped to one lane:

| Child | Scope |
| --- | --- |
| #1411 | Resume-protocol integration suite (`Save-WebFile` / `DownloadJobHandler`) |
| #1428 | UMDS lane parity fixtures (patch-store remove-old, reconciliation, prune safety) |
| #1449 | Mirror & content-library lane parity fixtures (stale-delete guards, VCSP writer validity) |
| #1463 | Depot-core parity fixtures (version-comparator ordering, presence-sweep correctness) — hands the completed matrix to the conformance gate, #1037 |

Per this issue's Risks note, each lane epic's closing PR is expected to update
its own rows below (status and the **Issue** column) as that lane's fixtures
land — this is a process convention, not CI-enforced. The **Issue** column
names the issue that will carry a case's concrete test; it stays blank (`—`)
until that issue is filed.

## Status legend

| Status | Meaning |
| --- | --- |
| `not yet buildable` | The owning epic has no implementing Waypoint code on `main` yet. |
| `buildable now` | The underlying Waypoint code already exists on `main` (verified in code, not assumed). |
| `covered` | A Waypoint test already exercises this contract. |
| `out of scope` | The sibling case belongs to the Transfer/air-gap-bundle feature, which is a separate future delivery story (*Transfer & enclave modes*, not yet epic'd) and out of this milestone's scope per milestone #11's Scope/Non-goals ("Out: transfer/enclave bundling (own story)"). |
| `n/a` | CLI-specific sibling behavior with no Waypoint analogue (Waypoint has a REST API + web UI, not an interactive CLI). |

## Buildable-today subset: the resume protocol

None of the sibling runbook's TC/EP/ID cases exercise partial-download resume
directly — it isn't in `tests/INTEGRATION-RUNBOOK.md` at all. It is called out
here anyway because it is the one load-bearing contract from the #1036 gap
report that already has implementing code on `main`:
`runners/download-runner/powershell/project/vcf-download-manager.common.ps1`
(reached via `WaypointDownload.psm1`'s `Invoke-WaypointDownload`, which dot-
sources it) implements the `Save-WebFile` resume protocol — `.resume.tmp`
merge, the 200-vs-206 response distinction, oversize handling, and 401/403
treated as non-retryable. `DownloadJobHandler.cs` is the C# caller that
delegates resume/retry to this PowerShell layer entirely and layers its own
independent sha256 verification on top (see the handler's own doc comment).
`#1411` pins the PowerShell-layer behavior with concrete tests; every other
lane below has no implementing code on `main` yet and is marked
`not yet buildable`.

## TC-01…TC-30: primary test cases

| Case | Waypoint contract | Owning epic | Status | Issue |
| --- | --- | --- | --- | --- |
| TC-01 Docker Build | The download-runner image builds cleanly with only air-gap-safe dependencies installed — no `vcf-download-tool` bundled, no standalone UMDS installer, no depot activation code or token (ADR-0015 decision 3, License & Borrowing Policy). PowerShell 7 *is* installed, deliberately: the image hosts it in-process via `Microsoft.PowerShell.SDK` per ADR-0013 decision 4, not as a prohibited dependency. | #1180 | buildable now | — |
| TC-02 Help and No-Args | An incomplete or malformed job request is rejected by the REST API with a clear validation error (no CLI usage text — Waypoint has no CLI). | #1180 | not yet buildable | — |
| TC-03 Configuration Validation | download-runner loads depot/subscription/credential configuration at startup and fails clearly, not silently, if it is invalid. | #1180 | not yet buildable | — |
| TC-04 UMDS Download | A UMDS-subscription job downloads and indexes ESX patch metadata/VIBs into the depot. | #1183 | not yet buildable | — |
| TC-05 UMDS Repair | A UMDS repair job validates vendor metadata and reports repository health without re-downloading. | #1183 | not yet buildable | — |
| TC-06 UMDS Prune | A UMDS prune job removes VIBs orphaned from every valid vendor index. | #1183 | not yet buildable | — |
| TC-07 UMDS Remove-Old | Retention removes UMDS content older than the configured window while preserving artifacts still referenced by a current index. | #1182 | not yet buildable | — |
| TC-08 VCSA Download | A VCSA acquisition job resolves the latest release for the configured version line from the vendor catalog, acquires it via `vcf-download-tool` if not already present/verified, and prunes superseded artifacts once the new release verifies. | #1181 | not yet buildable | — |
| TC-09 VCSA Update-Repo | The depot serving surface exposes an extracted, world-readable VCSA offline-repo tree for vCenter's own update mechanism to consume. | #1180 | not yet buildable | — |
| TC-10 VCSA Update-Content-Lib | A VCSA ISO can be added to a local content library. | #1185 | not yet buildable | — |
| TC-11 VMware Tools Download | A VMware Tools mirror job pulls the latest Windows installer from Broadcom (excluding ARM) with version metadata. | #1184 | not yet buildable | — |
| TC-12 VMware Tools Update-Content-Lib | The mirrored VMware Tools installer can be added to a local content library. | #1185 | not yet buildable | — |
| TC-13 VKS Download | A VKS mirror job syncs content-library items within a configurable time window. | #1184 | not yet buildable | — |
| TC-14 VKS Repair | A VKS repair job rebuilds the item index and verifies artifact integrity. | #1184 | not yet buildable | — |
| TC-15 VKS Prune | A VKS prune job removes item directories not referenced by the index. | #1184 | not yet buildable | — |
| TC-16 VKS Remove-Old | Retention removes VKS items older than the configured window. | #1182 | not yet buildable | — |
| TC-17 Content Library Import | Files placed into a library's incoming area are imported, indexed, and cleared from incoming. | #1185 | not yet buildable | — |
| TC-18 Content Library Repair | A library repair job regenerates metadata and verifies file integrity. | #1185 | not yet buildable | — |
| TC-19 Content Library Prune | A library prune job removes item directories not referenced by its index. | #1185 | not yet buildable | — |
| TC-20 Content Library Remove-Old | Retention removes library items older than the configured window. | #1182 | not yet buildable | — |
| TC-21 Transfer Prepare (All Components) | Air-gap bundle export. | — | out of scope | — |
| TC-22 Transfer Prepare (Filtered Components) | Air-gap bundle export, filtered. | — | out of scope | — |
| TC-23 Transfer Ingest (Merge Mode) | Air-gap bundle import, additive. | — | out of scope | — |
| TC-24 Transfer Ingest (Replace Mode) | Air-gap bundle import, wipe-and-replace. | — | out of scope | — |
| TC-25 Transfer Ingest (Custom Transfer Dir) | Air-gap bundle import, alternate staging path. | — | out of scope | — |
| TC-26 Pipeline | A scheduled or manually triggered refresh runs every enabled lane's acquisition and post-processing in sequence. (The sibling case's `-PrepareTransfer` staging step is the Transfer feature — out of scope; see TC-21.) | #1182 | not yet buildable | — |
| TC-27 Interactive Menu (Smoke Test) | CLI-only navigation menu; superseded by the web UI. | — | n/a | — |
| TC-28 Shell Mode | CLI-only interactive PowerShell escape hatch; Waypoint has no interactive shell. | — | n/a | — |
| TC-29 Photon OS Download | A Photon mirror job replicates the release/updates/extras repository trees 1:1 from Broadcom, excluding debuginfo and historical archives. | #1184 | not yet buildable | — |
| TC-30 Photon Transfer (Mirror Delete + FilesImported Delta) | A Photon mirror sync never deletes local content when the upstream listing is partial or incomplete, and its reported new-or-changed file count reflects the actual delta, not the destination's total file count. | #1184 | not yet buildable | — |

## EP-01…EP-06: edge/error-path cases

| Case | Waypoint contract | Owning epic | Status | Issue |
| --- | --- | --- | --- | --- |
| EP-01 Missing Volume Mount | download-runner reports a clear, actionable error — not a hang or crash loop — when the configured depot storage path is unavailable. | #1180 | not yet buildable | — |
| EP-02 Unknown CLI Argument | The API rejects an unrecognized job field/parameter with a clear 4xx validation error (no CLI parameter binding involved). | #1180 | not yet buildable | — |
| EP-03 VCSA Download With Removed VcfVersion Flag | VCSA acquisition always resolves the latest release for the configured version line; there is no per-release override, and requesting one is rejected the same as any other invalid job parameter. | #1181 | not yet buildable | — |
| EP-04 Transfer Prepare With Empty Repo | Air-gap bundle export with nothing to export. | — | out of scope | — |
| EP-05 Transfer Ingest Replace Without Force | Air-gap bundle import safety gate. | — | out of scope | — |
| EP-06 Content Library Import on Empty Incoming | Importing an empty incoming area succeeds as a no-op (0 succeeded, 0 failed), not an error. | #1185 | not yet buildable | — |

## ID-01…ID-03: idempotency cases

| Case | Waypoint contract | Owning epic | Status | Issue |
| --- | --- | --- | --- | --- |
| ID-01 Repeated Repair Is Safe | Running a repair job twice (UMDS, content library) produces identical state and no errors on either run. | #1183 (UMDS) / #1185 (content library) | not yet buildable | — |
| ID-02 Repeated Prune Is Safe | Running a prune job on an already-pruned VKS library removes zero items on the second run. | #1184 | not yet buildable | — |
| ID-03 Repeated Transfer Prepare | Air-gap bundle export, run twice. | — | out of scope | — |

## Fixture policy

Fixtures backing the concrete tests in #1411/#1428/#1449/#1463 are invented,
never exported from the lab. Real-tool (`vcf-download-tool`) invocation paths
remain live-lab-validated only, per `docs/testing.md`'s VCFDT policy — no
vendor tool bytes in CI, ever.
