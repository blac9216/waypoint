# Overnight, status, and the morning ritual

## The session log (always on)

From step 0, every session appends one JSON line per event to the log file in the
session's scratch directory ([formats/session-log.md](formats/session-log.md)): dispatch,
agent report, review round and verdict, fix round, merge, stall, resume, escalation,
validation considered/started/result, maintenance pass, claim change; per agent: model,
tokens, wall-clock. Cheap to write, and the only source for the metrics in a brief. `jq`
aggregates it; do not re-read it into context to count things.

## Quiet mode

Chat gets one line on: dispatch, merge, blocked-on-owner, escalation, validation result.
Every ten logged events, a three-line summary. Nothing else. The owner reads the brief,
not a narration.

## Overnight

- Create an hourly cron whose prompt is the single word `heartbeat`. It exists so a
  usage-limit interruption resumes itself; it does no work. Delete it at handoff or when
  the owner ends the session.
- Owner-gated decisions: label `help`, comment options + recommendation, route around
  them; the brief lists them first.
- Honour the repo's standing overnight limits from `docs/process` (read-only lab, scope
  ceilings, no destructive operations).
- On resume after an interruption: Orient again from GitHub, verify every in-flight
  agent's worktree state before resuming it, then continue.

## `status`

Produce the brief ([templates/brief.md](templates/brief.md)) from the log and the board.
**Do not stop or slow any work.** The owner may then choose to run the ritual below or
say nothing and let the session continue.

## `good morning`

1. **Freeze**: no new dispatches. In-flight agents finish their current step (a review
   round completes, a fix lands); nothing new starts.
2. **Drain**: wait for in-flight reports; update the board and log as they arrive.
3. **Brief**: the full template, with "dispatch frozen, in-flight drained" in the headline.
4. **Ritual**: the owner answers the open questions and corrects drift; apply each
   correction on GitHub as it is given (comment, re-label, move, rewrite).
5. The owner may then say **`morning cleanup`**: consider validation (start a loop if a
   trigger holds), run the full maintenance pass, report the validation status, and ask:
   **handoff or continue?**
6. **Handoff** → [templates/handoff.md](templates/handoff.md) in chat, release claims,
   delete the heartbeat, write memory. **Continue** → resume dispatch from the proposed
   next dispatch in the brief.
