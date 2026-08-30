# Adopt mode

Bring a repository onto the standard, or refresh one that adopted an older version. This
mode creates framework files; it does **not** rewrite existing design docs — that is
remediation, which the audit measures and plan-work schedules. Adoption lands as one PR
through github-workflow like any other change.

## 1. Survey what exists (facts, no questions yet)

Look for, and note the path or absence of: `docs/adr/` and its README, any `Status:` lines
in ADRs, `docs/rationale/`, `# why:` pointers anywhere, an architecture doc, a glossary or
domain-model doc, a `docs/README.md`, existing Diátaxis-like directories, CI workflows
that run shellcheck or skill tests, and `docs/process/`. Run `scripts/check-pointers.sh`
and, if `docs/adr/` exists, `scripts/adr-index.sh`; their findings tell you how far the
repo is from the standard before you ask anything. Design prose that predates adoption
(`docs/design.md`, `NOTES.md`, a wiki export) is not deleted or moved now — it is a
remediation target the audit reports as `DOC_OUTSIDE_KIND_DIR`, and plan-work decides
whether it becomes rationale entries, an explanation doc, or nothing.

## 2. Ask the owner only the adoption decisions

Use the interrogate skill (bounded ceremony) for exactly these, each with a
recommendation drawn from the survey:

- the design-set list for this repository (which docs are in scope);
- rationale areas to declare now (recommend: the areas that already have `# why:` pointers,
  plus one per top-level code directory that will get them);
- the Diátaxis move: which existing docs go where (recommend the assignment guide in the
  standard) and whether the move happens in the adoption PR or as remediation issues
  (recommend: adoption PR creates the directories and moves nothing; the audit's gap
  report drives the moves, so inbound links are fixed issue by issue with review);
- ADR normalisation: whether existing ADRs are normalised (the standard says uniformly,
  under one normalisation ADR — confirm, do not assume);
- CI wiring: which workflow runs `check-pointers.sh` and `adr-index.sh --check` (recommend
  the existing skills/shellcheck workflow if one exists; otherwise a new
  `docs-checks.yml` on pull_request, always-report so path filtering cannot hide it).

## 3. Scaffold

Create only what is missing; never overwrite a file that exists.

| File | From |
|---|---|
| `docs/doc-manifest.md` | [../templates/doc-manifest.md](../templates/doc-manifest.md), filled with the answers above |
| `docs/adr/README.md` | [../templates/adr-README.md](../templates/adr-README.md); then `scripts/adr-index.sh --write` |
| `docs/rationale/<area>.md` per declared area | [../templates/rationale-area.md](../templates/rationale-area.md) |
| `CONTEXT.md` | [../templates/CONTEXT.md](../templates/CONTEXT.md), seeded from the domain-model doc's terms |
| `docs/{tutorials,how-to,reference,explanation}/` | empty dirs with a `.gitkeep` only if the move is deferred |
| `docs/README.md` | [../templates/docs-README.md](../templates/docs-README.md), listing current docs at their current paths |
| CI step | [../templates/docs-checks.yml](../templates/docs-checks.yml), or the same two steps added to an existing workflow |
| `AGENTS.md` | add: read the ADR index table first; the design path is interrogate → plan-work → design-docs; no committed specs |

The scripts are copied into the repository only if the repository does not already vendor
skills under `.claude/skills/` — if it does, the CI step calls them from there, so one
copy exists.

## 4. Record the adoption

- A short ADR: "Adopt the design-docs standard" (MADR, of course) — it is hard to reverse,
  surprising without context, and had alternatives.
- If normalisation was confirmed, the normalisation ADR is filed in the same PR, in
  `Proposed` status, and the backfill work is remediation issues.

## 5. Hand off

Run `scripts/audit.sh --out <scratch>` on the adoption branch. The gap report it produces
is the remediation backlog; hand it to plan-work ("plan the docs normalisation from
<scratch path>") and stop. Adoption is complete when the PR is merged and
`doc-manifest.md` exists; remediation is its own story.
