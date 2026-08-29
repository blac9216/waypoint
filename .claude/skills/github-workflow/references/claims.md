# Claims

A claim tells every other session *who holds this work right now*. Assignees cannot do
this job: automation shares one GitHub account, so two orchestrators look identical.
The claim lives in the board's `Claimed by` text field and is the only coordination
signal the workflow trusts.

## Format

`<repo-slug>-<NN> @ <ISO-8601 UTC>` — e.g. `waypoint-03 @ 2026-08-29T15:10Z`.
Schema in [formats/claim.md](formats/claim.md).

## Taking an id at session start

1. Read every `Claimed by` value on the board (one query; filter to non-empty).
2. A claim is **stale** when its timestamp is more than 24 hours old **and** the claimed
   item has had no PR, commit or comment activity since that timestamp. Both conditions —
   an overnight session that is quietly merging is not stale.
3. Take the lowest number not held by a live claim. Record it in the session log.

## Granularity

- Orchestrated: claim the **epic**. Its issues inherit; nobody else works inside a claimed
  epic. A milestone target claims each epic as you start it, not all at once.
- Solo on a standalone issue: claim the issue.
- Refresh the timestamp whenever you merge something or start a wave, so the stale rule
  reads activity honestly.

## Collisions and takeovers

- A live claim by another id means the work is theirs. Do not touch its issues, PRs or
  file areas. Report the collision in chat (one line) and pick the next target or ask.
- A stale claim may be taken over: write your id, then post an event comment on the epic
  naming the superseded claim and its timestamp, so a resuming session sees it was
  superseded rather than discovering silently that its work moved.

## Releasing

Clear `Claimed by` on handoff, on `good morning` when the owner ends the session, and when
the epic closes. A claim you forget to release costs the next session a day (the stale
window) — releasing is part of finishing, not housekeeping.
