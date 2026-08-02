# Fixes Applied Template

Post on the PR after addressing a round of review findings, then spawn a fresh
contextless review agent for the next round.

```markdown
## Fixes Applied — Round <N>

Responding to the round <N> review findings above.

### Changes Made
| Finding # | Resolution | Commit |
| --------- | ---------- | ------ |
| 1 | What was changed | `abc1234` |

### Verification
- Unit tests: pass — <counts>
- Integration tests: pass — <counts>
- Lint: clean
- Coverage: <N>%

Ready for re-review.
```
