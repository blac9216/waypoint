# ADR-0032: ESX patch store — VCFDT-only acquisition, generation-agnostic reconciliation

Status: Accepted
Date: 2026-09-06

## Context

The original download-parity design (Epic #16, owner grill decisions 9–11,
2026-08-28) planned a UMDS-binary lane: a prepare/install two-job split that sources a
version-matched VCSA ISO, extracts the UMDS tar and EULA, and ships an EULA-accept
gate before running UMDS's own `-S`-flag configuration and sync. Research (#1028,
ratified 2026-08-29) found that plan does not survive Broadcom's own 9.1 roadmap: UMDS
is **end-of-life and absent from `vcf-download-tool` 9.1 entirely**. Continuing to
build EULA-gated UMDS-binary install machinery would ship a lane with no upgrade path
past the version it launches on. The research also found the ESX patch store is not a
sidecar the way UMDS was: `vcf-download-tool` 9.1 places it **inside** the depot tree
it already manages (owner decision 4 amendment, 2026-08-29), and generations 6.7–9.1
are all reachable through the same tool-driven acquisition path used for every other
VCFDT-indexed product, at varying levels of live-verified confidence (7.0/8.0 proven
with real payloads; 6.7 metadata-only).

## Decision Drivers

- No lane should ship EULA-gated install/config machinery around a binary the vendor
  has already announced end-of-life for the next platform generation.
- ESX patch content must be usable by both pre-9.1 and 9.1+ consumers without forcing
  every consumer onto the same generation's file layout.
- The sibling repository's UMDS reconciliation and retention logic (XML-first-then-prune,
  months dial) is real, working code worth porting regardless of which acquisition
  backend feeds it — the value was never in the UMDS binary itself, it was in keeping
  the served repo metadata correct against whatever content is actually on disk.
- Mixed-generation consumers exist in the same fleet (a disconnected 8.0 host cluster
  and a 9.1 SDDC Manager against the same depot) and both need a generation-correct
  view without the appliance maintaining separate copies of the same bytes.

## Considered Options

1. **Ship the original UMDS-binary two-job EULA-gated install as designed.** Matches
   the frozen design-record body on #16 verbatim, but builds install/config/EULA
   machinery around a binary with no future past the platform generation UMDS
   disappears from, and treats the ESX store as a sidecar when the vendor's own 9.1
   tooling already treats it as depot-internal.
2. **UMDS-binary retained, feature-flagged maintenance-mode, alongside a new 9.1
   backend.** Keeps a working path for operators mid-migration, but doubles the
   acquisition/config surface (two credential/config paths, two sets of failure modes)
   for a binary already announced end-of-life — this was research's interim
   recommendation before the owner ruling below replaced it.
3. **Retire UMDS-binary acquisition entirely; VCFDT-only acquisition across all
   supported generations (6.7–9.1); reconciliation/index/retention ported once,
   generation-agnostic; mixed-generation consumers served via generated hardlinked
   view trees, not clones or nginx rewrites** (this decision, owner ruling 2026-08-29).
   One acquisition backend, one credential/config surface, no dead-binary maintenance
   burden; the cost is that pre-7.0 generations metadata-only (6.7) get a visibly
   weaker confidence tier than live-proven ones, which must be surfaced rather than
   hidden.

## Decision

The ESX patch store is acquired exclusively through `vcf-download-tool`
(VCFDT) — no UMDS binary is installed, configured, or EULA-gated by Waypoint. Issues
#1049/#1050 (UMDS prepare/install, ephemeral `-S` config) are closed superseded by
this ADR; #1159 (VCFDT-only acquisition) and #38/#1051 (retitled: generation-agnostic
store reconciliation/index/retention, ported from the sibling's XML-first-then-prune
model with a months dial) carry the design forward. The store lives inside the depot
tree (ADR-0029), not as a sidecar volume. Mixed-generation consumers are served
through generated **hardlinked view trees** — a generation-scoped view is built by
hardlinking the subset of on-disk files that generation's per-zip filtering selects,
never by cloning bytes or rewriting URLs at nginx — because perfect generation-purity
is not achievable (Broadcom's own per-zip packaging mixes content across generation
boundaries in ways a rewrite rule cannot cleanly separate). Gate evidence for
enablement: platforms 6.7–9.1 default-enabled; 7.0/8.0 are live-proven against real
payloads; 6.7 is metadata-only and must be labeled as such wherever its confidence
tier is surfaced.

## Consequences

- No EULA-acceptance ledger, no extracted-EULA UI flow, and no `-S`-command
  ephemeral-config scrubbing exist for this lane — that entire subsystem from decisions
  9–10 is not built.
- The legacy Download Token (UMDS's credential in the original design) is not this
  lane's credential; per the 2026-08-29 ratification it is demoted to a legacy slot for
  ad-hoc 7.x/8.x host/vApp updates only, with the activation code as the primary
  acquisition credential for everything VCFDT-driven.
- A confidence tier (live-proven vs. metadata-only) must be part of the store's data
  model and surfaced to operators per generation — this is new scope the original
  UMDS-binary design never needed, since UMDS install either worked or failed outright.
- Hardlinked view-tree generation is new lifecycle machinery (build, invalidate,
  rebuild on new acquisition) that has no analog in the UMDS-binary design; it must be
  triggered by the same acquisition/retention events that change the underlying files.
