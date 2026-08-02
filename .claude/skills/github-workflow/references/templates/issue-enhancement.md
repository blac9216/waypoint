# Enhancement Issue Template

Use for new features, improvements, or refactoring requested by the user. Apply
the `enhancement` label at creation.

Keep each enhancement scoped so its PR lands within the review budget (≤ ~400 net
LOC / ≤ 15 files). If the work is bigger than that, it belongs under an **epic** —
see `issue-epic.md` and split it into several right-sized enhancement issues.

```markdown
## Summary
What is being added or changed and why.

## Motivation
Why this enhancement is needed. What problem it solves or what capability it adds.
Include user request context if applicable.

## Current Behavior
How things work today (if applicable). What is missing or insufficient.

## Proposed Changes
- Change 1: Description and rationale
- Change 2: Description and rationale

## Affected Files
| File | Relevance |
| ---- | --------- |
| `path/to/file` | Why this file is involved |

## Acceptance Criteria
- [ ] Criterion 1
- [ ] Criterion 2

(Each acceptance criterion should map 1:1 to a Suggested Test Step in the PR
body — keep the wording aligned so reviewers can verify them directly.)

## Risks / Considerations
Anything that could go wrong, break existing behavior, or needs special attention.

## Epic
Link the parent epic issue if this is part of multi-issue work (`Part of #<epic>`),
or state "standalone" if it is not.
```
