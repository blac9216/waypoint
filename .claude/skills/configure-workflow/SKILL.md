---
name: configure-workflow
description: Set up or audit a GitHub repository for the github-workflow skill — the Project board (fields, views, workflows), the canonical label set plus repo area labels, branch rulesets, account grants, the optional reviewer account, and the docs/process files the workflow reads. Use it for a fresh repo ("configure this repo for the workflow"), for auditing or repairing an already-configured one ("audit the workflow setup", "labels drifted", "add an area label"), for capturing an edited Project as the new standard ("make this board the template"), and when the github-workflow skill reports a missing fixture (no area labels, no docs/process, no Claimed by field). Idempotent: safe to re-run.
argument-hint: apply | audit | capture [--owner <login> --project <n>]
---

# Configure a repository for the workflow

The github-workflow skill assumes a set of fixtures exist and are exact: a Project with three
custom fields and a known set of views and automations, a closed label set with canonical
colours, a default branch that only accepts pull requests, an automation account that can
write to all of it, and `docs/process/` files that carry everything repo-specific. This skill
creates them, keeps them in sync, and turns your edited Project into the standard for the
next repository. Everything deterministic is a script; everything that needs judgment or
your credentials is a guided step.

## Two kinds of steps

| Automation-owned (the session runs these) | Owner-owned (your credentials or the UI) |
|---|---|
| labels, Project fields/views, docs/process scaffolding, the audit | creating the Project and granting the automation account admin; branch rulesets; collaborator grants; Project workflows and view group-by/sort (UI only); the reviewer account |

Owner-owned scripts take the token as **input** (`GH_TOKEN`); they never read a vault. To run
one for the owner, use the `with-secrets` skill to inject the owner's token into that one
command — discover the key name from that skill's inventory or ask. If no token is
available, print the exact command for the owner to run themselves.

## `apply` — the order

1. **Orient.** `gh auth status` (automation account, needs `repo`, `project`, `read:org`);
   `gh repo view`; does `docs/process/` exist; is there a Project already
   (`gh project list --owner <login>`). Read `AGENTS.md`/`CLAUDE.md` and `.gitignore` as they
   are now — you will propose edits to them later, not overwrite them.
2. **The Project.** If none exists, ask the owner to create it — brief steps in
   [references/owner-wizard.md](references/owner-wizard.md) — and to say anything that
   changes how it is managed (a board shared by several repositories, an org-owned board).
   Then `scripts/project.sh --owner <login> --project <n>` brings it to the manifest and
   prints the UI checklist (workflows, group-by, sort) for the owner. Shared boards: apply
   the baseline first, then the multi-repo additions by hand —
   [references/shared-projects.md](references/shared-projects.md).
3. **Grants.** `scripts/grant.sh` (owner token) adds the automation account as collaborator
   and Project admin, and the reviewer account if one exists. Verify with `gh project list`
   under the automation account.
4. **Labels.** Propose the repo's `area:*` set from its top-level layout (coarse — a handful,
   e.g. backend / frontend / deploy / docs / ci, plus whatever the repo actually splits on),
   confirm with the owner, write the table into `docs/process/labels.md`, then
   `scripts/labels.sh`. It creates/corrects the canonical set, creates the areas, and prunes
   labels outside both when no open issue carries them (it reports the ones it kept).
5. **docs/process.** `scripts/process-docs.sh` scaffolds the seven files and fills what it
   can detect (Project ids, CI check names, test/lint commands from manifests). Then fill
   the rest yourself from the repository — CI workflows, existing docs, README — and
   interview the owner only for what you genuinely cannot derive (live systems, thresholds,
   overnight limits). Show drafts before writing. `<owner: …>` markers left behind fail the
   audit.
6. **Ruleset.** `scripts/rulesets.sh` (owner token) applies the default-branch ruleset:
   require a PR, required checks from `docs/process/testing.md`, strict up-to-date, no
   force-push/deletion, linear history, admin bypass. Before running it, confirm every
   required check **always reports** on PRs — a path-filtered workflow that never runs on a
   docs PR blocks that PR forever.
7. **Reviewer account (optional).** [references/reviewer-account.md](references/reviewer-account.md).
   If the owner skips it, write `Reviewer identity: none — single account; the review
   comment plus the merge are the verdict of record` into `docs/process/work-tracking.md`
   so the fallback is a declared fact.
8. **Repo files.** Read `.gitignore`, `AGENTS.md`/`CLAUDE.md` and any worktree convention;
   propose the minimal edits (ignore `*.local.md`; a pointer that process lives in
   `docs/process/` and work follows the github-workflow skill) and let the owner decide.
   Apply only what they approve.
9. **Skills.** The repo needs its own copies of `github-workflow` and `github-pr-review`
   (cloud sessions cannot see the home copy) — [references/propagation.md](references/propagation.md).
10. **Audit.** `scripts/audit.sh --owner <login> --project <n> --machine <login>` — every
    fixture present and exact, or a list of gaps. Re-run until clean; that is the exit.

## `audit`

`scripts/audit.sh` alone. It is read-only and exits non-zero on any gap. Run it when the
workflow skill reports a missing fixture, after anyone edits the Project by hand, and before
trusting a repo you have not configured yourself.

## `capture` — make an edited Project the standard

The owner edits the board in the UI; the manifest must follow, or the next repository gets
the old shape. `scripts/capture.sh --owner <login> --project <n>` snapshots fields, options,
views (layout, filter, columns, group-by, sort) and enabled workflows into
`manifests/project.json`. Show the owner the diff of the manifest before committing to it,
then propagate the skill so every copy carries the new standard. Group-by and sort are
captured for the checklist; the API cannot set them.

## What lives where

- **Manifests** ([manifests/](manifests/)): `labels.json` (canonical set — the contract),
  `project.json` (the board standard), `rulesets.json` (the branch rules). One standard for
  every repository; repo-specific values never go here.
- **Repo-specific** values live in the repo's `docs/process/`: area labels, required check
  names, test commands, worktree root, thresholds, overnight limits, reviewer identity.
- **Templates** ([templates/process/](templates/process/)): the seven `docs/process` files
  with `{{MARKERS}}` the script fills and `<owner: …>` markers you fill.

## Things that will bite

- The automation account cannot create a Project on a personal account, cannot read or
  write rulesets without admin, and cannot see the owner's branch protection at all — a
  `404` from those endpoints means "not your role", not "does not exist".
- Project workflows and view group-by/sort exist only in the UI; the checklist is the
  mechanism. Workflows are *readable*, so the audit verifies they are enabled.
- Large boards cost GraphQL points; the scripts read each field/view list once.
- Deleting a label removes it from closed issues too; that is why pruning checks open
  issues only and reports rather than deletes when a non-canonical label is still in use.
- The `scripts/*.sh` require **bash ≥ 4** (case-folding parameter expansion, e.g.
  `${var,,}`, is bash-4-only and fails at expansion time on bash 3.2 — still the system
  `/bin/bash` on macOS — with `bash: ${acct,,}: bad substitution`, a runtime error rather
  than a parse/syntax error or a graceful failure). `_lib.sh` enforces this with an
  explicit version check that fails with a named message before any script does real
  work.
