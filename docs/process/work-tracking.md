# Work tracking — the four-layer shape

Adopted 2026-08-29. This is how work is organised on GitHub for this repository; the
`github-workflow` skill implements the mechanics. Every platform constraint below was
verified against the API.

## The four layers

Ordered by containment. The numbering is not a sequence of steps.

| # | Layer | What it is | What it holds |
|---|---|---|---|
| 1 | **Project** ([Waypoint, #5](https://github.com/users/blac9216/projects/5)) | One per repository. The aggregate view and **the one guaranteed home of every issue**. | Every open issue; closed ones stay in *Done* as the verification ledger. Custom fields (state *about* an item). |
| 2 | **Milestone** | A multi-epic **delivery story** (e.g. *Compliance parity*). Optional — only feature stories get one. | Domain epics and the standalone issues that belong to the story. Its description is the rolled-up state a newcomer reads first. |
| 3 | **Epic** | One domain inside a story, or a milestone-less theme (e.g. #770 hardening). An issue with native sub-issues, always more than one. | The domain's goal and scope in its body; the **event stream** (what landed, what a review found, decisions) in its comment thread. |
| 4 | **Issue** | The work. | Sits under an epic when it groups with related work; otherwise directly on the board (with or without a milestone). |

Hardening, CI, tooling and flake work is deliberately **milestone-less**: it is
sequenced by priority as capacity allows, independent of feature timelines.

## Platform constraints these rules come from

- A parent issue accepts at most **100 sub-issues**; closed children still occupy
  slots. This is why the hierarchy exists — one epic cannot hold a whole story.
- A milestone has an editable description (20,000+ characters) but **no comment
  thread**. Milestones carry state; events go in epic or issue comments.
- An issue belongs to **at most one milestone**. Cut stories so work does not straddle.
- A Project is owned by the account, **not the repository**, and holds only items
  explicitly added — the auto-add workflow is what keeps it complete.

## Board fields

Only two. Every field is something a human or the orchestrator must keep honest.

**Status** — the Kanban column.

| Column | Meaning |
|---|---|
| Triage | Newly filed and not yet sequenced. The orchestrator drains this column. |
| Backlog | Intentionally shelved: a record of intent, not sequenced into any milestone. |
| Ready | Sequenced (milestone and/or epic) and ready to dispatch. |
| In progress | Being worked, including fix rounds. |
| In review | PR open; contextless review in flight. |
| Done | Closed. See *Verified* for outstanding proof. |

**Verified** — whether a merged change has been proven on the real stack.

| Value | Meaning |
|---|---|
| n/a | No proof beyond CI is needed (docs, refactors, unit-only changes). |
| pending-live | Merged; live-lab proof still outstanding. |
| live-verified | Proven by a validation run on real infrastructure. |
| live-failed | Validation found it broken; a bug is filed. |

Unit, integration and synthetic end-to-end tests gate the merge and are therefore
implied by *Done*; *Verified* tracks only post-merge live proof.

## Rules

- **Acceptance criteria must be provable when the PR merges.** An issue closes at
  merge. Proof that review cannot supply (a live vCenter, real vendor tooling, a
  third-party system) becomes `Verified: pending-live` on the board, never a reason to
  hold the issue open. Holding issues open for unprovable proof is what drove an epic
  past the 100-child cap.
- **Milestones hold state; epics hold events.** A milestone description is
  edit-in-place, so it is rewritten **wholly, never patched** — patched status
  sections drift into self-contradiction. The orchestrator owns milestone
  descriptions and edits them rarely: when a story-wide assumption changes, the
  change is recorded in the description's dated *Decision ledger*, which is never
  deleted.
- **Design lives in `docs/`**, not in issue bodies. Milestone descriptions point at
  the docs; owner decision records (grill answers) live on the domain epic's thread.
- **Every open issue has exactly one home** — the board — and appears there through
  the auto-add workflow. After any reorganisation, audit: zero open issues off the
  board, no epic with fewer than two children, no open child of a closed parent.
- **Labels classify; fields track state.** `deferred` is provenance — "an agent filed
  this as out of scope during other work" — and stays on the issue for good.
  `backlog` mirrors the Backlog column. `help` means the owner is needed. The label
  catalogue in the `github-workflow` skill remains the closed set.
- **A stale board is a process defect**, not a cosmetic one. Concurrent sessions
  avoid colliding by trusting it; the moment it is not maintained they collide again.

## Milestone description template

```
## Goal
## Scope / Non-goals
## Design            — pointers into docs/, never copies
## Epics             — one line each, with state
## Current state     — rolled-up; rewritten wholly
## Decision ledger   — dated, oldest first, only story-wide assumption changes; never deleted
## Records           — links: design-record epics, grill threads, validation summaries
```

## Ownership

| Layer | Created by | Maintained by |
|---|---|---|
| Project | Account owner, once | Automation — items, fields, board state |
| Milestone | Automation | Automation — description is the rolled-up context |
| Epic | Automation | Automation — body and comment thread |
| Issue | Anyone | Whoever holds the work |

## Former milestone numbering

Earlier revisions of `roadmap.md` numbered the stories M0–M7. The mapping is kept in
[`../adr/README.md`](../adr/README.md#former-milestone-numbering) because accepted ADRs
still use those labels and are immutable.
