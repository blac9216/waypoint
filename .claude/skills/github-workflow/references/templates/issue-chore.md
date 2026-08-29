# Chore Issue Template

Use for dependency bumps, infrastructure changes, no-behavior-change refactors,
formatting passes, and other maintenance work. Apply the `chore` label and at least one `area:*` label at creation.

```markdown
## Summary
What is being changed and why. Keep this short — chores should be small.

## Type
- [ ] Dependency update
- [ ] Refactor (no behavior change)
- [ ] Build / CI / tooling
- [ ] Formatting / lint
- [ ] Documentation
- [ ] Other (specify)

## Affected Files
| File | Relevance |
| ---- | --------- |
| `path/to/file` | Why this file is involved |

## Verification
How to confirm the chore changed only what was intended (e.g., behavior tests
still pass, dependency lockfile diff matches expected upgrade, formatter output
is clean).

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
