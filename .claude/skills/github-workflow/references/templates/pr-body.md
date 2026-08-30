# PR Body Template

The **Suggested Test Steps**, **Risk**, and **Rollback** sections are required. If
the PR is part of an epic, reference it (`Part of #<epic>`) below the `Closes` line. Closing keywords go on their
own line at the top — never inside prose, where they close the wrong things at merge.

Closing keywords must live in the PR **body** — GitHub only links issues from keywords
in the body, not from commit messages, even when those commits are on the PR's branch.
For multi-issue PRs, put every `Closes #<issue>` on its own line. This matters most at
merge: on squash, GitHub composes the commit message per the repository's
default-commit-message setting (PR title / title+description / title+commit details) and can
silently drop closing keywords that only existed in individual commits, closing fewer
issues than intended (or none). Before merging, verify `closingIssuesReferences` on the
PR matches every issue you intend to close, and pass the PR body explicitly via
`--body-file` to the squash so the keywords survive.

```markdown
Closes #<issue>
Closes #<issue2>   <!-- one line per issue for multi-issue PRs -->

## Summary
What changed and why.

## Risk
What could regress, what areas are touched indirectly, blast radius.
Be honest — "no risk" is rarely true.

## Rollback
How to revert if this introduces a regression. Usually `git revert <sha>`,
but flag any state migrations, schema changes, or destructive operations that
complicate a clean revert.

## Suggested Test Steps
Concrete, change-specific steps a fresh reviewer can follow to validate this PR.
Each step must be reproducible and state its expected result. For enhancements,
align these 1:1 with the issue's Acceptance Criteria.

1. <step> — expected: <result>
2. <step> — expected: <result>

## Verified expectation
`n/a` | `pending-live` — <what only the real stack can prove>. Copied from the issue;
the reviewer sets the board's `Verified` field from this line at merge.
```
