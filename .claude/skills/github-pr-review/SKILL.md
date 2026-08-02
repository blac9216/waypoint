---
name: github-pr-review
description: Independent, contextless review of a pull request created via the github-workflow skill, in both the cloud sandbox (GitHub MCP tools) and locally (gh CLI). Use when reviewing a PR — verify the linked issue is solved, run all tests, check coverage, walk the suggested test steps, then request changes or squash-merge. You are the only party that lands the PR; the authoring conversation never merges its own work.
argument-hint: pr-number
---

# GitHub PR Review

An independent review of a pull request created through the [github-workflow](../github-workflow/SKILL.md) skill. This skill is meant to be run by a **subagent that has no context of how the PR was built**. Each review round starts fresh.

## Your Role

- You are a fresh reviewer. You did **not** write this PR and have no knowledge of the author's intent.
- The PR description, the linked issue, the diff, and the existing PR/issue comments are your **only** source of truth.
- Do not assume the author's choices were correct — verify them.
- **You are the only party that lands this PR.** The conversation that wrote the code does not approve or merge its own work — that separation is the rule that keeps everyone honest, and it holds because you and the author both follow it, not because of any tooling limitation. You record your verdict by posting the `## PR Review — …` comment and, on a clean review, performing the squash-merge yourself. The comment plus the merge are the approval of record; there is no separate formal "approve" step.
- **Never merge a PR that still has unresolved findings.** Merge only after a clean review.

## Environment: cloud sandbox vs. local

This skill runs in one of two environments and the operations below are written as actions, not raw commands:

- **Cloud sandbox** (default for this project's automated review — Claude Code on the web, CI): **no `gh` binary**; use the GitHub MCP tools (`mcp__github__*`) with explicit `owner`/`repo`.
- **Local**: the `gh` CLI.

Read the full mapping and its caveats once at the start: **[../github-workflow/references/github-tools.md](../github-workflow/references/github-tools.md)**. The operations this review needs:

| Operation | Local — `gh` | Cloud — GitHub MCP |
| --------- | ------------ | ------------------ |
| Read PR (title/body/refs) | `gh pr view <N> --json …` | `pull_request_read` `get` |
| Read PR diff | `gh pr diff <N>` | `pull_request_read` `get_diff` |
| PR CI / checks | `gh pr checks <N>` | `pull_request_read` `get_check_runs` |
| Read CI logs | `gh run view <id> --log` | `get_job_logs` (`failed_only`) |
| Read issue + comments | `gh issue view <N> --comments` | `issue_read` `get` / `get_comments` |
| Search issues (dup scan) | `gh issue list --search` | `search_issues` / `list_issues` |
| Comment on the PR | `gh pr comment <N>` | `add_issue_comment` (PR number) |
| Label the PR / issue | `gh pr edit --add-label` | `issue_write` `update` on the number |
| Secret scan | _(local scanner, e.g. gitleaks)_ | `run_secret_scanning` |
| Squash-merge | `gh pr merge <N> --squash` | `merge_pull_request` (`merge_method: squash`) |

## Time-Box

A single review round should not exceed ~10 minutes of wall-clock work. If you are spending materially longer than that, the PR is too large — go to the size sanity check in Step 3 and request decomposition instead of dragging the review out.

## Step 1 — Gather Context

Read everything before forming an opinion:

- Read the PR's title, body, head/base refs, and **all** comments (`pull_request_read` `get` + `get_comments`, or `gh pr view <N> --json title,body,headRefName,baseRefName,comments`). Prior review rounds and the author's "Fixes Applied" responses are the context this review builds on.
- Extract the linked issue from the PR body (`Closes #X`) and read it in full, including every comment (`issue_read`, or `gh issue view X --comments`). **If the issue links a parent epic (`Part of #<epic>`), read the epic too** — its Goal and Design tell you what the larger work is for, which is how you judge whether this slice actually fits.
- **Determine the review round.** Count existing `## PR Review — Changes Requested` and `## PR Review — Decomposition Requested` comments. This review is round `count + 1`. A round counts only when one of those comments was posted — pushes to the branch without a review comment do not increment the round.
- If 3 review rounds already exist, do not start a 4th — go straight to **Escalation** below.

## Step 2 — Check Out the PR

Work in a dedicated worktree so the author's worktree is never disturbed (`git` is the same in both environments):

```bash
git fetch origin
git worktree add /tmp/review-pr<N> origin/<headRefName>
cd /tmp/review-pr<N>
```

Remove it when the review is done: `git worktree remove /tmp/review-pr<N>`.

## Step 2.5 — Check CI Status First

Before running anything locally, check the PR's CI status (`pull_request_read` `get_check_runs`, or `gh pr checks <N>`):

- If CI is **failing**, read the failure logs first (`get_job_logs` with `failed_only: true`, or `gh run view <run-id> --log`). A real, reproducible CI failure is itself a blocker finding — don't waste time running locally before recording it.
- If CI is **green**, still run the suggested test steps locally — CI may not cover everything (especially network-gated or platform-specific paths).
- If CI is **missing** for a change that should have it (the project has a CI workflow and this PR did not trigger it), that itself is a finding.

## Step 3 — Review the Diff

Read every changed file (`pull_request_read` `get_diff`, or `gh pr diff <N>`). Check for:

- **Correctness** — does the code do what the issue requires, with no logic errors?
- **Security** — no injection, secret leakage, or unsafe input handling.
- **Conventions** — matches the surrounding codebase and project `CLAUDE.md`.
- **Scope** — no unrelated changes; no half-finished or dead code.
- **Tests** — new/changed behavior has matching tests.

### Size Sanity

If the diff is **> ~400 net LOC changed** or **touches > 15 files**, do not perform a full review. Post the `## PR Review — Decomposition Requested` template and stop. The author should split the work into smaller issues/PRs (and, if they haven't, open an epic to track them — see the github-workflow skill).

Exceptions where size is acceptable: pure renames, generated-file regenerations, lockfile updates, mechanical formatting passes. The diff in those cases is mechanically simple and review value lies in spot-checking, not line-by-line review.

### Style Guide Check

If the repository contains a `style-guide/` directory at the repo root, treat every `*.md` file inside as authoritative for the language(s) it covers. For each style-guide file relevant to the PR's diff (e.g., `powershell-style-guide.md` applies when the PR touches `*.ps1` / `*.psm1`; `python-style-guide.md` applies to `*.py`; etc. — match by language):

- **Read it before reviewing the diff.** Treat its rules as a first-class checklist.
- **Check every added or modified line** against each numbered rule. Pre-existing violations on lines the PR does NOT touch are out of scope; new violations the PR introduces are findings.
- **Severity follows the rule's intent.** A "must" / "always" / "do not" rule is generally a `major` finding; a "should" / "prefer" / "avoid" rule is generally `minor`. Use judgment — egregious violations of a "should" rule can be major; trivial drift on a "must" can be minor.
- **Cite the rule by section number** in the finding (e.g., "violates §3.1 — code at column 0 inside an `InModuleScope` block").

If `style-guide/` exists but no file matches the PR's language(s), that's not a finding — just note it in the summary.

### Secret Scanning

Scan the PR's changed content for secrets. In the cloud sandbox, pass the diff hunks / changed-file contents to the `run_secret_scanning` MCP tool (it scans content you provide, not a ref). Locally, run a secret scanner such as gitleaks over the diff. Any hit is a **blocker** finding. Beyond posting it in the review, the finding must require all three of:

1. **Remove the secret from the diff** (sanitize the file).
2. **Rewrite git history** to purge every commit that ever contained the secret — sanitizing the latest commit does NOT expunge the secret from history. Use `git filter-repo` (preferred) or interactive rebase, then force-push the rewritten branch (`git push --force-with-lease`). Every commit SHA on the branch will change; that's expected.
3. **Rotate the credential upstream** — revoke and regenerate the actual key/token at the issuing system. Once a secret has been pushed to a remote (even briefly), assume it is compromised. History rewriting hides it from future clones but does not unleak it.

The follow-up review round must verify all three were done. If a later round finds the secret still present in `git log -p` history on the head ref, that is itself a blocker and the round restarts.

## Step 4 — Verify Issue Resolution

Every requirement in the linked issue must be satisfied:

- **Enhancement / Chore issues:** each Acceptance Criteria checkbox is genuinely met.
- **Bug issues:** the described failure no longer reproduces and the root cause is addressed.

Record each requirement as met or unmet — an unmet requirement is a blocker finding.

## Step 5 — Run Tests

Run every test suite you can. If the project has a project-local testing skill, follow it.

**Unit tests** mock external dependencies; **integration tests** exercise real code against real dependencies (real network, filesystem, services, etc.). Both must pass. How the project separates them is up to its testing skill (if any), `CLAUDE.md`, or convention.

- **Unit tests** — must all pass.
- **Integration tests** — must all pass. Run them however the project specifies.
- **Lint** — run the project linter. Discover the command from the project's lint config, `CLAUDE.md`, or common manifests (`package.json`, `pyproject.toml`, etc.). Any new warning/error introduced by this PR is a finding.
- **Coverage** — generate a coverage report. The PR passes only if coverage **does not regress versus the base branch** AND is **at least 80%**. Compare against the base branch number, not the PR's own claim.

**Baseline coverage handling:** if the base branch's coverage is already below 80% (e.g., a greenfield repo just starting QA, or an early PR in a coverage-raising series), do not block on absolute coverage — only on regression. The 80% bar applies once the project crosses it. The first PR in such a series should generally include or be followed by a coverage-raising effort.

Any failing test, lint regression, or coverage regression is a blocker finding.

### Coverage Waiver

If the PR contains code that genuinely cannot be exercised in the CI environment (e.g., a PowerShell `if ($IsWindows)` branch, a Python `if sys.platform == 'win32'` branch, or a JavaScript `if (process.platform === 'win32')` branch, when CI runs on Linux), the author must include a `## Coverage Waiver` section in the PR body that lists:

- The specific file:line ranges being waived
- Why they cannot be covered
- How the behavior is verified manually instead

Verify the waiver is legitimate. Anything not in the waiver still counts against the 80% threshold. A missing or vague waiver for code below 80% is a finding.

## Step 6 — Walk the Suggested Test Steps

The PR body contains a **Suggested Test Steps** section specific to these changes. Execute each step exactly as written and compare against its expected result. Record every step as pass or fail. A failed step is a blocker finding.

If the PR has no Suggested Test Steps section, that itself is a finding — the author must add one.

## Step 7 — Verdict

**Decomposition Requested** if size sanity triggered in Step 3.

**Changes Requested** if any of these is true:
- A blocker or major finding in the diff.
- Secret scanning hit.
- Any unit, integration, or lint check fails.
- Coverage regressed or is below 80% with no valid waiver.
- Any issue requirement is unmet.
- Any suggested test step failed, or the section is missing.
- Required PR body sections (Summary, Risk, Rollback, Suggested Test Steps) are missing or unsubstantive.

Otherwise: **Approved**.

## Step 8 — File Deferred Items for Non-Blocking Findings

Before posting the Approval, Changes Requested, or Decomposition Requested template, **file a deferred issue for every finding you noted but did not require fixed in this PR**. This is the rule from the [github-workflow](../github-workflow/SKILL.md) skill's Deferred Items Rule, applied to your review.

What qualifies:

- Minor style-guide drift on lines the PR did not touch (out-of-scope per Step 3 but still real).
- New linter warnings you observed but did not block on.
- Pre-existing bugs, code smells, or unused code paths you happened to notice.
- Anything you described in your review as "minor", "noted, non-blocking", "out of scope", "pre-existing", "worth a follow-up", or similar.

If you wrote the phrase, you owe an issue.

**Before filing, scan for duplicates.** Don't open a second ticket for a problem that already has one — search open issues (`search_issues` / `list_issues` filtered by the `deferred` label, or `gh issue list --state open --search "<keywords>"`). If an open issue covers the same problem, add a comment on it referencing this PR as the rediscovery context and link to it in your review template's Deferred Items section instead of filing a new one.

**Filing.** Use the Bug or Enhancement template from the github-workflow skill ([../github-workflow/references/templates/](../github-workflow/references/templates/)), apply the `deferred` label plus the matching `concern:*` (or `documentation`), and reference the PR (`Found while reviewing PR #N`) in the Discovery / Motivation section. Each filed (or referenced existing) issue goes in the review template's `### Deferred Items` section so the audit trail is on the PR.

"Noted, non-blocking" is not a parking place — it either blocks the merge or it has an issue number next to it before the review template is posted.

## Verdict Templates

Post one of these as a comment on the PR. Only load the one matching your verdict:

| Verdict | Template |
| ------- | -------- |
| Changes Requested | [references/templates/changes-requested.md](references/templates/changes-requested.md) |
| Decomposition Requested | [references/templates/decomposition-requested.md](references/templates/decomposition-requested.md) |
| Approved | [references/templates/approved.md](references/templates/approved.md) |
| Escalation (cap reached) | [references/templates/escalation.md](references/templates/escalation.md) |

## If Changes Requested

1. Post the **Changes Requested** template as a comment on the PR.
2. Do **not** merge. Hand control back to the parent agent — the parent owns fixing the findings.
3. Remove your review worktree.

The parent will fix the findings, post a `## Fixes Applied` comment, and spawn a new contextless review agent for the next round.

## If Decomposition Requested

1. Post the **Decomposition Requested** template.
2. Do **not** merge. The parent must split the work into smaller issues/PRs.
3. This counts as a round (Step 1's round count includes it).
4. Remove your review worktree.

## If Approved

Squash-merge the PR yourself — this is the approval of record.

- **Cloud sandbox:** `merge_pull_request` with `merge_method: "squash"`. There is no MCP tool to delete the head branch and `merge_pull_request` has no delete-branch option, so rely on the repo's "automatically delete head branches" setting or leave the branch for cleanup — do not block the merge on branch deletion.
- **Local:** run the merge from a **neutral cwd** outside the repo (not the review worktree, not the author's worktree) and pass `--repo` explicitly, because `gh pr merge --delete-branch` runs a post-merge local-checkout step that fails when the cwd is inside a worktree whose base branch is checked out elsewhere:
  ```bash
  cd /tmp
  gh pr merge <N> --repo <owner>/<repo> --squash --delete-branch
  ```
  Then verify the remote branch is actually gone (`--delete-branch` is best-effort):
  ```bash
  BRANCH=<headRefName>
  if gh api "repos/<owner>/<repo>/branches/$BRANCH" >/dev/null 2>&1; then
      gh api -X DELETE "repos/<owner>/<repo>/git/refs/heads/$BRANCH"
  fi
  ```
  A `404` from the first call means the branch is already gone — that's the success case.

In both environments:
- If the merge is blocked by conflicts, merge the base branch into the PR branch, resolve, push, then retry.
- If a required check is failing, investigate and fix the cause — never bypass it.

After merging: post the **Approved** template as a comment, remove your review worktree, and hand back to the parent for cleanup (local worktree/branch removal).

## Escalation (cycle cap reached)

If 3 review rounds already exist and the PR is still not clean, do not loop further:

1. Post the **Escalation** template.
2. Apply the `help` label to both the issue and the PR (locally `gh issue edit X --add-label help` / `gh pr edit <N> --add-label help`; in the cloud, `issue_write` `update` with `labels` on each number).
3. Hand back to the parent — a human must take over.
