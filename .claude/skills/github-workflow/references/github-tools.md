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
| Squash-merge a PR | `gh pr merge <N> --squash --delete-branch --body-file <file>` (keeps `Closes` lines; see github-pr-review "If Approved") | `merge_pull_request` with `merge_method: "squash"` — see caveat |
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

## Scripts — home-only, not in this repo checkout (#1854)

The skill's helper scripts (`post-comment.sh`, `check-manifest.sh`,
`check-test-steps.sh`, `preflight.sh`, `stamp-claim.sh`, `home-deferred.sh`,
`batch-deferred.sh`, `board-audit.sh`, `save-log.sh`, `stall-check.sh`, and the
`scripts/lib/` helpers they source) exist only in each host's home-directory skill
install, e.g. `/home/vscode/.claude/skills/github-workflow/scripts/` — **verified**:
`git log --all -- .claude/skills/github-workflow/scripts` on this repo is empty, and
`.claude/skills/github-workflow/` here contains only `SKILL.md` and `references/`.
Any instruction that names a repo-relative path
(`.claude/skills/github-workflow/scripts/<name>.sh`) does not resolve in this
checkout — invoke the script by its **absolute** home path instead:

```
bash /home/vscode/.claude/skills/github-workflow/scripts/post-comment.sh <issue-or-pr> <body-file>
```

**Cloud sandbox / any environment without that home install**: fall back to the `gh`
or MCP equivalent named for the operation elsewhere in this file — for posting a
comment specifically, `gh pr comment <N> --repo <owner>/<repo> --body-file <file>`
(compose the body in a file first; never `gh … --body "@<path>"`, which posts the
literal path string rather than the file's contents).

**Why not sync `scripts/` into the repo instead** (the alternative this issue
weighed): the skill's own regression suite (`tests/test_agent_rules_drift.sh`,
`tests/test_rule_pointer_drift.sh`, `tests/test_stamp_claim.sh`) cross-checks the
scripts against files that do not exist anywhere in this repo either —
`references/agent-rules.md`, `references/templates/session-card.md`,
`configure-workflow/manifests/family.json`, and the `.claude/agents/workflow-*.md`
subagent definitions (an entire top-level directory this repo has never had). Copying
only `scripts/` and `tests/` in without those made the suite fail on first run
(`test_agent_rules_drift: FAILED`, `test_rule_pointer_drift: FAILED`,
`test_stamp_claim: FAILED` — a pre-existing `stamp-claim.sh` vs `claims.md`
exit-code-contract drift surfaced in that last one, independent of the sync question).
Landing a working sync therefore means bringing all of that across at once, which is a
change far larger than one repo-relative-path fix — it is exactly the byte-for-byte,
owner-authorized fast-path sync **#1538** already exists to do (whole family +
`.claude/agents/workflow-*.md` from `storage@main`, self-merge after its own drift
suites go green), not a scope this issue should reach for on its own. This is why
**option 2** (correct the paths, document the gap) was chosen over
**option 1** (sync `scripts/` in) for #1854, even though option 1 was the issue's own
recommendation — the recommendation predated running the suite against a real sync
attempt.

**CI-job scope**: the two required checks named `shellcheck .claude/skills` and
`test .claude/skills` (`.github/workflows/skills-shellcheck.yml`) run
`find .claude/skills -type f -name "*.sh"` / `find .claude/skills -type f -path
"*/tests/*.sh"` — genuine, unfiltered path scans, not scoped to a hardcoded skill list.
Counted against the tree (not inferred), that is **18** `.sh` files in six installed
skills, and the split matters:

| Skill | `.sh` files | Linted by `shellcheck .claude/skills` | Executed by `test .claude/skills` |
| --- | --- | --- | --- |
| `configure-workflow` | 8 (`scripts/{_lib,audit,capture,grant,labels,process-docs,project,rulesets}.sh`) | yes — all 8 | no — ships no `tests/` dir |
| `design-docs` | 6 (3 `scripts/`, 3 `tests/`) | yes — all 6 | yes — its 3 `tests/*.sh` |
| `plan-work` | 4 (2 `scripts/`, 2 `tests/`) | yes — all 4 | yes — its 2 `tests/*.sh` |
| `github-workflow` | 0 | n/a | n/a |
| `github-pr-review` | 0 | n/a | n/a |
| `interrogate` | 0 | n/a | n/a |

So `shellcheck .claude/skills` really does check 18 files (at `-S warning`, per #1235)
and `test .claude/skills` really does run 5 test files — `configure-workflow`'s 8
scripts are linted and passing, they are simply never *executed*, because the
"scripts are mentioned in a test" soft gate is deliberately scoped to skills that
already have a `tests/` dir (#1349). The three skills that report **success** without
anything of theirs being checked are the three that currently ship **zero** `.sh` files
in this repo: `github-workflow`, `github-pr-review` and `interrogate`. Reading either
job's green status as coverage of *those* three skills' scripts — including every
script this section says lives only in the home install — is exactly the misreading
this note exists to foreclose.

**This skill's own repo copy is stale relative to the executing copy.** The gap is not
limited to `scripts/`: this repo's `.claude/skills/github-workflow/SKILL.md` is 182
lines against the home install's 331, and the diff runs 205 added / 56 removed lines.
Whole concepts that the workflow *as actually executed* depends on are absent from the
repo copy — the thirteen-item startup checklist and its `startup-item` /
`startup-complete` session-log events, the `readiness-gate` step, the `Unit:`
deferred-batching marker rule, and every `batch-deferred` reference (each of these
greps to 0 hits in the repo copy and 1+ in the home copy). A reader who trusts the repo
copy is reading an older process. Read the home install when the two disagree, and
track the fix at **#1538**, which owns the byte-for-byte sync and now carries the
measured drift — do not attempt a partial sync here (see the paragraph above: #1854
tried it and it fails CI).
