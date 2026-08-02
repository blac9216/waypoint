# PR Body Template

The **Suggested Test Steps**, **Risk**, and **Rollback** sections are required. If
the PR is part of an epic, reference it (`Part of #<epic>`) below the `Closes` line.

```markdown
Closes #<issue>

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
```
