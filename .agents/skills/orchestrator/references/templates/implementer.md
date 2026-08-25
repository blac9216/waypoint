# Implementer dispatch

Fill every placeholder and remove inapplicable bracketed text.

```text
Implement GitHub issue #<N> in <owner>/<repo>, a public repository at <path>.
Read AGENTS.md, the full issue, and the github-workflow skill before editing.
Sanitization is absolute: use invented fixtures and never expose private lab or
entitlement data.

You own only <worktree> on branch <branch>. Do not touch the main checkout.
Do not spawn or delegate to subagents; do every step yourself and confirm that in
your final report. Do not review or merge.

Context to read: <epic, ADRs, prior PRs, and relevant code>.
Scope: <concrete deliverables and explicit sibling-owned boundaries>.
[Coordination: <agent/task> owns <files/area>; do not touch them.]
[Migration reservation: <slots>; re-check migrations and open PRs before use.]

Verification (run exactly):
<commands and required environment setup from docs/testing.md>

Follow github-workflow for the issue worktree, AI-prefixed commits, sanitization
self-scan, and PR body. Rebase on origin/main before push. Open a non-draft PR,
mirror issue labels, and use Closes #<N> only if every acceptance criterion is met.

Return: PR link, concise summary, design decisions, exact test results, deferred
issues filed after duplicate search, and a no-subagents-used confirmation. Keep
the worktree for review.
```

The orchestrator verifies the PR exists, is ready, has matching labels, and records
every unfiled follow-up before dispatching review.
