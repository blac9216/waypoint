# Maintenance

A fixed step, not a mood. It runs at session start, on every resume, before each
parallel wave or every three serial issues, and on "morning cleanup" — never on the
heartbeat, which only keeps the session alive. Each pass writes one
[formats/maintenance-report.md](formats/maintenance-report.md) record to the session log
so the brief can show what was found. Five steps, in order.

## 1. Triage drain

The Triage column is the intake for everything anyone filed since the last pass —
reviewer and implementer deferrals, validation bugs, owner-filed issues. The filer set
basic labels; the filer cannot decide precedence. For each item:

- **Dup-scan** open issues; a duplicate is closed with reason `duplicate` and a pointer.
- **Complete the labels**: exactly one type; severity on bugs; `concern:*` if it is a
  found-issue; at least one `area:*` from the repo's closed set (`docs/process/labels.md`).
- **Home it**: sub-issue of the epic it belongs to, or milestone directly, or neither if it
  is a standalone theme. Check the target epic's child count.
- **Sequence it**: `priority:*`; native `blocked by` links where order matters; fold
  small items into the in-flight issue whose natural home they are (say so on both).
- **Move it**: Ready if sequenced into live work; Backlog if intentionally shelved (add the
  `backlog` label; it carries no milestone).

Live-test blockers that gate the active epic's proof are active work by definition —
they go straight to Ready under that epic, whatever label they arrived with.

## 2. Host audit

Read CPU load, memory and disk free; list processes older than the session that look like
test leftovers (containers, runners, servers, stray node/dotnet processes). Compare disk
usage to the previous pass — a large jump is a signal to investigate before continuing,
not a number to log. Thresholds are the repo's (`docs/process/maintenance.md` if it
exists); absent that, use judgment and say what you used.

## 3. Cleanup

Remove what tests left behind: containers, images, volumes and networks with the repo's
test prefix, temp worktrees from finished reviews, untracked garbage in the repo tree
(`git status --porcelain` outside the allowed scratch locations). Never a blanket prune —
other sessions share the host. List what you removed in the report.

## 4. Rule audit

The rules agents are most likely to break silently:

- Anyone working in the **main checkout** (`git status` there should be clean and on
  main; a dirty main checkout is an agent that skipped its worktree).
- Files written **outside the allowed locations** (repo tree, the documented worktree
  root, the documented scratch dir).
- Evidence or captures containing anything the repo's sanitization rule forbids, sitting
  inside the repo tree even untracked.
- Agents that spawned subagents against instruction (their reports will say so if asked;
  a suspiciously fast large diff is the tell).

Findings become issues or resume directives; a repeated finding becomes a
[failure-modes.md](failure-modes.md) entry.

## 5. State audit

The board is the coordination surface, so its lies are expensive. Check:

- Every open issue is on the board; none is In progress / In review without a live PR or
  agent; nothing is Done while open.
- Claims: yours is current; others' are live or stale (take over per
  [claims.md](claims.md)).
- Epics: every open epic has >1 child, is under the 100-child cap, and its parent (if any)
  is open; no open child of a closed parent.
- Milestone descriptions still describe reality; if a story-wide assumption changed since
  the last rewrite, rewrite the *Current state* section wholly now and add a decision
  ledger entry.
- `pending-live` count — feeds the validation trigger check
  ([validation.md](validation.md)).
