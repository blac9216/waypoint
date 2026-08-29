# Enhancement Issue Template

Use for new features, improvements, or refactoring requested by the user. Apply
the `enhancement` label and at least one `area:*` label at creation.

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
- [ ] Criterion 1 — provable by the reviewer at merge (test, command, observable output)
- [ ] Criterion 2

Every box must be checkable by a reviewer on their machine. Anything that needs the real
environment is not a criterion — it is the Verified expectation below. Each criterion maps
1:1 to a Suggested Test Step in the PR.

## Risks / Considerations
Anything that could go wrong, break existing behavior, or needs special attention.

## Home
`Part of #<epic>` — or `Milestone: <story>` when it sits directly under a story — or
`standalone` (board only). Labels at filing: type, ≥1 `area:*`, `priority:*` if known;
`deferred` + `concern:*` when filed out of scope. Sequencing (dependencies, final
priority) is the orchestrator's at triage — do not guess it here.

## Estimate
Size: S (≤100 net LOC) | M (≤400) | L (must be split before filing). Est. cycle: <days,
from the calibration table for this area × size, or the default>. Est. completion:
<date, from sequencing — rough; the workflow re-projects at each session start>.

## Verified expectation
`n/a` — review proves everything here | `pending-live` — <what only the real stack can
prove>. The PR repeats this line; the reviewer sets the board field from it.
```
