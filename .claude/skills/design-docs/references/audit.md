# Audit mode

Measure the design set's drift from the standard and hand plan-work a gap report. The
audit **changes nothing and files nothing**: it reads, runs scripts, thinks, and writes one
ephemeral file in scratch. Fixing is remediation; remediation is issues; issues are
plan-work's. Keeping that seam is what lets the audit be run casually — during a morning
cleanup, before a release, by a cheap model — without anyone worrying what it touched.

## 1. Tier 1 — mechanical (scripted)

```
scripts/audit.sh --root <repo> --out <scratch>/design-docs-gap-<date>.md
```

Run it on a **full checkout** — the Tier 2 tasks compare docs against code and deploy
trees, and a docs-only export makes every path citation look dead. If the checkout lacks
the code directories the docs cite, mark those tasks *environment-limited* in the report
instead of recording findings.

This runs `check-pointers.sh`, `adr-index.sh --check`, and the structural checks
(MADR sections, header blocks, `Kind:` markers vs directories, README index coverage,
CONTEXT.md uniqueness and domain-model term coverage, C4 headings and mermaid presence)
against the shape in `docs/process/documentation.md`, and writes the gap report scaffold
([../templates/gap-report.md](../templates/gap-report.md)) with the Tier 1 findings filled
and the Tier 2 tasks listed. If `documentation.md` is missing it stops: audit a repository
that has not adopted, and every finding is "not adopted".

## 2. Tier 2 — cross-reference (agent, cheap model is fine)

For each task the scaffold lists, check and record findings under it. These are lookups,
not judgement calls, so a small model with grep does them well:

- **Dead citations** — every `#NNNN`, `ADR-NNNN`, `docs/…` path and `deploy/…` path
  mentioned in the design set and in `AGENTS.md`: does the target exist (path) or is it
  closed/renamed in a way that makes the sentence false (issue/ADR)? `gh issue view` is
  GET-only and allowed.
- **Layout table vs tree** — `AGENTS.md`'s repository layout block against `ls`.
- **Container names** — services in the Container diagram vs `deploy/compose*.yaml`
  service names (or the adopted topology source).
- **Superseded-but-cited** — a wholly superseded ADR (`Status: Superseded`) cited as
  authority in prose ("per ADR-0006") where the superseding one should be. An amended ADR
  (`Amended-by:`) is still authority for everything outside the amended part — check the
  cited claim, not the number.
- **Glossary use** — rejected synonyms from `CONTEXT.md` appearing in the design set.
- **Standard-version gap** — anything `documentation.md` declares that the standard now
  requires differently (adopt mode's refresh case).

Do not do semantic doc↔code review ("does architecture.md describe what the code does?")
in a default audit; it costs a real model's attention across the whole codebase. The
owner asks for it explicitly, and then it is a plan-work research lane, not an audit tier.

Reading the Tier 1 dump: a code that hits **every** instance of a doc kind (all 25 ADRs
lack `Date:`) is one finding about the shape predating the rule, not 25 — cluster it as
the normalisation story, and say so, or plan-work will size it as 25 chores. Heading
checks match on the leading word (`## Component view` satisfies Component), so a level
reported present may still be thin; that is Tier 2 judgement, not a script bug. Template
boilerplate left in a real file (an unfilled "Example entry", a `<Term>` placeholder) is
its own line in the chore cluster — it is not organic drift.

## 3. Cluster and hand off

Group findings into clusters a single PR would fix (one rationale area; the ADR
normalisation; the Diátaxis move for one directory; dead citations in one doc). For each
cluster the report states: what, where (paths/lines), which standard rule, and a size
guess (S/M) so plan-work's estimator has a hint. Trivial Tier 1 findings — a missing
`Refs:` line, one bad anchor — go into one *chore* cluster, not one each.

Finish with one line in chat: the report path, cluster count, finding count by tier, and
"hand to plan-work: `plan the docs remediation from <path>`". Then stop. Do not open
issues, do not fix the easy one, do not paste the report into an epic — the report is
scratch and plan-work extracts what becomes durable.

## Frequency

Adoption PR (baseline), before each milestone close, and whenever github-workflow's
morning cleanup is run. CI covers the two script checks on every PR, so a routine audit
mostly finds Tier 2 drift — which is the kind scripts cannot see and the reason the
audit exists.
