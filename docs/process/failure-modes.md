# Repo-specific failure modes

General ones live in the github-workflow skill. These have bitten here:
- **42501 grant drift** — a new table or column used by a runner role without the matching GRANT in the migration; caught only by tests that run under the real roles. Guard: grant-hygiene convention tests (#573 umbrella).
- **Stale closed sets** copied from an old migration (job types, run types, purposes). Guard: drift tests that parse the authoritative migration.
- **gitleaks runs range-mode** in CI: a `// gitleaks:allow` annotation must be on the introducing line in the same commit; a follow-up commit cannot clear it — collapse the unmerged branch if needed.
- **CI vs local analyzer drift** (CA1859 etc.): pin the SDK via `global.json` (#859); a green local build is not evidence CI is green.
- **Migration number collisions** between parallel agents: pre-assign slots; verify at branch time against the tree and open PRs.
- **Docker cannot see `/tmp`**: bind mounts under `/tmp` mount empty; use `/workspaces`.
- **Frontend dist bind-mounts** go stale after UI merges on a live stack; rebuild `frontend/dist` and hard-refresh the PWA.
- **Path-filtered CI** means a docs-only PR reports only the sanitize job — required checks must be always-report (#232).
