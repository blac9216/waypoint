# Testing

Commands and environment the workflow's dispatch prompts copy verbatim. The long-form
rationale, the isolation recipe and the honesty rules live in [../testing.md](../testing.md)
— required reading before any stack bring-up.

## Required checks
<!-- names exactly as they appear on PR check runs; only always-reporting jobs may be required (issue #232) -->
Required on `main` via classic branch protection (`strict: false`; owner ruling
2026-09-06, issue #100) — a red or pending context blocks the merge:
- secret + identifier scan
- build, test, coverage
- build, test, lint
- compose config, nginx -t, shellcheck
- shellcheck .claude/skills
- test .claude/skills
- download-runner: pester, coverage, shellcheck
- compliance-runner: pester, coverage, shellcheck

The seven path-filtered contexts above — `build, test, coverage` (backend/**), `build, test, lint` (frontend/**), `compose config, nginx -t, shellcheck` (deploy/**, scripts/**), `shellcheck .claude/skills` + `test .claude/skills` (.claude/skills/**/*.sh), `download-runner: pester, coverage, shellcheck` (runners/download-runner/**), `compliance-runner: pester, coverage, shellcheck` (runners/compliance-runner/**) — always report: each workflow gates the real work internally (a `changes` job + `if: needs.changes.outputs.<name> == 'true'`) and a final always-run gate job owns the check-run name above, succeeding when the real job succeeded or was skipped and failing otherwise (the always-report pattern, #232). The gate job uses `if: always()`, so a cancelled run fails the gate instead of skipping it — GitHub reports a job skipped by its own condition as Success to a required check. Each workflow's `changes: <name>` job reports its own context too, but only the seven gate contexts above are always-report; other path-gated jobs (e.g. `pester: powershell shape inventory`) are not, and are not required-check candidates.

## Commands
| Suite | Command | Environment |
|---|---|---|
| backend unit + integration | `dotnet test backend/Waypoint.sln` | `export PATH="$HOME/.dotnet:$PATH"`; `WAYPOINT_TEST_PG_NETWORK=<docker network of this process>` (devcontainer: `git_devcontainer_default`) — see ../testing.md §Postgres test fixture |
| backend build (CI parity) | `dotnet build backend/Waypoint.sln -warnaserror` | same PATH |
| frontend unit | `cd frontend && npm ci && npm test` | Node per `frontend/.nvmrc` / README |
| frontend build + air-gap guard | `cd frontend && npm run build` | must fail on any external asset (ADR-0007) |
| lint | backend: `dotnet format backend/Waypoint.sln --verify-no-changes`; frontend: `cd frontend && npx oxlint`; shell (same set as CI): `find deploy scripts -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck` and `find .claude/skills -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck --shell=bash -S error` | |
| skill script tests | `find .claude/skills -type f -path "*/tests/*.sh" -print0 \| xargs -0 --no-run-if-empty -n1 bash` | `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8` required — self-contained, mocked `gh` on `PATH`, no network (see #1345) |
| coverage | backend: `dotnet test backend/Waypoint.sln --collect:"XPlat Code Coverage"` then `python3 scripts/check-coverage-floor.py --report "backend/TestResults/**/coverage.cobertura.xml" --format cobertura --floor 88.0 --metric line`; frontend: `cd frontend && npm run test:coverage` then `python3 scripts/check-coverage-floor.py --report "frontend/coverage/coverage-summary.json" --format vitest-json-summary --floor 88.0 --metric line`; runner (Pester → JaCoCo): `pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = "<suite>.Tests.ps1"; $c.CodeCoverage.Enabled = $true; $c.CodeCoverage.Path = "<module>.psm1"; $c.CodeCoverage.OutputFormat = "JaCoCo"; $c.CodeCoverage.OutputPath = "coverage.xml"; Invoke-Pester -Configuration $c'` then `python3 scripts/check-coverage-floor.py --report coverage.xml --format jacoco --floor 88.0 --metric line` | gate: floors live in the workflow YAML (88.0 line for both backend and frontend), not in the script; no regression vs base. `src/screens/**` is measured (not excluded) as of issue #1314 — the frontend number reflects the whole app, screens included. |
| coverage-gate script unit tests | `python3 -m unittest discover -s scripts/tests -v` | stdlib `unittest` only, no new deps; covers all three `check-coverage-floor.py` readers against invented fixtures under `scripts/tests/fixtures/` |
| PowerShell module unit (Pester) | `pwsh -NoProfile -Command "Invoke-Pester -Path <suite>.Tests.ps1 -CI"` | `WaypointDiscovery.SessionMatch.Tests.ps1` keeps two deliberate opt-in real-DNS cases (issue #1252/#1299): they assert that an unresolvable `.invalid` name yields no addresses, so they require a resolver that returns NXDOMAIN for RFC 2606 names and FAIL on a wildcard/hijacking resolver that answers every name. Everything else in that suite is hermetic. |
| download-runner Pester | `pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = "runners/download-runner/tests"; $c.Run.Exit = $true; $c.CodeCoverage.Enabled = $true; $c.CodeCoverage.Path = "runners/download-runner/powershell/project/vcf-download-manager.common.ps1"; $c.CodeCoverage.OutputFormat = "JaCoCo"; $c.CodeCoverage.OutputPath = "runners/download-runner/coverage.xml"; Invoke-Pester -Configuration $c'` then `python3 scripts/check-coverage-floor.py --report runners/download-runner/coverage.xml --format jacoco --floor 88.0 --metric line` | dot-sources `vcf-download-manager.common.ps1` directly from the runner tree (issue #1356), not through `backend/`; network cmdlets (`Invoke-WebRequest`) are `Mock`ed, filesystem cases use `TestDrive:`/`$TestDrive` |
| compliance-runner Pester | `pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = "runners/compliance-runner/tests"; $c.Run.Exit = $true; $c.CodeCoverage.Enabled = $true; $c.CodeCoverage.Path = "runners/compliance-runner/powershell/*.ps1"; $c.CodeCoverage.OutputFormat = "JaCoCo"; $c.CodeCoverage.OutputPath = "runners/compliance-runner/coverage.xml"; Invoke-Pester -Configuration $c'` then `python3 scripts/check-coverage-floor.py --report runners/compliance-runner/coverage.xml --format jacoco --floor 88.0 --metric line` | dot-sources `module.common.ps1`/`module.transport.vmware.ps1`/`module.transport.nsxapi.ps1` directly from the runner tree (issue #1355), not through `backend/`; `Get-LogSplat`/`Write-Log` and every cross-module (catalog/config/attestation) and PowerCLI cmdlet dependency are stubbed then `Mock`ed since PowerCLI is not installed in CI/dev containers |
| sanitize scan | `gitleaks detect --source . --no-banner` and `python3 .github/sanitize/scan_repo_specific.py` | the CI hard gate; run before every push |
| e2e (synthetic, Playwright) | `cd deploy && ./scripts/e2e-playwright.sh <slug> <port>` | unique slug + port; tears down itself |
| smoke | `cd deploy && ./scripts/fresh-stack-smoke-test.sh <slug> <port>` | same |

## Isolation on a shared host
Every bring-up uses its own Compose project name (`-p <slug>`) and host port well away from 8443; verify isolation before trusting a result; `down -v` when done. Docker cannot see `/tmp` — bind mounts live under `/workspaces`. Full recipe: ../testing.md §The recipe.

## Live testing
Pointer only: environment-specific recipes live in `docs/testing.local.md` (untracked).
