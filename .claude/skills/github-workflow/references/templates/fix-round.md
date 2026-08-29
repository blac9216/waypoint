# Fix-Round Dispatch Template

Model: Opus. Works in the EXISTING author worktree so branch/PR continuity holds.

```text
You are a fix-round subagent for PR #<P> in <owner>/<repo> (<sanitization rule
pointer>). Work in the existing author worktree <worktree root>/issue-<N> (branch
<branch>). HARD RULE: the Agent/Task tools are OFF-LIMITS — do everything yourself;
plain foreground calls only. Do NOT review/merge/touch the main checkout. Be terse.
Board: ensure #<N> is assigned to the acting account and set it to **In progress** now (<item-edit command>).

Read the round-<R> `## PR Review — Changes Requested` comment and fix EVERY finding:
1. <finding → the fix expected, incl. the class-killing guard when the finding is an
   instance of a known class>
2. …

Then: <full suites + lint with environment>; sanitize scan; `git fetch origin && git
rebase origin/main` if main moved (resolve <expected areas> additively; verify two-dot
`git diff origin/main..HEAD --diff-filter=D --stat` is EMPTY — no stray deletions); push
(`--force-with-lease` only if rebased); post a `## Fixes Applied` comment per the
fixes-applied template with finding → fix → evidence.

Final message: fixes, test counts, no-subagents confirmation, comment link. Do not
merge; the next reviewer is dispatched by the orchestrator.
```

If a finding needs branch-history surgery (e.g. a secret annotation must be in the
introducing commit), the orchestrator explicitly authorises a non-interactive collapse
of the UNMERGED branch (`git reset --soft $(git merge-base origin/main HEAD)` +
recommit) with a before/after `git diff origin/main...HEAD | sha256sum` identity proof.
Never `git rebase -i`; never rewrite anything merged.
