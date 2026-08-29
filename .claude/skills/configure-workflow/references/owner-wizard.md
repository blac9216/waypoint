# Owner steps (things only the account owner can do)

Give the owner these, briefly, when the step comes up. Ask what is special about this
repository before running the scripts — a shared board or an org-owned Project changes
the parameters.

## Create the Project (once per board)
1. github.com → your profile → **Projects** → **New project** → *Table* → name it
   (repository name, or the shared name) → Create.
2. Project **⋯** → **Settings** → **Manage access** → invite the automation account →
   role **Admin**. (Or run `scripts/grant.sh` with your token once the Project exists.)
3. Tell the session the Project number (from the URL) and whether other repositories share it.

## Project workflows (UI only) — after `project.sh`
Project **⋯** → **Workflows**:
- **Auto-add to project** → repository = this repo, filter `is:issue is:open` → on.
  Shared board: one auto-add entry per repository.
- **Item added to project** → Status = **Triage** → on.
- **Item closed** → Status = **Done** → on.
- **Item reopened** → Status = **Triage** → on.
- Leave the pull-request and auto-archive workflows off.

## View group-by and sort (UI only) — from the checklist `project.sh` prints
Open the view → **▾** → *Group by* / *Sort by*. Typical: Board grouped by Status (board
column field); Roadmap grouped by Milestone; Ready queue and Triage sorted by Labels so
`priority:high` floats.

## Branch ruleset — `scripts/rulesets.sh`
Needs repo admin. Either let the session inject your token for that one command via the
`with-secrets` skill, or run it yourself:
`GH_TOKEN=<your token> scripts/rulesets.sh --repo <owner/name>`
(add `--reviewer-account` once a reviewer account exists, which raises required approvals to 1).

## Milestone due dates
Set a target date on every open story milestone so the Roadmap view shows markers;
closed stories get their close date.
