# Fix-round dispatch

```text
Fix review round <R> for PR #<N> in <owner>/<repo>. Read AGENTS.md,
github-workflow, the PR, and the complete `## PR Review — Changes Requested`
comment. Work only in the existing author worktree <worktree> on <branch>.

Do not spawn or delegate to subagents. Do not review, merge, broaden scope, or
touch the main checkout.

Resolve every finding:
1. <finding and expected invariant>
2. <finding and expected invariant>

Run exactly:
<test/lint commands and required environment>

Sanitize the diff, rebase on origin/main if needed, push safely, and post the
github-workflow `## Fixes Applied` comment mapping each finding to its fix and
evidence. Return the commit, exact results, comment link, and a no-subagents-used
confirmation. The orchestrator will dispatch a fresh reviewer.
```

If unmerged history must be rewritten, the orchestrator must explicitly authorize
the exact operation and require proof that the before/after diff is identical.
