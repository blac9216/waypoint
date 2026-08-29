# Execution and orchestration

Read this at step 5. Solo mode uses the same loop with this session playing the
implementer and fix-round roles; the reviewer is a fresh agent in every mode.

## Division of labour

| Party | Model | May | May not |
|---|---|---|---|
| Orchestrator (this session) | — | dispatch/resume agents; triage, sequence and home issues; move Triage/Backlog/Ready; set and release claims; file issues agents left unfiled; body/doc-only fixes to its own side's PRs; run maintenance; rewrite milestone state | implement, review or merge non-trivial work; decide genuine design questions |
| Implementer | Sonnet | implement ONE issue (or one named cohesive group) in its own worktree; open the PR; file deferred issues; set **In progress** at start | spawn subagents; review; merge; touch the main checkout; decide design |
| Fix-round agent | Opus | fix exactly the review findings in the author worktree; post Fixes Applied; set **In progress** at start | spawn subagents; merge; expand scope |
| Reviewer | Opus, fresh every round | everything in `github-pr-review`: set **In review** at start, verdict, squash-merge, tick ACs, set `Verified`, close-outs named in its handoff; may spawn **Haiku or cheaper** helpers only | be reused across rounds; spawn its own tier or above |
| Validation agent | Opus | run a real stack; file bugs into the validation epic; flip `Verified` per issue; post summaries; close the validation epic when green | fix anything; dispatch anything |

Genuine design decisions go to the owner: label `help`, comment the options with a
recommendation, keep independent work moving. Owner-permission blockers (branch rules,
CI scope) get `help` too — surface, do not retry. Always set `model` explicitly on
dispatch; inheriting the session model has silently put implementers on the premium tier.

## Column ownership (one owner each)

| Transition | Owner |
|---|---|
| Triage → Backlog / Ready; Backlog → Ready | orchestrator (during triage) |
| → In progress | implementer or fix-round agent, at its start — in solo mode that is **you**, the moment you start implementing or fixing |
| → In review | reviewer, at its start, every round |
| → Done | board automation on close |
| `Verified` at merge (`n/a` / `pending-live`) | reviewer |
| `Verified` → `live-verified` / `live-failed` | validation agent |

Nobody sets a column they do not own. If a transition is missing, the owning role's
prompt is missing the instruction — fix the prompt, not the board.

## The loop, per issue

1. **Pick** the next issue: `blocked by` dependencies first, then `priority:*`, then age.
   Re-read the epic body; confirm the issue still fits; note anything a recent merge
   changed about it (comment on the issue if its scope moved).
2. **Dispatch** the implementer ([templates/implementer.md](templates/implementer.md))
   in the background, or implement yourself in solo mode — setting the issue to
   **In progress** yourself before the first commit. Log the dispatch.
3. **On the report**: verify a PR actually exists and is not draft; labels mirrored; file
   every deferred finding the agent *mentioned* but did not file (dup-scan first); copy the
   agent's strong claims into the reviewer's scrutiny points.
4. **Dispatch the reviewer** ([templates/review-dispatch.md](templates/review-dispatch.md)).
   Round *n* = number of `## PR Review — Changes Requested` / `Decomposition Requested`
   comments + 1, counted from the PR, never from memory.
5. **Changes Requested** → dispatch a fix-round agent into the author worktree
   ([templates/fix-round.md](templates/fix-round.md)); body/doc-only findings you may fix
   yourself (run every Suggested Test Step you write before posting). Then a **fresh**
   reviewer. Three rounds, then the reviewer escalates with `help` and you stop.
6. **Merged** → confirm on GitHub (reviewer reports occasionally outpace it): issue
   closed, branch gone, `Verified` set. Remove the worktree, `-D` the local branch,
   `pull --ff-only`. Post the **event comment** on the epic
   ([templates/epic-event.md](templates/epic-event.md)) — what landed, drift from the
   design, findings worth remembering. Re-check whether the merge invalidates any in-flight
   sibling; a conflicting PR gets a rebase directive to its author agent before review.
7. **Epic children all closed** → the last reviewer closes the epic (name it in the
   handoff); you verify and post the closing event; rewrite the milestone's *Current
   state* wholly if the story's state changed.

## Dispatch prompts

Every subagent prompt contains, in this order and without softening:

- The **no-subagents rule** ("the Agent/Task tools are off-limits — do every step
  yourself; confirm no-subagents in your final report") — except the reviewer's
  Haiku-or-cheaper allowance.
- The repository's **sanitization / public-repo rule** as pointed to by `docs/process`.
- The **worktree path and branch** it owns, and the main checkout it must not touch.
- The **claim id** and the areas/issues other agents hold ("a parallel agent owns
  `area:frontend` (#N) — do not touch it; keep shared-file edits additive").
- The **exact test, lint and sanitize commands with environment** from
  `docs/process/testing.md` and `testing.local.md` — copied in, not referenced, because
  agents do not discover context reliably.
- The **token-discipline** paragraph from the `local-model-delegate` skill when the
  repo uses it.
- The board transition it owns, and the board/field ids it needs.
- "Plain foreground calls only; no background waits" — the dominant stall is an agent
  waiting for a notification that never comes.

Scale prompts, not trust: put the sharp questions in the **reviewer's** prompt (scrutiny
points derived from the implementer's claims, the change's blast radius, and the session's
live defect classes) and treat the implementer's report as unverified until the reviewer
or the repo confirms it.

## Parallel safety

- Parallelise only work with **disjoint surfaces**. `area:*` is the first filter (two
  issues sharing an area are serialised unless you have checked their file sets); the
  file sets named in the issues are the second; anything touching a shared sequence
  resource (numbered migrations, generated ledgers, lockfiles — the repo names them in
  `docs/process`) is the third.
- Pre-assign shared sequence slots across your own agents; when another live claim
  exists, downgrade to "verify at branch time and state the assumption in the PR body".
  Whichever PR merges second renumbers and reconciles.
- Name the other in-flight agents and their areas in every prompt.
- Serialise anything sharing a screen, module or subsystem. Two agents in one module
  produce a merge conflict and a review that has to reason about both.

## Resuming agents

An interrupted or stalled agent is resumed with an explicit next-step list. Check the
worktree first (`git status`, `git log origin/main..HEAD`) so the directive matches
reality; if the agent died on a usage limit, its work is uncommitted in the worktree and
step 1 is "commit the WIP". Tell implementers to commit early rather than hold one large
uncommitted change.
