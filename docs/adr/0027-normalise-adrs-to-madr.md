# ADR-0027: Normalise ADRs 0001–0025 to the MADR frame

Status: Proposed
Date: 2026-08-30

## Context

ADR-0026 adopts the design-docs standard, which requires every ADR to carry the full
MADR section set (Context · Decision Drivers · Considered Options · Decision ·
Consequences) plus a machine-readable header. The 25 ADRs accepted before this adoption
(0001–0025) predate that shape: most have only a Context and Decision (sometimes titled
differently), none have a `Status:`/`Supersedes:`/`Superseded-by:` header block, and none
have Decision Drivers or Considered Options sections. The standard's own immutability
rule — Context and Decision are byte-immutable once Accepted — would otherwise forbid
ever bringing these 25 files into the new shape at all, so a decision that authorises
exactly one uniform backfill pass is needed before that work can start.

## Decision Drivers

- The standard's own rule that Context and Decision, once Accepted, are never rewritten
  — normalisation must not violate the rule it exists to protect.
- Reconstructed material (Decision Drivers, Considered Options) must be visibly
  distinguishable from the original 2026-era prose, so a reader can tell verified history
  from inference.
- The backfill has to be scoped so a reviewer knows exactly what to scrutinise — a
  normalisation PR that also relitigates the original decision is doing the wrong job.
- 25 files is enough that "normalise as you touch each one" would leave the index
  inconsistent (some MADR, some not) for months; a single authorising decision keeps the
  pass bounded and traceable to one ADR.

## Considered Options

1. **Grandfather the 25 existing ADRs** — leave them in their original shape forever,
   apply MADR only to new ADRs from 0026 onward; cheapest, but the index and the
   standard's own audit (`ARCH_MISSING_LEVEL`/section-presence checks) treat every
   pre-0026 ADR as permanently non-conformant, and a reader has two shapes to learn.
2. **Supersede each of the 25 individually** with a fresh MADR-shaped ADR — preserves
   strict immutability by construction (nothing is ever backfilled in place), but burns
   25 new ADR numbers on pure reformatting, breaks every existing cross-reference
   (`[ADR-0013](adr/0013-...)`) across `domain-model.md`, `security.md`, and other ADRs,
   and drowns the real decision history in mechanical churn.
3. **One-time uniform normalisation under a single new ADR** (this decision) — every one
   of the 25 files is edited once, in place, to add the header block and the two
   reconstructed sections; original Context and Decision prose is kept byte-identical and
   only re-homed under the new headings; every reconstructed section opens with
   `_Backfilled under ADR-0027 from #<issue>, #<pr>._` so provenance is visible inline.
   Costs one bounded pass with a distinct reviewer instruction, but keeps file names and
   numbers stable, keeps the index honest going forward, and marks reconstruction
   instead of hiding it.

## Decision

Option 3. This ADR authorises a single, uniform normalisation pass over ADRs 0001–0025:
one mechanical header/index-conversion issue first (adds the `Status:` header block and
runs `adr-index.sh --write`, touching no prose), then one backfill issue per ADR that adds
Decision Drivers and Considered Options reconstructed from that ADR's originating issues
and PRs. Existing Context and Decision text is preserved verbatim, re-homed under the new
headings without rewording. Each reconstructed section begins with the provenance line
`_Backfilled under ADR-0027 from #<issue>, #<pr>._` naming its sources. `docs/adr/README.md`
keeps its amend/supersede rules and adds one sentence pointing at this ADR as the
exception to "Context and Decision are never touched." The PR description for every
normalisation PR instructs the reviewer: *review as formatting + backfill only — verify
the original Context and Decision are byte-identical against `main`, then scrutinise only
the backfilled sections against their cited sources.* No normalisation PR reopens or
re-argues the original accepted decision.

## Consequences

- All 25 ADRs eventually carry the same header block and section set as ADRs written
  after adoption; `adr-index.sh --check` can treat the whole directory uniformly once the
  backfill completes.
- Until each ADR's backfill issue lands, its Decision Drivers/Considered Options sections
  are either absent (pre-header-conversion) or present but explicitly marked as
  reconstructed — never presented as original 2026-era reasoning.
- Reviewers of normalisation PRs are directed away from re-litigating settled decisions;
  a reviewer who does so anyway is reviewing the wrong thing, per this ADR's own
  instruction.
- This authorisation is exercised exactly once, for 0001–0025. A future repository
  adopting design-docs late writes its own equivalent normalisation ADR for whatever
  range it has accumulated by then — this ADR does not generalise past this pass.
- The mechanical header-conversion issue and the 25 backfill issues are remediation work,
  filed and sequenced by plan-work from this adoption PR's baseline audit — not done in
  this PR.
