# Author mode

The reference plan-work, interrogate and github-workflow consult when a decision has been
made and must be written into the design set. It answers three questions: *does this need
recording*, *in which form*, and *how exactly*. Read `docs/doc-manifest.md` first
for the repository's paths.

## Does it need recording? (the ADR trigger test)

Record a decision as an ADR only when all three hold:

1. **Hard to reverse** — changing it later costs real work or migration.
2. **Surprising without context** — a future reader would look at the code and ask why.
3. **A real trade-off** — there were genuine alternatives and one was chosen for reasons.

Fails one? Then it is not an ADR. It may still be a rationale entry (a local "why" attached
to the code that carries it), a glossary sharpening, or nothing — most decisions are
nothing, and a log full of nothings hides the reversals that matter.

## Which form

| The decision is about… | Write |
|---|---|
| system shape, integration pattern, technology with lock-in, a boundary, a deliberate deviation, a constraint invisible in code | **ADR** |
| why one line/block of code is the way it is (a timeout, an ordering, a flag, a guard) | **rationale entry** + `# why:` pointer at the code |
| what a word means, or that two words were one thing | **CONTEXT.md** entry (and fix every doc that used the other word) |
| where something lives or how containers talk | **architecture.md** at the matching C4 level, diagram and prose together |
| a rule about the process itself | not this skill — `docs/process/` via configure-workflow |

A decision can produce more than one: an ADR that changes topology also changes the
Container diagram and probably a glossary term. Ship them in the same PR.

## How — ADR

1. Number: highest existing + 1. Slug: the decision, not the topic
   (`0026-runners-claim-own-jobs`, not `0026-job-engine`).
2. Fill every MADR section ([standard.md#adrs](standard.md#adrs)). Decision Drivers come
   straight from the interrogation record's constraints; Considered Options are the
   options that were actually put to the owner, with the recommendation's reasoning as
   the pros/cons. This is why the interrogation record exists — copy from it, do not
   re-imagine it.
3. Status `Proposed` on the PR; the reviewer's merge is acceptance and flips it to
   `Accepted` in the same PR (the reviewer edits the line, or the author does when the
   reviewer says "approved pending status").
4. Superseding, whole: `Supersedes: NNNN` on the new ADR *and* `Superseded-by:` +
   `Status: Superseded` on the old one, same PR. Superseding, partial: `Amends: NNNN` on
   the new ADR, `Amended-by:` on the old one, old status unchanged, and the new ADR's
   Context says which part ("replaces §4 of ADR-0013 — runners host all tooling
   in-process"). Numbers only in header lines; the scope is prose.
5. Run `scripts/adr-index.sh --write`, commit the README change with the ADR. `--write`
   updates the table even when other ADRs have findings (a repo mid-normalisation is
   normal); it still prints them, and they are the audit's business, not this PR's.

**Worked partial supersede.** New `0026-inspec-sidecar.md` header:
`Status: Proposed` / `Amends: 0013` / `Date: …`; its Context opens "ADR-0013 §4 states
that runners host all tooling in-process; this ADR replaces that for InSpec only." Old
`0013-…md` gains one line, `Amended-by: 0026`, and nothing else changes.

**Design it twice.** Before writing Considered Options for an architectural ADR, sketch
two genuinely different interfaces or shapes for the same decision, not one shape and a
straw man. If the second is not worse in an articulable way, the decision is not ready and
goes back to interrogate. The rejected sketch becomes a Considered Option.

## How — rationale entry

1. Choose the area file from `doc-manifest.md`; create it from the template if the area
   is declared but the file is missing.
2. Section `## <source file>` (create in path order); entry `### <prefixed-slug>` — grep
   the file for the slug first; a collision means a more specific prefix, never `-2`.
3. 2–6 lines: the constraint or trade-off that makes the code non-obvious. If you are
   describing what the code does, delete it — that is the code's job.
4. `Refs:` line with the issue/PR/ADR numbers. Those numbers appear nowhere in the code.
5. At the code: replace any explanatory comment with a one-line warning plus the pointer.
   `# why: docs/rationale/<area>.md#<slug>` on its own line or trailing.
6. Run `scripts/check-pointers.sh`.

## How — glossary

Add or sharpen the term in `CONTEXT.md`: canonical name, one-line meaning, `not:` list of
rejected synonyms. Then grep the design set for the rejected synonyms and fix them in the
same PR — a glossary that the other docs contradict is worse than none.

## How — architecture doc

Edit at the level the decision lives: a new service is a Container change; a new module
inside a service is a Component change; a new external system is a Context change. Update
the mermaid block and the prose together; the diagram is not decoration, it is the
definition the prose explains.

## The ratchet

Classify the change you are recording — spike (nothing to record), bounded (a rationale
entry or glossary tweak), architectural (an ADR plus its knock-ons). Complexity found
mid-writing upgrades the class; it never downgrades. If a "rationale entry" turns out to
need Decision Drivers, it was an ADR.

## When the repository is mid-normalisation

Author mode writes new records to the standard even if the old ones are not there yet.
It does not fix neighbouring ADRs, does not create design-set files that
`doc-manifest.md` lists but the repo lacks (a missing `CONTEXT.md` is an audit finding,
`DESIGNSET_MISSING`, and a remediation issue), and does not hand-edit the ADR index —
`adr-index.sh --write` works regardless of findings elsewhere.

## What author mode never does

Write a spec, plan, or interrogation record into the repository. Decide anything the
owner did not decide — an unresolved trade-off is sent back to interrogate with the
options, not settled in the Decision section.
