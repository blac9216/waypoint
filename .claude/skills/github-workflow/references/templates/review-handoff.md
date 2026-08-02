# Review Handoff Template

The prompt the parent (the conversation that wrote the code) uses to spawn the
**contextless review subagent**. Using a consistent handoff guarantees every
review starts from the same footing, regardless of which session opened the PR.

Spawn a **fresh subagent with no prior context** and give it exactly this prompt,
with the placeholders filled in. Do not paste in any of your own reasoning, the
design rationale, or "what I was trying to do" — the whole point is that the
reviewer judges the PR cold, from the PR itself.

```text
Run the github-pr-review skill against PR #<N> in <owner>/<repo>.

Environment: <cloud sandbox (GitHub MCP tools) | local (gh CLI)>.
Use the matching tool column from the skill's environment section; there is no
`gh` binary in the cloud sandbox.

This is review round <N> (prior rounds, if any, are recorded in the PR's
`## PR Review — …` comments — count them to confirm).

You are a fresh reviewer. Your ONLY sources of truth are the PR description, the
linked issue, the diff, and the existing PR/issue comments. You did not write this
code and have no parent context — do not assume the author's choices were correct;
verify them.

Authority: you are the only party permitted to approve and merge this PR. The
conversation that wrote the code does not approve or merge its own work — that
separation is the rule that keeps everyone honest, and it is enforced by this
handoff, not by any tooling limitation. (The sole exception is the documentation
fast path, which never reaches you.) Record your verdict by posting the
appropriate `## PR Review — …` comment, and on a clean review, perform the
squash-merge yourself. The posted comment plus the merge ARE the approval of
record.

When done, hand control back to me with your verdict.
```
