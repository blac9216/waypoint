# GitHub Tooling: Cloud Sandbox vs. Local

This workflow runs in one of two environments. The procedure is identical in both;
only the commands differ. Figure out which one you are in, then use that column for
every GitHub operation.

## Detect your environment

- **Local** — a `gh` binary is on `PATH` and authenticated (`gh auth status` succeeds).
  Use the `gh` CLI. It infers `owner/repo` from the current checkout.
- **Cloud sandbox** — the remote execution environment (Claude Code on the web,
  GitHub Actions, etc.). **There is no `gh` binary.** Use the GitHub MCP tools
  (`mcp__github__*`). Every call needs `owner` and `repo` passed explicitly (plus
  the issue/PR number where relevant) — nothing is inferred from a checkout.

If you are unsure, assume cloud sandbox and reach for the MCP tools — that is the
default for this project's automated work.

## Command ↔ tool mapping

| Operation | Local — `gh` | Cloud — GitHub MCP |
| --------- | ------------ | ------------------ |
| Read a PR (title, body, refs) | `gh pr view <N> --json title,body,headRefName,baseRefName` | `pull_request_read` method `get` |
| Read a PR's diff | `gh pr diff <N>` | `pull_request_read` method `get_diff` |
| PR CI / checks status | `gh pr checks <N>` | `pull_request_read` method `get_check_runs` (or `get_status`) |
| Read PR comments | `gh pr view <N> --json comments` | `pull_request_read` method `get_comments` |
| Read PR review threads | `gh api .../pulls/<N>/comments` | `pull_request_read` method `get_review_comments` |
| Create a PR | `gh pr create --title … --body-file …` | `create_pull_request` |
| Mark a PR ready (un-draft) | `gh pr ready <N>` | `update_pull_request` with `draft: false` |
| Edit PR title/body/base | `gh pr edit <N> --title/--body` | `update_pull_request` |
| **Label a PR** | `gh pr edit <N> --add-label <l>` | `issue_write` method `update`, `issue_number: <N>`, `labels: […]` — see caveat |
| Squash-merge a PR | `gh pr merge <N> --squash --delete-branch` | `merge_pull_request` with `merge_method: "squash"` — see caveat |
| Read an issue + comments | `gh issue view <N> --comments` | `issue_read` method `get`, then `get_comments` |
| Search / list issues (dup scan) | `gh issue list --state open --search "<kw>"` | `search_issues` (query syntax) or `list_issues` (`labels`, `state` filters) |
| Create an issue | `gh issue create --title … --body … --label …` | `issue_write` method `create` |
| Update an issue (label / close / assign) | `gh issue edit <N> --add-label …`, `gh issue close <N>` | `issue_write` method `update` (`labels`, `state`, `assignees`) |
| Comment on an issue or a PR | `gh issue comment <N>`, `gh pr comment <N>` | `add_issue_comment` (`issue_number` accepts a PR number too) |
| Link a sub-issue to a parent (epic) | `gh api repos/{owner}/{repo}/issues/<parent>/sub_issues -F sub_issue_id=<id>` | `sub_issue_write` method `add` (parent `issue_number` + `sub_issue_id`) — see caveat |
| List an epic's sub-issues | `gh api repos/{owner}/{repo}/issues/<parent>/sub_issues` | `issue_read` method `get_sub_issues` |
| Check whether a label exists | `gh label list` | `get_label` (404 ⇒ does not exist) |
| Read CI run logs | `gh run view <run-id> --log` | `get_job_logs` (`failed_only: true` for a run) or `actions_get` |
| Secret scan a diff/file | _(no first-class command)_ | `run_secret_scanning` |

## Caveats the mapping cannot paper over

1. **PR labels go through the issues API.** `update_pull_request` has **no** labels
   field. A PR is an issue under the hood, so set its labels with `issue_write`
   (`method: update`, `issue_number` = the PR number, `labels: […]`). Same for the
   `help` label during escalation.

2. **No MCP tool deletes a branch.** `merge_pull_request` has no delete-branch
   option and there is no `delete_branch` MCP tool. In the cloud sandbox, rely on
   the repo's "automatically delete head branches" setting, or leave the stale
   branch for later cleanup — do not block a merge on it. Locally, `gh pr merge
   --delete-branch` (and the `gh api -X DELETE …/git/refs/heads/<branch>` verify
   step) still apply.

3. **There is no formal "approve" step, by design.** The contextless reviewer
   records its verdict by posting the `## PR Review — …` comment (via
   `add_issue_comment` / `gh pr comment`) and, on a clean review, performing the
   squash-merge. The comment plus the merge ARE the approval of record. Do not
   reach for a formal review-approve API — the merge is what counts, and routing
   approval through the merge is what enforces "only the contextless reviewer
   lands a PR." This is policy, not a tooling limitation.

4. **Arbitrary `gh api` calls have no generic MCP equivalent.** Where the local
   flow shells out to `gh api`, find the specific MCP tool for that operation (the
   table above covers the ones this workflow needs). If none exists, treat it as a
   local-only step and note it.

5. **No MCP tool creates or edits a label.** `get_label` only checks whether a
   label exists; there is no cloud tool to create one or change its color.
   Provisioning the canonical label set (github-workflow → "Provisioning the
   labels is a hard gate") is therefore local-only (`gh label create` / `gh label
   edit`) or a human action in the cloud sandbox. If a required label is missing
   or mis-colored and you cannot fix it, stop and ask the user — do not proceed
   with a substitute or no label.

6. **`sub_issue_id` is the issue's internal ID, not its number.** Both
   `sub_issue_write` and the `gh api …/sub_issues` body take the child issue's
   database **`id`** (read it from `issue_read` `get` on the child — the `id`
   field), not its `#number`. Mixing them up links the wrong issue or errors.

## Additions for the four-layer shape

| Operation | Local — `gh` | Cloud — GitHub MCP |
| --------- | ------------ | ------------------ |
| Read board items / fields | `gh project item-list <N> --owner <o> --format json` (filter or paginate — large boards burn API points); field ids via `gh project field-list` | _(no first-class tool — treat as local-only; the orchestrator runs locally)_ |
| Move a column / set a field | `gh project item-edit --project-id <pid> --id <item> --field-id <fid> --single-select-option-id <oid>` (text fields: `--text`) | local-only |
| Add an issue to the board | `gh project item-add <N> --owner <o> --url <issue url>` (auto-add covers new issues) | local-only |
| Milestone create / edit description | `gh api repos/{o}/{r}/milestones -f title=… -F description=@file` / `-X PATCH …/milestones/<n>` | local-only |
| Assign a milestone | `gh issue edit <N> --milestone "<title>"` | `issue_write` `update` (`milestone`) |
| Move a sub-issue to another parent | `gh api -X POST repos/{o}/{r}/issues/<new parent>/sub_issues -F sub_issue_id=<id> -F replace_parent=true` | `sub_issue_write` `add` with `replace_parent` |
| Dependencies (blocked by) | `gh api repos/{o}/{r}/issues/<N>/dependencies/blocked_by` (GET / POST `-F issue_id=<id>`) | local-only |
| Close with a reason | `gh issue close <N> --reason completed|"not planned"`; duplicate via `gh api -X PATCH …/issues/<N> -f state=closed -f state_reason=duplicate` | `issue_write` `update` (`state`, `state_reason`) |
| Linked branch | `gh issue develop <N> --name <branch>` | local-only |
| Native review (second account) | `GH_TOKEN=<reviewer token> gh pr review <P> --approve|--request-changes --body-file …` | `create_pending_pull_request_review` + `submit_pending_pull_request_review` under the reviewer identity |
| Project status update (brief, optional) | `gh api graphql` `createProjectV2StatusUpdate` | local-only |

Caveat 7: **an account cannot review its own PR** (approve or request changes → 422).
Native reviews therefore need a second account; with one account the verdict of record
stays the `## PR Review — …` comment plus the merge. Caveat 8: **`gh auth switch` is
global** — never switch accounts mid-session; give the reviewer its identity via
`GH_TOKEN` in its own process.
