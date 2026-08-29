# Brief Template

Produced on `status` (work continues) and `good morning` (dispatch frozen, in-flight
drained). Built from the session log (`jq`) and the board — not from memory.

```markdown
# Brief — <target> — <UTC timestamp> — <status | good morning>

**Headline**: one line on where the target stands. [good morning: "dispatch frozen,
in-flight drained".]

**Orchestrator's read**: a short paragraph — where this session feels it is versus
where it started. Opinion, deliberately; the sections below are the facts.

## Merged since last brief
| Issue | PR | Rounds | Verified |

## In flight
| Issue | Stage | Round | Agent state (running / reporting / stalled) |

## Blocked on you
- #N — the question — **recommendation**: …

## Validation
pending-live: <count> · last run: <date, result> · open validation epic: #V or none ·
next trigger check: <which trigger, when>

## Drift and anomalies
Rule-audit findings, stale claims taken over, host pressure, stalls and resumes.

## Metrics
Agents dispatched by role/model · review rounds per PR (mean, max) · first-pass approval
rate · stalls · wall-clock since session start · tokens by role.

## Proposed next dispatch
What starts if you say go, in order, with why.
```
