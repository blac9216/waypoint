# What the repository must provide

This skill is the same in every repository. Everything that differs lives in the repo:

## `docs/process/` (committed)

| File | Holds | Read at |
|---|---|---|
| `work-tracking.md` | the four-layer shape as adopted here, any local rule deviations, Project number/ids | Orient |
| `labels.md` | the closed `area:*` set and any extra label values; the canonical base set is provisioned by the configure-workflow skill | Triage, filing |
| `testing.md` | unit/integration/lint/coverage commands with environment; isolation recipe for shared hosts; sanitize scan | dispatch prompts, PR prep |
| `validation.md` | what "live" means here, thresholds (pending-live count), the zero-workaround bar specifics, evidence locations | Validate |
| `maintenance.md` | worktree root, scratch dir, allowed file locations, test resource prefixes, host thresholds, shared sequence resources (e.g. migration numbering) | Maintenance, dispatch |
| `overnight.md` | standing limits for unattended runs (read-only systems, scope ceilings) | Overnight |
| `failure-modes.md` | repo-specific observed failure modes (this skill's [failure-modes.md](failure-modes.md) holds only general ones) | Orient |

A missing file is a gap: file a `documentation` issue with `area:` of the process and use
the most conservative reading meanwhile. Do not put repo specifics into the skill.

## `*.local.md` (never committed)

Environment-specific and sensitive-adjacent guidance: live-lab inventories by pointer,
credential mechanism pointers, host quirks, which systems may be touched today. The
repo's `.gitignore` carries the `*.local.md` rule. Read every one at Orient; treat
their content as the owner's standing instructions for this machine.

## Fixtures the configure-workflow skill provisions

Label set with canonical colours; the Project (fields `Status`, `Verified`, `Claimed by`;
the standard views; automations auto-add → Triage, closed → Done, reopened → Triage);
milestone due dates for the roadmap; the `docs/process/` skeleton; the `.gitignore` rule;
the machine account's `project` scope and admin grant; optionally a second reviewer
account and its token in the secrets mechanism (enables native reviews — see
`github-pr-review`).
