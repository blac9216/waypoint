# Bug Issue Template

Use when something is broken or not working as expected. Apply the `bug` label
plus a `severity:*` label and at least one `area:*` label at creation.

```markdown
## Description
What is broken and how it manifests. Include exact error messages or log output.

## Discovery
How and when this was discovered. What operation or test triggered it.
Include the sequence of events that led to finding the problem.

## Root Cause (if known)
Technical explanation of why it happens. If unknown, state what has been investigated so far.

## Affected Files
| File | Relevance |
| ---- | --------- |
| `path/to/file` | Why this file is involved |

## Impact
What is affected (features, output, user experience, other components).

## Possible Fixes
- Option A: Description of approach and trade-offs
- Option B: Alternative approach if applicable

## Done when
- [ ] The described failure no longer reproduces: <the exact command/test the reviewer runs>
- [ ] Root cause addressed, with a regression test

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
