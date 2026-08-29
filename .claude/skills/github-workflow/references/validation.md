# Live validation

Review proves that code does what the issue says on the reviewer's machine. Validation
proves the story works on the real stack. It is a **defined loop with its own epic**, not
an afterthought at the end of a story.

## When

You decide when to start a loop, but at each of these moments you must consider it and
log the decision either way (`validation-considered` event with the reason):

1. An epic's runnable slice is complete (its execution-path issues are merged).
2. The number of `pending-live` merges since the last run exceeds the repo's threshold
   (`docs/process/validation.md`; default 5).
3. A fix-wave from the previous validation has fully merged.
4. The owner asks for "morning cleanup" and anything is `pending-live`.

The morning brief shows the `pending-live` count so avoidance is visible.

## The loop

1. **Open a validation epic** from
   [templates/issue-validation-epic.md](templates/issue-validation-epic.md) under the same
   milestone as the epic being validated (milestone-less if that epic is). It names the
   goal statements to prove, the steps, the issues whose `Verified` it will decide, and
   the zero-workaround bar. Label `epic` + `area:*` of the validated domain.
2. **Dispatch the validation agent**
   ([templates/validation-dispatch.md](templates/validation-dispatch.md)): fresh stack,
   real credentials via the repo's secrets mechanism, the repo's isolation recipe, sanitized
   step log in scratch ([formats/validation-log.md](formats/validation-log.md)).
3. **Findings**: the agent dup-scans, files sanitized bugs as children of the validation
   epic (they land in Triage), flips `Verified` per issue (`live-verified` /
   `live-failed`), and posts a step summary on the validation epic.
4. **Fix-wave**: you triage the new bugs (they are Ready by definition — they gate the
   proof), dispatch them through the normal review loop grouped by disjoint area.
5. **Re-run** with the **zero-workaround bar**: no manual grants, no `docker cp`, no
   overrides beyond documented operator configuration, no restarts. Any step that still
   needs a workaround is a FAIL with a new bug.
6. **Green** → the validation agent posts the summary on the *validated* epic (what
   flipped, what failed and where the bugs went), closes the validation epic with reason
   `completed`; you rewrite the milestone's *Current state* wholly.

Expect the first run of a loop to find real bugs — that is what it is for. A validation
that finds nothing on the first pass is examined for what it did not exercise.

## Authority

The validation agent files, flips and summarises. It never fixes, never dispatches, never
closes anything but its own validation epic. Evidence containing lab identifiers stays in
scratch; if any lands in the repo tree, moving it out is the first thing that happens.
