# ADR-0026: Adopt the design-docs standard

Status: Proposed
Date: 2026-08-30

## Context

Waypoint's design record — ADRs, `architecture.md`, `domain-model.md`, `security.md`,
`roadmap.md`, `api-contract.md` — has grown organically since the design phase closed.
ADRs predate a consistent section shape; there is no generated index, so an agent has to
open all 25 files to learn which are still active; there is no glossary, so domain terms
are defined once in `domain-model.md` prose and drift as new docs use near-synonyms; and
`docs/` has no declared organisation, so a reader cannot tell a tutorial from a reference
from a decision record by directory alone. `design-docs` (built in the storage repo,
epic blac9216/storage#168, and vendored into this repository by #1540/#1542) packages a
concrete answer to all four gaps as a repo-general framework: MADR-shaped ADRs with a
generated status index, a C4-levelled architecture doc, a root `CONTEXT.md` glossary, and
a Diátaxis-organised `docs/`. Adopting it here is the first real-repository use of that
framework; Waypoint is the pilot.

## Decision Drivers

- Agents (and the owner) need to know which ADRs are still active without reading all of
  them.
- Domain vocabulary must have one canonical home so design docs and code stop drifting
  toward near-synonyms for the same concept.
- `docs/` must sort by reader need (learning vs. task vs. lookup vs. why) rather than by
  whatever order files were created in.
- The adoption must be cheap to reverse or extend — a documentation convention, not a
  tool the project depends on to run.
- Specs and plans belong on GitHub, not as committed files that go stale the moment the
  issue closes.

## Considered Options

1. **Keep the ad-hoc docs as they are** — zero migration cost; but the four gaps above
   keep compounding as the design set grows (currently 25 ADRs and rising with the
   compliance-parity work), and every new contributor re-derives the same missing
   conventions.
2. **Superpowers' committed-specs path** (`docs/design/`, `docs/superpowers/` —
   design docs as committed spec/plan files) — keeps deliberation next to the code; but
   duplicates what GitHub issues already hold, and a committed spec is either stale the
   day after merge or requires perpetual upkeep with no clear owner.
3. **Adopt the design-docs standard** (this decision) — MADR ADRs + generated index,
   C4 architecture doc, root glossary, Diátaxis layout, rationale-index convention for
   code comments; specs stay on GitHub (interrogate → plan-work), only durable outcomes
   land in the design set. Costs a normalisation pass on the 25 existing ADRs (ADR-0027)
   and a Diátaxis move (deferred to remediation issues) but directly closes all four
   driver gaps and gives every future adopting repository the same shape.

## Decision

Waypoint adopts the design-docs standard as its architecture-documentation framework.
The adopted shape — design set, ADR range, rationale areas, Diátaxis directories, CI
wiring — is recorded in `docs/doc-manifest.md`, which this ADR authorises as the
repository-specific record the audit reads first. Existing design docs move under the
Diátaxis directories as remediation, not in this adoption PR; the 25 existing ADRs are
normalised to the MADR frame under ADR-0027 in the same PR. `docs/adr/README.md` carries
the generated status index from `scripts/adr-index.sh`; `AGENTS.md` is updated to send
agents to that index before opening individual ADRs, and to name the design path
(interrogate → plan-work → design-docs author mode; specs and plans are never committed).

## Consequences

- Every future ADR is written to the full MADR shape from the start; `adr-index.sh
  --check` fails CI (once #1361 wires it) on drift between the table and the files.
- The 25 pre-existing ADRs need one normalisation pass (ADR-0027) before their Context
  and Decision Drivers sections are trustworthy reading — until that lands, those
  sections are backfilled and marked as such rather than authoritative history.
- `docs/` gains four empty Diátaxis directories now; the actual move of existing docs
  into them is tracked as a separate remediation backlog (from this PR's baseline audit,
  handed to plan-work) rather than done here, so `docs/README.md` and internal links
  still point at today's paths until that lands.
- `CONTEXT.md` becomes the one place domain vocabulary is defined; `domain-model.md` and
  future design docs are expected to use its words rather than introduce new spellings.
- No CI enforcement exists yet (`docs-checks.yml` is tracked under #1361, an owner-run PR
  because the machine account lacks `workflow` scope) — drift is caught by manual
  `audit.sh` runs until that lands.
