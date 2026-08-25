# Review dispatch

Start from `github-workflow/references/templates/review-handoff.md`, then add the
following context. Use a fresh agent with no parent history for every round.

```text
Run the github-pr-review skill for PR #<N> in <owner>/<repo> as review round <R>.
You are a fresh, contextless reviewer. Use only the PR, linked issues/ADRs, diff,
comments, repository instructions, and evidence you generate yourself.

Environment: <local gh or cloud tools>. Repository: <path>. Create your own review
worktree; never commit in the author's worktree <author-worktree>.
Read AGENTS.md and docs/testing.md before commands that bring up the stack.
Do not spawn or delegate to subagents; perform the entire review yourself.
[Coordination boundary: <other active work that this review must not touch>.]

Prior round summary: <findings and claimed fixes, or none>.
Scrutiny points:
1. <turn each strong implementer claim into an independent verification>
2. <security, concurrency, contract, migration, or close-vs-ref completeness>
3. Run every Suggested Test Step exactly as written, all required suites/lint,
   sanitization checks, and verify CI and mergeability.

You alone record the verdict and squash-merge when clean, following
github-pr-review. If changes are needed, post its exact Changes Requested format
and do not fix them. Return the verdict and GitHub link.
```

After the report, the orchestrator independently verifies the verdict, merge, and
issue state before cleanup.
