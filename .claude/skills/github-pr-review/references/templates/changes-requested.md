# Review Findings (Changes Requested) Template

Post as a comment on the PR when the verdict is Changes Requested. Then hand
control back to the parent — the parent owns fixing the findings.

```markdown
## PR Review — Changes Requested

**Round**: <N>
**Reviewer**: contextless review agent (github-pr-review skill)
**Verdict**: Changes requested

### Summary
One paragraph: what was reviewed and the overall state.

### Issue Coverage
| Requirement (from #X) | Status | Notes |
| --------------------- | ------ | ----- |
| <criterion> | met / unmet / pending-live | ... (ticked on the issue when met) |

### Findings — Spec (does not do what was asked / does more)
| # | Severity | Confidence | Location (permalink) | Problem | Required change |
| - | -------- | ---------- | -------------------- | ------- | --------------- |
| 1 | blocker / major / minor | ≥80 | `https://github.com/…/blob/<sha>/path#L…-L…` | ... | ... |

### Findings — Standards (how it is written)
| # | Severity | Confidence | Location (permalink) | Problem | Required change |
| - | -------- | ---------- | -------------------- | ------- | --------------- |

### Attack list
| Probe | Result |
| ----- | ------ |
| Spec fidelity / scope | probed / n-a / not probed (why) |
| Correctness | |
| Silent failures | |
| Behavioural tests | |
| Concurrency / ordering | |
| Security surface | |
| Drift guards | |
| History (blame, prior PRs) | |
| Comments match code | |
| Standards & smells | |

### Notes (not required — confidence < 80)
- ...

### Test Results
- CI: <status>
- Unit tests: <pass/fail — counts>
- Integration tests: <pass/fail — counts>
- Lint: <clean / regressed — details>
- Coverage: <N>% (base <M>%) — <regression? meets 80%? waiver?>
- Secret scan: <clean / hits>
- Suggested test steps: <X of Y passed>

### Required Before Merge
- [ ] <finding 1>
- [ ] <finding 2>

### Deferred Items
- #<n>: <short title> — filed during this review
- *(none)* if nothing was deferred
```
