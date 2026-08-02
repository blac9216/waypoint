---
name: github-workflow
description: GitHub issue-driven workflow for tracking all work — issues, epics, branches, commits, and PRs — across both the cloud sandbox (GitHub MCP tools) and local development (gh CLI). Use whenever creating or updating issues or epics, planning a multi-issue feature, branching, committing, opening a PR, handing a PR off for review, or picking work back up in a fresh session. Consult it before writing any code so the work is tracked from the very start.
argument-hint: issue
---

# GitHub Issue Workflow

This defines how Claude Code agents track work using GitHub issues. All work follows an issue-first workflow, and any work too large for a single review-sized PR is tracked under an **epic** so the larger goal survives across sessions and context compaction.

## Environment: cloud sandbox vs. local

Every GitHub operation in this skill is written as an *action* ("create the issue", "label the PR"), not as a raw command, because the command depends on where you are running:

- **Cloud sandbox** (the default for this project's automated work — Claude Code on the web, CI): **no `gh` binary**. Use the GitHub MCP tools (`mcp__github__*`), passing `owner`/`repo` explicitly on every call.
- **Local**: the `gh` CLI, which infers `owner/repo` from the checkout.

The full command ↔ tool mapping, plus three caveats that bite in the cloud (PR labels go through the issues API, no MCP tool deletes a branch, and there is no formal "approve" step), lives in **[references/github-tools.md](references/github-tools.md)**. Read it once at the start of GitHub work in a new environment. The essentials:

| Operation | Local — `gh` | Cloud — GitHub MCP |
| --------- | ------------ | ------------------ |
| Create / update an issue | `gh issue create` / `gh issue edit` | `issue_write` (`create` / `update`) |
| Comment on an issue or PR | `gh issue comment` / `gh pr comment` | `add_issue_comment` |
| Search issues (dup scan) | `gh issue list --search` | `search_issues` / `list_issues` |
| Create a PR | `gh pr create` | `create_pull_request` |
| Label a PR | `gh pr edit --add-label` | `issue_write` `update` on the PR number |
| Squash-merge a PR | `gh pr merge --squash` | `merge_pull_request` (`merge_method: squash`) |

(`git` itself — branches, commits, worktrees, push — is identical in both environments. Only GitHub API operations differ.)

## Issue-First Rule

Create a GitHub issue **immediately before writing any code**. No code changes without a corresponding issue. This applies to both bugs discovered during testing and enhancements/features requested by the user.

**Assignment.** As soon as you start work on an issue, assign it to yourself (or to the agent's unique identifier — e.g., the session/branch name). Always check the assignee before starting a new issue: if it's already assigned and the assignee is active, pick a different issue. This is how concurrent agents avoid duplicating work.

## Epics

An **epic** is a meta-issue that tracks one larger goal delivered across **more than one** issue and PR. It exists for a specific failure mode: long features outlive a single context window, and when the window compacts the *details* survive in the diff but the *larger intent* — why we're doing this, what's done, what's left, how the plan has drifted — evaporates. The epic is the durable home for that intent. Chat memory and summaries are not; the epic issue is.

**When to open one.** The moment a feature is too big to land in one review-sized PR (≤ ~400 net LOC / ≤ 15 files), it needs an epic. If you're about to file a second issue that only makes sense as part of the same larger goal, that goal is an epic — open it first. An epic always has more than one sub-issue; a "one sub-issue epic" is just an issue.

**Shape.** Use [references/templates/issue-epic.md](references/templates/issue-epic.md) and apply the `epic` label. The body is a **living spec**: a Goal, the Design/Approach (edited in place as decisions change), the **sub-issues linked natively** (GitHub then auto-tracks the parent's progress — not a manual checklist), a **Trajectory Log** (each closed sub-issue's PR + any design drift), and a one-line Status. Treat the first draft as a starting point, not a contract.

**Keep it current — this is the part that matters.** An epic that isn't maintained is worse than none, because it lies to the next session. Two mandatory update points:

- **Before** you start work on any sub-issue: re-read the epic in full, confirm the sub-issue still fits the goal, and update Status to show it's in flight.
- **After** a sub-issue closes: GitHub updates the parent's progress bar automatically, so just append a Trajectory Log entry — which issue/PR landed and **what changed from the original design**, if anything. Then **re-evaluate the whole epic's trajectory**: does the remaining plan still make sense given what just shipped? If not, edit the Design section and update any still-open sub-issues whose scope or approach the change affects (comment on them, or revise their bodies). Catching drift here is the entire point — a stale open sub-issue is how a session three windows from now builds the wrong thing.

## Plan the work, then file it all — before starting

When a discussion lands on a path forward, do not start coding the first piece. First **decompose the entire effort**: break it into an epic (or epics) plus right-sized issues — each scoped to land in a PR the review agent will accept (≤ ~400 net LOC / ≤ 15 files, see the size sanity check in the github-pr-review skill). Then **file all of it** — every epic and every issue — *before* writing any code. Filing first means the plan exists somewhere durable the instant it's agreed, not locked in a context window that may compact mid-build. Tag the issues you won't start right away with `backlog`, and give each a `priority:*` so the sequence you reasoned out lives on the issues themselves rather than in memory.

**Mind the context window.** All the epics and issues for a given discussion must be recorded before that window compacts — otherwise the decomposition you just reasoned through is lost and the next session re-derives it (differently). If a planning discussion is large and you can see the remaining turns getting tight, **say so**: tell the user you're approaching the point where unrecorded plan items are at risk, and file what's decided before continuing. Recording the plan beats polishing it.

## Starting a session / picking up after compaction

When this skill loads in a fresh context window and the work is part of an epic, **familiarize yourself with the entire epic and all its associated sub-issues before doing anything else** — read the epic body (Goal, Design, Trajectory Log, Status) and read each open sub-issue (enumerate them from the native sub-issue list — `issue_read` `get_sub_issues`, or `gh api …/sub_issues`). This is the durable channel the previous session left for you; reconstruct the larger picture from it rather than inferring intent from the diff. Only once you hold the whole epic in view should you start (or resume) a sub-issue — and per the rule above, update the epic's Status before you do.

## Deferred Items Rule

When you notice a real bug, code smell, or improvement opportunity that you are not addressing in the current work, file a deferred issue immediately. **"Out of scope" is not a parking place — it is a signal to file.**

**Trigger phrases.** If you find yourself writing, saying, or thinking any of these while implementing, reviewing, or testing — stop and file before continuing:

- "out of scope" / "for this PR" / "in another PR"
- "pre-existing"
- "noted but not fixed" / "noted, non-blocking"
- "worked around" / "minor"
- "we can address later" / "won't fix here"

**Before filing, scan for duplicates.** A new issue for a problem that is already tracked dilutes the backlog and splits the conversation. Search open issues for the same keywords first (locally `gh issue list --state open --search "<keywords>"`; in the cloud sandbox `search_issues` / `list_issues` filtered by the `deferred` label — see references/github-tools.md). If you find an existing issue, comment there with the new context (which PR or workflow surfaced it again, anything new about the symptom or repro) and link it from your in-progress work. Do not open a duplicate.

**Filing.** Use the Bug or Enhancement template per nature, apply the `deferred` label plus the `concern:*` that matches the finding (or `documentation` for a docs gap), and link the discovering PR or work in the Discovery / Motivation section. Then continue with the in-scope work.

**Before pushing a PR, run a self-scan.** Grep your own diff and notes for the trigger phrases above. Every match is either an inline fix or a deferred issue — nothing leaves the PR tagged "out of scope" without a record.

```bash
# Self-scan: look at your own diff and any notes for trigger phrases.
git diff main...HEAD | grep -iE 'out.of.scope|pre.existing|noted|worked.around|address.later|won.t fix'
```

## Issue Templates

One template per issue kind, kept as separate files so only the one you need is loaded:

| Kind | Template | When |
| ---- | -------- | ---- |
| Bug | [references/templates/issue-bug.md](references/templates/issue-bug.md) | Something is broken or misbehaving |
| Enhancement | [references/templates/issue-enhancement.md](references/templates/issue-enhancement.md) | New feature, improvement, or refactor |
| Chore | [references/templates/issue-chore.md](references/templates/issue-chore.md) | Deps, infra, no-behavior refactor, formatting |
| Epic | [references/templates/issue-epic.md](references/templates/issue-epic.md) | Multi-issue / multi-PR goal (see Epics above) |

Progress updates as you work an issue: [references/templates/progress-comment.md](references/templates/progress-comment.md).

## Issue Labels

The **author of the issue** applies labels at creation. The author of the PR mirrors the same labels onto the PR at PR creation. Exactly one of `bug`/`enhancement`/`chore`/`epic` should classify every issue. Severity is required on bugs, optional on enhancements/chores. Priority (`priority:low`/`medium`/`high`, at most one) is optional and used to **sequence** work — most useful on backlog items. The `backlog` label marks issues filed during planning that are not active work yet (see "Plan the work, then file it all"); pair it with a priority so the intended order is captured on the issue itself. `documentation` marks any issue whose work is primarily docs (it coexists with a type). A `concern:*` (at most one) classifies the *kind* of a found, usually-deferred problem — `style`, `lint`, `tests`, `refactor`, `perf`, or `security` — so the review-findings backlog is filterable by dimension.

This is the **canonical label set** — names *and* colors. The colors are part of the contract so a repo that freshly adopts this skill ends up looking like every other repo that uses it; provision them exactly as listed.

| Label | Color | When to use |
| ----- | ----- | ----------- |
| `bug` | `d73a4a` | Something isn't working |
| `enhancement` | `a2eeef` | New feature or improvement |
| `chore` | `cfd3d7` | Maintenance — deps, refactor, lint, build, infra |
| `epic` | `5319e7` | Meta-issue tracking a multi-issue / multi-PR goal |
| `documentation` | `0075ca` | Docs-only work, or a found docs gap — coexists with a type |
| `help` | `008672` | Requires human intervention — agent is blocked |
| `severity:critical` | `b60205` | Breaks runtime, blocks merge, or causes data loss |
| `severity:major` | `d93f0b` | Affects a core path but has a workaround |
| `severity:minor` | `fbca04` | Cosmetic, edge case, or low-frequency |
| `priority:high` | `cf222e` | Sequencing — pull this ahead of other work |
| `priority:medium` | `dbab09` | Sequencing — normal ordering |
| `priority:low` | `0e8a16` | Sequencing — do after higher-priority work |
| `regression` | `e99695` | A previously-fixed issue has returned, or a recent change broke something that worked |
| `backlog` | `bfdadc` | Filed during planning; not active work yet — sequence it with a `priority:*` |
| `deferred` | `c5def5` | Real but intentionally out of scope for now |
| `concern:style` | `d4c5f9` | Found-issue dimension — style / formatting / naming drift |
| `concern:lint` | `d4c5f9` | Found-issue dimension — linter warning not blocked on |
| `concern:tests` | `d4c5f9` | Found-issue dimension — missing/thin tests or coverage gap |
| `concern:refactor` | `d4c5f9` | Found-issue dimension — code smell, duplication, dead code |
| `concern:perf` | `d4c5f9` | Found-issue dimension — performance / inefficiency |
| `concern:security` | `d4c5f9` | Found-issue dimension — non-blocking or pre-existing security concern |

The `help` label signals that the agent cannot resolve the issue autonomously. Always post a comment explaining what was tried and why it's blocked before adding this label.

### Provisioning the labels is a hard gate

Before filing the first issue in a repo — and any time you reach for a label that is missing or whose color has drifted — reconcile the repo's labels against the canonical set above: create any that are missing with the exact name and color, and correct any whose color differs. Locally that's `gh label create <name> --color <hex> --description "<when to use>"` and `gh label edit <name> --color <hex>`. In the cloud sandbox there is **no** GitHub MCP tool to create or edit a label (only `get_label`, to check existence), so you usually cannot self-provision there.

When you cannot create or correct the labels yourself — no tool, no permission, or the API refuses — **stop, ask the user to add or fix them against this canonical list, and refuse to continue until they confirm it is done.** Do not improvise around it: inventing a substitute label, dropping the label, or proceeding unlabeled quietly breaks the parts of the workflow that lean on these exact names (classification, severity/priority/concern triage, the deferred queue and the planning backlog, the review gate). The label set is load-bearing, so this is the right place to halt and wait rather than press on.

## Branches and Worktrees

Create a branch for each issue before starting work. Branch names follow the pattern `<issue-number>-<short-description>`.

To allow multiple agents to work on different issues concurrently, always create a **temporary git worktree** instead of switching branches in the main checkout:

```bash
git worktree add /tmp/issue-10 -b 10-short-description
cd /tmp/issue-10
```

All commits for that issue go in its worktree. Do not commit directly to `main`. Keep the worktree until the PR is merged — the review loop may need fixes applied in it. After the review agent merges the PR, clean up:

```bash
git worktree remove /tmp/issue-10
git branch -D 10-short-description
```

The local branch is removed with `-D` because a squash merge leaves it unmerged in local history.

**When to use worktrees vs. work in place:** worktrees are required when another agent or human is actively working in the main checkout, or when multiple issues are being worked on in parallel. For solo serial work, committing directly on the issue branch in the main checkout is fine. The rest of the workflow (issue-first, `AI:` prefix, PR with full body, contextless review) still applies either way.

## Commits

All AI-authored commits use the `AI:` prefix:

```bash
git commit -m "AI: fix description of what was fixed"
```

Humans should not use the `AI:` prefix — it exists so commit history can be filtered by author type.

Two further commit conventions:
- **One logical change per commit.** Don't bundle unrelated fixes.
- **Squash on merge is the default.** Feature branches can carry multiple commits during review iterations; the final landed commit on `main` is one squashed commit per PR.

## Trivial Change Fast Path

For changes that are mechanically simple and behavior-free, skip the full review-subagent loop:

- Doc-only typo fixes (single word/punctuation in markdown)
- Comment-only changes that don't alter executable code
- Formatting-only changes from running an autoformatter
- Renaming a local variable for clarity, no signature change

Process for trivial changes:
1. Still open an issue (or reuse an existing one) so there's a record.
2. Single commit with `AI:` prefix.
3. Push directly to a small-scoped PR with `[trivial]` in the title.
4. The PR may be self-merged after CI passes — **this is the only path on which the authoring conversation may merge its own work.** Everything else goes through the contextless reviewer (see PR Review Handoff).

If you're unsure whether a change qualifies, treat it as non-trivial and use the full workflow.

## Pull Requests

Before opening a PR:
- All unit tests pass. (Unit tests mock external dependencies.)
- All integration tests pass. (Integration tests exercise real code against real dependencies — real network, filesystem, services. Discover the project's split from its testing skill, `CLAUDE.md`, or convention.)
- The project linter has been run and reported issues are fixed or explicitly justified. Examples by language:
  - PowerShell: `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning`
  - JavaScript / TypeScript: `npm run lint` (or `npx eslint .`)
  - Python: `ruff check .` (or `pylint <pkg>`)
  - Go: `go vet ./... && golangci-lint run`

  Discover the actual command from the project's lint config files, `CLAUDE.md`, or `package.json` / `pyproject.toml` / etc. If the project has a linter installed but no canonical invocation is documented, that's worth surfacing to the human.

When everything passes, push the branch and create the pull request (`gh pr create` locally, `create_pull_request` in the cloud). The PR body must follow [references/templates/pr-body.md](references/templates/pr-body.md) — the **Suggested Test Steps**, **Risk**, and **Rollback** sections are required.

### PR Title Convention

Format: `<type>(<scope>): <description>`

- **type:** `fix`, `feat`, `refactor`, `test`, `docs`, `chore`, `perf`
- **scope:** module name (e.g. `parallelism`, `common`) or `all` for cross-cutting changes
- **description:** imperative mood, lowercase, no period
- **length:** ≤ 70 characters total
- example: `fix(parallelism): drain queued jobs on one-shot exit`

The commit prefix is still `AI:` — the type-scope convention applies to the PR title (and any generated changelog), not the commit body.

After the PR is created, the **PR author** applies the same labels as the linked issue (locally `gh pr edit <N> --add-label …`; in the cloud, label the PR number via `issue_write` `update` — `update_pull_request` has no labels field, see references/github-tools.md).

### Draft PRs

If the work is incomplete or you want CI feedback without spawning a review subagent yet, open the PR as a draft (`gh pr create --draft` / `create_pull_request` with `draft: true`). When it's fully ready (tests pass, coverage holds, lint clean, body complete), mark it ready (`gh pr ready <N>` / `update_pull_request` with `draft: false`).

**Do not spawn the review subagent until the PR is out of Draft.** The review costs context and tokens; running it on incomplete work is wasted effort.

## PR Review Handoff

A PR is **never merged by the agent that wrote it.** This is the rule that keeps everyone honest: the conversation that wrote the code cannot also be the one that signs off on it. The only exception is the trivial/doc fast path above. Every other PR is approved and merged by an independent, contextless reviewer running the [github-pr-review](../github-pr-review/SKILL.md) skill — and that separation is enforced by *us following it*, not by any tooling limitation.

To hand off, spawn a **subagent with no prior context** using the consistent kickoff prompt in [references/templates/review-handoff.md](references/templates/review-handoff.md). It tells the reviewer the PR number, the environment (so it picks the right tools), the round, the contextless boundary, and that it alone records the verdict and performs the squash-merge.

### Review Loop

1. Parent creates the PR with Suggested Test Steps in the body.
2. Parent spawns a contextless review subagent via the review-handoff template. The reviewer reviews fresh, runs all tests, and either requests changes or merges.
3. If the reviewer posts **`## PR Review — Changes Requested`**, the parent:
   - Fixes every finding in the issue's worktree.
   - Commits with the `AI:` prefix.
   - Re-runs unit + integration tests + lint.
   - Posts a **Fixes Applied** comment ([references/templates/fixes-applied.md](references/templates/fixes-applied.md)).
4. Parent spawns a **new** contextless subagent for the next round. Every round is a fresh agent — it gets context only from the PR and its comments, never from the parent.
5. The cycle repeats until the reviewer posts **`## PR Review — Approved`** and merges the PR.
6. After merge, the parent does final cleanup (worktree + local branch).

The reviewer caps the loop at **3 cycles**. A "round" is counted by the number of `## PR Review — Changes Requested` (or `Decomposition Requested`) comments already on the PR — not by the number of pushes. If the PR is still not clean after 3 rounds, the reviewer applies the `help` label and posts an escalation comment — the parent must then stop and surface the blocker to a human.

### Session Resume

If the parent session terminates mid-loop, the next session must re-derive state from the PR itself before responding:

- Count existing `## PR Review — Changes Requested` / `## PR Review — Decomposition Requested` comments → that's the round number completed.
- The latest `## Fixes Applied` comment shows what the previous parent did last.
- Pick up at the next step (either fixing newly-requested findings or spawning the next review).

Never spawn a review subagent until you have reconciled state from PR comments. (If the work belongs to an epic, also re-read the epic first — see "Starting a session" above.)

## Regressions

If a merged fix is found to have caused a regression:

- **Same area as the original fix:** reopen the original issue, add a `## Regression` comment describing what now fails, link the PR that introduced it, apply the `regression` label, and re-enter the workflow on the reopened issue.
- **Different area:** open a new issue with the bug template and the `regression` label. Reference the originating PR in the Discovery section.
