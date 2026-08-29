# Observed failure modes (general)

All observed in real sessions; the maintenance rule audit looks for the first four.
Repo-specific ones belong in the repo's `docs/process/failure-modes.md`.

- **Implementers writing into the main checkout** before creating their worktree. A
  dirty main checkout is the tell; a report that "smells off" is the prompt to check.
- **Agents spawning subagents** despite the rule. Order full disclosure and personal
  verification of the delegated diff; tell the reviewer to treat it as unverified
  first-draft code.
- **Stalled agents "waiting for a notification"** that never comes — implementers and
  reviewers alike. Resume with: the notification will never arrive; plain foreground calls
  only; here are the remaining steps.
- **Usage-limit death** mid-task: the report is the limit message, the work is
  uncommitted in the worktree. Resume the same agent; step 1 is "commit the WIP".
- **Model inheritance**: omitting `model` on dispatch puts every agent on the session's
  tier. Always set it.
- **Body edits via process substitution** blank the body while printing a success URL.
  File + `--body-file` + length check, always. A script that asserts before writing can
  abort silently after a successful-looking run — grep to confirm the edit landed.
- **Status sections rot** under incremental patching into self-contradiction. Rewrite
  wholly.
- **Stale-base branches**: a rebase-less branch whose diff "deletes" files that landed
  on main since. Require two-dot `git diff origin/main..HEAD --diff-filter=D --stat` to be
  empty and `merge-base --is-ancestor` true before review; reviewers rebase, never merge
  stale.
- **Reviewer over-deference to design framing**: a capability loss waved through as
  "by design" — re-check the cited design yourself; non-goals get stretched.
- **Reviewers merge-then-comment**: a merged PR with no verdict for a minute is the
  ordering race, not a violation.
- **Closing keywords in prose** (`closes #N` in a sentence) close the wrong things at
  merge. Keywords only on their own line at the top of the body.
- **Agents under-file deferrals**: every "worth a follow-up" in a report is an unfiled
  issue until the orchestrator files it.
- **Board listing at scale** burns API points fast; filter or paginate rather than
  listing everything on each check.
