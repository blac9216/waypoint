# Review Dispatch Template

Model: Opus, ALWAYS — every round, every PR, in every mode. A FRESH agent per round;
its only context is the PR and the repository. This is the kickoff for `github-pr-review`.

```text
Run the github-pr-review skill against PR #<P> in <owner>/<repo>.

Environment: <local (gh CLI) | cloud sandbox (GitHub MCP)>. Repo checkout <path>; the
author worktree <worktree root>/issue-<N> is NOT yours — use your own review worktree
under <worktree root>/review-pr<P> and remove it when done. <Exact test env: commands,
variables, isolation recipe, docker/host notes from docs/process/testing.md and
testing.local.md.> <Sanitization rule pointer.>
Board: set issue #<N> to **In review** now (<item-edit command>); on merge set
`Verified` to <n/a | pending-live> per the PR's Verified expectation (<field/option
ids>). Reviewer identity: <default auth | GH_TOKEN for the reviewer account via
<secrets mechanism>; never print it>.
HARD RULE: you may spawn helpers only on Haiku or a cheaper tier than your own (the
confidence scorer, the pre-flight); nothing else. Plain foreground calls only.
[Other live claims: <claim id> owns <area/PRs> — do not review or comment on its PRs;
if a concurrent merge conflicts this PR at verdict time, post the approval stating
that and hand back WITHOUT merging.]

This is review round <R> (prior rounds are the PR's `## PR Review — …` comments —
count them to confirm; ignore <non-round comments to exclude>).
[Round ≥2: round <R-1> requested: <findings>. The `## Fixes Applied` comment reports:
<fixes>. Round-1-clean areas carry forward only if their code is unchanged — verify the
delta is exactly the fix commits.]

You are a fresh, adversarial reviewer. Assume this PR has defects; your job is to find
them, and an approval must be earned with evidence. Your ONLY sources of truth are the
PR, the linked issue(s) (#<N> [+ epic #E]), the diff, and existing PR/issue comments.

Scrutiny points beyond the skill's attack list: (a) <the implementer's specific claims,
turned into checks>; (b) <blast-radius items: grants, invariants, race windows, contract
fidelity, honesty of Closes-vs-Refs>; (c) suites green when YOU run them (author claims
<numbers>), lint/build gates, sanitize scan, CI green, mergeable; walk the Suggested Test
Steps exactly as written.

Authority: you are the only party permitted to approve and merge this PR. Record your
verdict with the `## PR Review — …` comment [and the native review when the reviewer
account is configured]; on a clean review squash-merge yourself, tick the issue's
acceptance-criteria boxes, set `Verified`. [Post-merge in your authority when named:
close epic #<E> when its last child closes.]

When done, hand back with your verdict.
```

Scrutiny points are the highest-leverage text the orchestrator writes: derive them from
the implementer's report (each strong claim → a verification demand), the change's blast
radius, and the session's live defect classes.
