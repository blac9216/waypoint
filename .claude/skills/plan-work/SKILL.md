---
name: plan-work
description: Turn an ask into GitHub state the github-workflow skill can execute — research lanes, an owner interrogation, a milestone with domain epics and right-sized issues (provable acceptance criteria, area labels, dependencies, size estimates), projected onto the roadmap from the repository's own history. Use it whenever the owner wants to plan, decompose, scope, estimate or schedule work ("plan this feature", "decompose the epic", "how long will this take", "research X before we build it", "re-sequence the milestones", "when will milestone Y land"), and whenever github-workflow's readiness gate stops on work that has not been through planning. Works on any repository; best on one running the workflow.
argument-hint: <ask | epic | milestone> [--research] [--resequence]
---

# Plan work

github-workflow executes; it never decomposes. This skill produces what it executes: a
story the owner has actually decided, broken into issues a reviewer can prove at merge,
homed on the board, with an estimate on every issue and a date on every milestone. Its
outputs go to GitHub (issues, comments, milestones) and to scratch — **never to committed
files**: the repository holds only what is permanent and reviewed; the record of research
and decisions lives on the issues, out of the code.

## Classify first

Say it out loud so the owner can override: **spike** (a feasibility question — answer it,
file nothing), **bounded** (fits one issue, maybe two — skip research, one short
interrogation round, file the issue), **architectural** (a story — the full flow). When in
doubt, the heavier path; complexity discovered mid-way upgrades it, never downgrades.

## The flow (architectural)

1. **Orient.** Read `docs/process/*.md` and `*.local.md` if present, the board, the open
   milestones and their descriptions, and anything the ask touches in `docs/`. Run
   `scripts/history.sh` once — it builds the calibration table the estimates use
   ([references/estimate.md](references/estimate.md)).
2. **Research** — only when the ask depends on facts nobody in the room has (vendor
   behaviour, an API's real shape, what a sibling system does). One research epic, one
   lane issue per question, findings as comments, a composite, then a **hard stop for the
   owner's sign-off** ([references/research.md](references/research.md)). Findings are
   propagated into the issues they affect; they are never committed as docs.
3. **Interrogate** — invoke the `interrogate` skill with the record location set to the
   milestone's design-record epic thread (or the epic being planned). Its output is the
   decision record every issue is written against.
4. **Decompose** — milestone description, domain epics, issues
   ([references/decompose.md](references/decompose.md)): provable acceptance criteria,
   `area:*`, priority, `blocked by` links (across milestones too), a consumes/produces note
   where a sibling depends on a contract, an **Estimate** section on every issue. Every
   issue estimated **L is split before filing**. Everything is homed and lands in
   **Backlog** — the owner releases work to Ready deliberately.
5. **Project** — per-issue estimates from the calibration table; milestone duration from
   its issues' critical path at the observed parallelism; place the milestone on the
   timeline after the already-scheduled ones unless issue dependencies say otherwise; set
   `due_on` ([references/timeline.md](references/timeline.md)). Ambiguous placement is a
   question for the owner, not a guess.
6. **Self-review, then present.** Placeholder / contradiction / scope / ambiguity pass over
   everything filed; then the owner sees the plan summary (milestone, epics, issue count by
   area and size, projected dates with sample sizes) and confirms before anything is
   released.

Flags: `--research` forces the research phase even for a bounded ask (the owner wants facts
before deciding); `--resequence` skips to step 5 for the milestones the owner names — the
same logic re-places them when priorities change or a new story slots in ahead.

## Rules

- **Commit nothing.** Research, decisions and estimates live on GitHub objects.
- **Issues are the unit of truth for time.** Estimates are per issue; milestone dates are
  derived; milestones never carry `blocked by` links to each other.
- **Estimates carry their evidence.** Every number states the bucket and sample size it
  came from (`M · area:backend · n=12 · p50 2.1d`) or says it is a default.
- **Backlog on filing.** Nothing this skill files is Ready.
- **Ask when unsure, look up when possible** — facts are yours, decisions are the owner's.

Templates: [references/templates/](references/templates/) — research epic and lane,
lane findings, composite findings, sign-off request, plan summary. Scripts:
`scripts/history.sh` (calibration from repository history; GET-only; last 90 days by default),
`scripts/timeline.sh` (milestone placement proposal from issue estimates and dependencies;
GET-only; writes `timeline.md` — the agent applies `due_on` after the owner confirms).
