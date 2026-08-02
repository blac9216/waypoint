# Decomposition Requested Template

Post as a comment on the PR when the diff is too large for meaningful review
(> ~400 net LOC changed or > 15 files, outside the mechanical-change exceptions).
This counts as a review round. Do not merge — the parent must split the work.

```markdown
## PR Review — Decomposition Requested

**Round**: <N>
**Reviewer**: contextless review agent (github-pr-review skill)
**Verdict**: Decomposition requested — PR too large for meaningful review

### Size
- Files changed: <count> (threshold: ~15)
- Net LOC changed: <count> (threshold: ~400)

### Why this matters
Reviews on PRs this size become rubber-stamps. Smaller PRs improve review
quality, blast radius, and revertability.

### How to decompose
Suggested split (the author is free to choose a different decomposition):
- PR A: <slice>
- PR B: <slice>
- PR C: <slice>

Close this PR (or leave it open as draft) and open the smaller PRs as separate
issues per the github-workflow skill. If this is the first time the work has
proven too big to land in one PR, that is the signal to open an **epic** and file
the smaller slices as its sub-issues.

### Deferred Items
- #<n>: <short title> — filed during this review
- *(none)* if nothing was deferred
```
