# Repo-specific failure modes

General ones live in the github-workflow skill. These have bitten here:
- **42501 grant drift** — a new table or column used by a runner role without the matching GRANT in the migration; caught only by tests that run under the real roles. Guard: grant-hygiene convention tests (#573 umbrella).
- **Stale closed sets** copied from an old migration (job types, run types, purposes). Guard: drift tests that parse the authoritative migration.
- **gitleaks runs range-mode** in CI: a `// gitleaks:allow` annotation must be on the introducing line in the same commit; a follow-up commit cannot clear it — collapse the unmerged branch if needed.
- **CI vs local analyzer drift** (CA1859 etc.): pin the SDK via `global.json` (#859); a green local build is not evidence CI is green.
- **Migration number collisions** between parallel agents: pre-assign slots; verify at branch time against the tree and open PRs.
- **Docker cannot see `/tmp`**: bind mounts under `/tmp` mount empty; use `/workspaces`.
- **Frontend dist bind-mounts** go stale after UI merges on a live stack; rebuild `frontend/dist` and hard-refresh the PWA.
- **Path-filtered CI** means a docs-only PR reports only the sanitize job — required checks must be always-report; the always-report pattern (changes job + always-run gate job) landed with #232.
- **Devcontainer docker credential-helper exit 255, silent** (issue #1595): the
  devcontainer's `~/.docker/config.json` names a `credsStore` pointing at a
  `docker-credential-dev-containers-<id>` helper (verified present at
  `/usr/local/bin/docker-credential-dev-containers-<id>` in this environment).
  Calling it directly (`echo get | docker-credential-<name> get`) exits 255 with no
  stderr. Any `docker pull`/`docker compose build` step that needs to authenticate an
  uncached public-image pull through this helper fails the same way — silently,
  with no diagnostic text pointing at the credential helper as the cause. Workaround:
  point `DOCKER_CONFIG` at a scratch directory containing an empty `{}` config (no
  `credsStore`) for the pull/build step only — never edit the real
  `~/.docker/config.json`. Example: `DOCKER_CONFIG=<scratch-dir> docker pull <image>`
  with `<scratch-dir>/config.json` containing `{}`.
