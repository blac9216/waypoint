---
name: github-workflow
description: Issue-driven GitHub workflow for a repository organised as Project board → delivery-story milestones → domain epics → issues. Use it whenever work touches GitHub state or code in such a repo — picking up an issue, epic or milestone ("work epic 1176", "work the triage queue", "next ready"), running solo or as the sole orchestrator of parallel subagents (serial/parallel, interactive/overnight), branching, committing, opening or handing off a PR, triaging filed issues, running live validation, asking for "status", "good morning", "morning cleanup", or a session handoff. Consult it before writing any code so the board stays honest from the first minute.
argument-hint: <issue|epic|milestone|triage|next> [solo|serial|parallel] [overnight]
---

# GitHub workflow

One skill, two roles. **Solo**: this session implements. **Orchestrated**: this session is the
*sole* orchestrator of background subagents and never implements, reviews or merges
non-trivial work itself. In both roles the reviewer is always a fresh, contextless agent
running `github-pr-review`. Everything the workflow knows about *state* lives on GitHub —
the Project board, milestones, epics, issues and PR comments — never in chat memory. A
session that cannot be reconstructed from GitHub after a crash was not run correctly.

The repository carries its own specifics. This skill is general: repo names, paths,
commands, label values, testing recipes and environment nuance come from
`docs/process/*.md` (committed) and `*.local.md` (never committed). If something you need
is not there, that is a gap in the repo's process docs — file it, do not improvise it into
the skill. See [references/process-dir.md](references/process-dir.md).

## The shape you are working in

| Layer | What it is | What it holds |
|---|---|---|
| **Project board** | The one guaranteed home of every issue. | `Status` column, `Verified`, `Claimed by`. Closed items stay as the verification ledger. |
| **Milestone** | A multi-epic delivery story. Optional — only feature stories get one. | Description = rolled-up state (rewritten wholly, rarely) + dated decision ledger. No comment thread. |
| **Epic** | One domain inside a story, or a milestone-less theme. Always >1 child. | Goal/scope/design pointer in the body; **events in the comment thread**. ≤100 children. |
| **Issue** | The work. | Under an epic when it groups; otherwise directly on the board. |

Columns: **Triage → Backlog → Ready → In progress → In review → Done**. `Verified`:
`n/a · pending-live · live-verified · live-failed`. Labels classify (type, severity,
priority, `concern:*`, ≥1 `area:*`, `deferred` = filed-out-of-scope provenance,
`backlog` = shelved); fields track state. Native mechanisms over prose: sub-issues for
hierarchy, `blocked by` dependencies for sequence, close reasons
(`completed`/`not planned`/`duplicate`), task-list checkboxes for acceptance criteria,
`gh issue develop` for the linked branch.

## Entry — resolve target, mode, horizon

Every invocation resolves three things before anything is read or written:

- **Target**: an issue, an epic, a milestone, the Triage queue, or "next Ready by priority".
- **Mode**: `solo` · `orchestrated serial` · `orchestrated parallel`.
- **Horizon**: `interactive` · `overnight`.

Infer what the target shape makes obvious — a single issue is solo; a large milestone is
orchestrated parallel. An epic is ambiguous. **When unsure, ask one question; never
default.** A wrong mode wastes a night, a question costs a line.

## The flow

The numbers are the order things happen. Each step names the reference that holds its
detail; read the reference when you reach the step, not before.

**0. Entry** — start the session log **first**, before resolving anything or asking
any question ([references/formats/session-log.md](references/formats/session-log.md));
a session that stops at its opening question still leaves a log line saying so. Then
resolve target/mode/horizon (above). The log is always on, interactive or not, so
`status` works at any moment.

**1. Orient** — read, in order: `docs/process/*.md`, every `*.local.md`, the target's
milestone description, the target epic's body and its recent comments, the board slice
for the target plus everything In progress / In review across the board (that is where
collisions live). Re-derive review rounds from PR comments. Derive state from GitHub
only.

**2. Claim** — take a claim id and write it to `Claimed by` on the epic (or the
standalone issue). Refuse anything live-claimed by someone else; take over stale claims
with an event comment. [references/claims.md](references/claims.md).

**3. Readiness gate** — the target must look like planning produced it: template-shaped
bodies, provable acceptance criteria, type + `area:*` labels, an epic with >1 child, a
milestone with epics, dependencies set where order matters. If it does not, **stop and
ask** — "this doesn't look like it has been through planning; proceed anyway?" — and do
nothing until answered. This skill never decomposes work; a separate planning skill does.

**4. Maintenance** — run the full pass ([references/maintenance.md](references/maintenance.md)):
triage drain (with sequencing), host audit, cleanup, rule audit, state audit. Runs here,
on every resume, before each parallel wave or every three serial issues, and on "morning
cleanup".

**5. Execute** — per issue: pick next by dependencies then priority → worktree →
implement (solo: you; orchestrated: implementer agent) → PR from template → contextless
review, fresh agent every round, three-round cap → fix rounds → the merge **closes the
issue** → reviewer sets `Verified` → cleanup → **event comment on the epic**. Board
columns have exactly one owner each — see [references/orchestration.md](references/orchestration.md)
for the loop, the dispatch rules and parallel safety.

**6. Validate** — live validation is a defined tight loop with its own epic: run →
findings → fix-wave → re-run until green. You decide *when*; you must consider it and log
the decision at the named triggers. [references/validation.md](references/validation.md).

**7. Overnight** — hourly `heartbeat` cron for self-resume, quiet chat, structured log,
owner-gated decisions routed around with `help`.
[references/overnight-and-status.md](references/overnight-and-status.md).

**8. Status and the morning ritual** — `status` = brief without stopping. `good morning`
= freeze dispatch, drain in-flight, brief, then the owner's Q&A and drift correction;
`morning cleanup` on request runs validation-if-warranted and maintenance, reports, and
asks: handoff or continue. Same reference.

**9. Handoff** — a chat-only structured handoff
([references/templates/handoff.md](references/templates/handoff.md)); release claims;
write memory. Nothing goes to the issues — continuous state maintenance is the handoff.

## Rules that do not bend

- **Issue first.** No code without an issue. Assign yourself as a courtesy; the *claim*
  is the coordination signal.
- **Never merge your own work.** Every non-trivial PR is approved and squash-merged by a
  fresh `github-pr-review` agent. The one exception is the fast path: ≤3 files, ≤50
  changed lines, zero executable code, nothing under `docs/adr/` — `[trivial]` in the
  title, self-merge after CI. If you are wondering whether it qualifies, it does not.
- **Close at merge.** Acceptance criteria must be provable when the PR merges. Proof that
  review cannot supply (a live environment, real vendor tooling, a third party) becomes
  `Verified: pending-live`, never a reason to hold the issue open. Holding issues open for
  unprovable proof is what drives an epic past the 100-child cap.
- **Deferred items are filed immediately.** The moment you write or think "out of scope",
  "for this PR", "pre-existing", "noted, non-blocking", "worked around", "minor", "we can
  address later" — stop, dup-scan, file it (type + `deferred` + `concern:*` or
  `documentation` + `area:*`, linking the discovering work), then continue. It lands in
  Triage; the orchestrator sequences it. Before pushing, grep your own diff and notes for
  those phrases. Agents chronically under-file: every such phrase in a subagent's report
  becomes an issue the orchestrator files before the next dispatch.
- **Events in comments; state in descriptions.** Epic bodies hold goal, scope, a design
  pointer and one Status line. What landed, what a review found, what was decided goes in
  the epic's comments ([references/templates/epic-event.md](references/templates/epic-event.md)).
  Milestone descriptions are rewritten wholly — never patched — and only when a story-wide
  assumption changes; the change goes in the dated decision ledger, which is never deleted.
- **Design lives in `docs/`.** Bodies point at docs; they do not copy them. Owner
  decisions (grill answers) are posted on the domain epic's thread.
- **Every body edit goes through a file.** Write the body to a file, `--body-file` it,
  verify the resulting length. Never process substitution — it has silently blanked bodies.
- **Record state before compaction.** If the context window is getting tight, write
  pending state (event comments, board moves, log lines) before doing anything else.
- **The board is only as honest as its last update.** Move columns at the moment the
  transition happens, by the role that owns the column. A stale board is a process defect:
  the moment sessions stop trusting it they collide again.
- **Quiet.** One line to chat on dispatch, merge, blocked-on-owner, escalation and
  validation result; a short summary every ten logged events; everything else goes to the
  log. `status` exists so you never have to narrate.

## Branches, worktrees, commits, PRs

- Branch per issue via `gh issue develop <N> --name <N>-<slug>` (native link; falls back
  to plain `git branch` when unavailable). Work in a **worktree** at the repo's documented
  worktree root — never in the main checkout when anything else may be running there.
  Keep the worktree until the PR merges (fix rounds need it); then remove it and `-D` the
  local branch (a squash merge leaves it unmerged).
- Commits: `AI:` prefix, one logical change each; squash on merge.
- PR title `<type>(<scope>): <description>` (≤70 chars, imperative, lowercase). Body per
  [references/templates/pr-body.md](references/templates/pr-body.md) — Summary, Risk,
  Rollback, Suggested Test Steps, Verified expectation. Mirror the issue's labels onto the
  PR. Draft PRs get no reviewer until un-drafted.
- Before opening: unit + integration suites and the linter, using the commands in
  `docs/process/testing.md` / `testing.local.md`; sanitization scan per the repo's rules.
- Regressions: same area → reopen the original with a `## Regression` comment and the
  `regression` label; different area → new bug with `regression`, citing the PR.

## Templates and formats

GitHub- and chat-facing bodies live in [references/templates/](references/templates/):
`issue-bug`, `issue-enhancement`, `issue-chore`, `issue-epic`, `issue-validation-epic`,
`milestone-description`, `epic-event`, `pr-body`, `fixes-applied`, `implementer`,
`fix-round`, `review-dispatch`, `validation-dispatch`, `brief`, `handoff`. Machine-read
records live in [references/formats/](references/formats/): `session-log`, `claim`,
`maintenance-report`, `validation-log`. Fill every section; the structure is what lets the
owner read every session the same way. Prose inside sections is yours.

Environment mapping (local `gh` vs GitHub MCP) is in
[references/github-tools.md](references/github-tools.md); observed failure modes worth
checking for are in [references/failure-modes.md](references/failure-modes.md).
