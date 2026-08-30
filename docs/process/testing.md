# Testing

Commands and environment the workflow's dispatch prompts copy verbatim. The long-form
rationale, the isolation recipe and the honesty rules live in [../testing.md](../testing.md)
— required reading before any stack bring-up.

## Required checks
<!-- names exactly as they appear on PR check runs; only always-reporting jobs may be required (issue #232) -->
- secret + identifier scan

Path-filtered jobs — `build, test, coverage` (backend/**), `build, test, lint` (frontend/**), `compose config, nginx -t, shellcheck` (deploy/**, scripts/**), `shellcheck .claude/skills` + `test .claude/skills` (.claude/skills/**/*.sh) — do not report on every PR and therefore cannot be required until the always-report pattern (#232) lands.

## Commands
| Suite | Command | Environment |
|---|---|---|
| backend unit + integration | `dotnet test backend/Waypoint.sln` | `export PATH="$HOME/.dotnet:$PATH"`; `WAYPOINT_TEST_PG_NETWORK=<docker network of this process>` (devcontainer: `git_devcontainer_default`) — see ../testing.md §Postgres test fixture |
| backend build (CI parity) | `dotnet build backend/Waypoint.sln -warnaserror` | same PATH |
| frontend unit | `cd frontend && npm ci && npm test` | Node per `frontend/.nvmrc` / README |
| frontend build + air-gap guard | `cd frontend && npm run build` | must fail on any external asset (ADR-0007) |
| lint | backend: `dotnet format backend/Waypoint.sln --verify-no-changes`; frontend: `cd frontend && npx oxlint`; shell (same set as CI): `find deploy scripts -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck` and `find .claude/skills -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck --shell=bash -S error` | |
| skill script tests | `find .claude/skills -type f -path "*/tests/*.sh" -print0 \| xargs -0 --no-run-if-empty -n1 bash` | `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8` required — self-contained, mocked `gh` on `PATH`, no network (see #1345) |
| coverage | backend: `dotnet test backend/Waypoint.sln --collect:"XPlat Code Coverage"` then `python3 scripts/check-coverage-floor.py --report "backend/TestResults/**/coverage.cobertura.xml" --format cobertura --floor 88.0 --metric line`; frontend: `cd frontend && npm run test:coverage` then `python3 scripts/check-coverage-floor.py --report "frontend/coverage/coverage-summary.json" --format vitest-json-summary --floor 88.0 --metric line` | gate: floors live in the workflow YAML (88.0 line for both backend and frontend), not in the script; no regression vs base |
| PowerShell module unit (Pester) | `pwsh -NoProfile -Command "Invoke-Pester -Path <suite>.Tests.ps1 -CI"` | `WaypointDiscovery.SessionMatch.Tests.ps1` keeps two deliberate opt-in real-DNS cases (issue #1252/#1299): they assert that an unresolvable `.invalid` name yields no addresses, so they require a resolver that returns NXDOMAIN for RFC 2606 names and FAIL on a wildcard/hijacking resolver that answers every name. Everything else in that suite is hermetic. |
| sanitize scan | `gitleaks detect --source . --no-banner` and `python3 .github/sanitize/scan_repo_specific.py` | the CI hard gate; run before every push |
| e2e (synthetic, Playwright) | `cd deploy && ./scripts/e2e-playwright.sh <slug> <port>` | unique slug + port; tears down itself |
| smoke | `cd deploy && ./scripts/fresh-stack-smoke-test.sh <slug> <port>` | same |

## Isolation on a shared host
Every bring-up uses its own Compose project name (`-p <slug>`) and host port well away from 8443; verify isolation before trusting a result; `down -v` when done. Docker cannot see `/tmp` — bind mounts live under `/workspaces`. Full recipe: ../testing.md §The recipe.

## Live testing
Pointer only: environment-specific recipes live in `docs/testing.local.md` (untracked).
