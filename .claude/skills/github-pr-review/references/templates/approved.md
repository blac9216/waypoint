# Review Approved Template

Post as a comment on the PR after a clean review **and** after performing the
squash-merge yourself. The posted comment plus the merge are the approval of
record — there is no separate formal "approve" step. Then hand back to the parent
for worktree/branch cleanup.

```markdown
## PR Review — Approved

**Round**: <N>
**Reviewer**: contextless review agent (github-pr-review skill)
**Verdict**: Approved — merged

### Issue Coverage
All requirements from #X verified resolved.

### Test Results
- CI: green
- Unit tests: pass — <counts>
- Integration tests: pass — <counts>
- Lint: clean
- Coverage: <N>% (base <M>%) — no regression, at or above 80%
- Secret scan: clean
- Suggested test steps: all <Y> passed

### Deferred Items
- #<n>: <short title> — filed during this review
- *(none)* if nothing was deferred

Squash-merged; remote branch deletion verified. Handing back to parent for cleanup.
```
