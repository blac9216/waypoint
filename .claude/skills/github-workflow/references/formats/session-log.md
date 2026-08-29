# Session log format

Append-only JSON lines at `<scratch>/session.jsonl`, from step 0, in every mode.
One object per event. `jq` aggregates; the model does not re-read the file to count.

Required keys: `ts` (ISO-8601 UTC), `event`, `claim`. Optional keys by event:

| event | keys |
|---|---|
| `session-start` | `target`, `mode`, `horizon`, `main_sha` |
| `claim` | `action` (take/refresh/takeover/release), `item`, `superseded` |
| `dispatch` | `role` (implementer/fix/reviewer/validation/helper), `model`, `issue`, `pr`, `agent` |
| `report` | `role`, `agent`, `issue`, `pr`, `tokens`, `duration_s`, `outcome` |
| `review` | `pr`, `round`, `verdict` (approved/changes/decomposition/escalated), `findings`, `agent` |
| `merge` | `pr`, `issue`, `rounds`, `verified` |
| `board` | `issue`, `from`, `to`, `by` |
| `stall` / `resume` | `agent`, `reason` |
| `escalation` | `issue`, `pr`, `reason` |
| `validation-considered` | `trigger`, `decision`, `reason` |
| `validation` | `epic`, `run`, `result` (pass/fail counts), `bugs` |
| `maintenance` | the maintenance-report object |
| `brief` | `kind` (status/morning), `path` |
| `note` | `text` — free text, sparingly |

Example:
`{"ts":"2026-08-29T15:10:00Z","event":"dispatch","claim":"waypoint-03","role":"implementer","model":"sonnet","issue":1140,"agent":"a1b2"}`
