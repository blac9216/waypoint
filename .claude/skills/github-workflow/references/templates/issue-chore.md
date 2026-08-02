# Chore Issue Template

Use for dependency bumps, infrastructure changes, no-behavior-change refactors,
formatting passes, and other maintenance work. Apply the `chore` label at creation.

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
```
