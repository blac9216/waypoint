# Reviewer account (optional)

GitHub refuses reviews on your own pull request, so with a single automation account the
review verdict is a comment plus the merge. A second account makes reviews **native**:
Approve / Request changes, a ruleset that requires one approval, and auto-merge.

## Set up
1. Create a GitHub account for the reviewer (e.g. `<machine>-reviewer`). Enable 2FA.
2. Create a fine-grained or classic PAT for it with `repo` and `project` scopes.
3. Store the PAT in the owner's secrets mechanism (the `with-secrets` skill's inventory
   names the entry; do not hard-code the name anywhere in scripts).
4. Grant it: `scripts/grant.sh … --reviewer <login>` (collaborator write + Project admin).
5. Re-run `scripts/rulesets.sh --reviewer-account` so the ruleset requires one approval.
6. Record in `docs/process/work-tracking.md`:
   `Reviewer identity: <login> via GH_TOKEN (with-secrets entry <name>); native reviews required.`

## How it is used
Reviewer agents receive the token as `GH_TOKEN` in their own process — never `gh auth
switch`, which is global and races with other agents. `github-pr-review` submits the native
review and still posts the `## PR Review — …` comment with the detail.

## Skipping it
Write `Reviewer identity: none — single account; the review comment plus the merge are the
verdict of record` into `docs/process/work-tracking.md`. The audit reports which mode is in
force; the reviewer skill falls back accordingly.
