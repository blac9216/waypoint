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
