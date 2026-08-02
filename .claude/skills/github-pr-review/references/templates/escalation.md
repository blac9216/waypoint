# Escalation Template

Post as a comment on the PR when 3 review rounds have completed and the PR is
still not clean. Apply the `help` label to the PR and its issue, then hand back to
the parent — a human must take over.

```markdown
## PR Review — Escalated

**Round**: 4 (cycle cap reached)
**Verdict**: Blocked — human review needed

After 3 review cycles the PR still has unresolved findings. `help` label applied.

### Outstanding Findings
- <finding>

### What was tried across rounds
- Round 1: <summary>
- Round 2: <summary>
- Round 3: <summary>
```
