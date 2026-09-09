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

## CI-coverage map
<!-- named section required by github-pr-review's Step 5 Path 1 (evidence-paths.md);
     derived directly from .github/workflows/*.yml — the filters and steps each job
     actually runs — not from prose elsewhere in this file. -->
Path 1 ("skip local test execution entirely") applies only when the diff touches
**exclusively** surfaces listed below, each mapped to the check run that fully
exercises it:

| Surface (path prefix) | Check run | Coverage enforced in CI? |
|---|---|---|
| `backend/**` (+ shared inputs, see `backend.yml` header) | `build, test, coverage` | Yes — `dotnet test --collect:"XPlat Code Coverage"` then `check-coverage-floor.py` at 88.0% line, no path excluded. |
| `frontend/**` (+ `RunTypes.cs`/`DepotArtifact.cs`, see `frontend.yml` header) | `build, test, lint` | Yes — `npm run test:coverage` then `check-coverage-floor.py` at 88.0% line (`src/screens/**` included, issue #1314). This corrects issue #1589's original discovery note that this job had "no coverage step": that was true before issue #102 added the coverage-collection and floor-gate steps, and is no longer accurate as of this map. |
| `deploy/**`, `scripts/**` | `compose config, nginx -t, shellcheck` | No. `docker compose config`, `nginx -t`, and `shellcheck` are structural/lint checks; there is no test-coverage command for this surface, in CI or documented locally. |
| `.claude/skills/**/*.sh` | `shellcheck .claude/skills` + `test .claude/skills` | No. Shellcheck plus the skill script regression suite; no coverage instrumentation exists for shell scripts in this repo. |
| `runners/download-runner/**` | `download-runner: pester, coverage, shellcheck` | Yes — Pester with JaCoCo output, floor-gated at 88.0% line (see the runner coverage row below for the LINE-vs-console-percentage caveat). |
| `runners/compliance-runner/**` | `compliance-runner: pester, coverage, shellcheck` | Yes — Pester with JaCoCo output, floor-gated at 88.0% line (same caveat). |
| every path, unconditionally (docs, root files, anything not above) | `secret + identifier scan` | No. `sanitize` is a hard gate (no path filter) but is a secret/identifier scan, not a test or coverage signal. |

**Frontend coverage on a frontend-only PR**: coverage **is** enforced in CI (see
table). Record it via the manifest Coverage field's first form — the coverage command
(`cd frontend && npm run test:coverage` + the floor-gate script) and its CI result —
the same as any other surface with an enforced floor. There is no "no coverage
command" case for `frontend/**`.

**A path with no coverage command** (`deploy/**`, `scripts/**`, `.claude/skills/**/*.sh`)
records the manifest Coverage field's second form, quoting this map's own line above
that says so for that surface — e.g. for `deploy/**`/`scripts/**`: "there is no
test-coverage command for this surface, in CI or documented locally."

A diff spanning more than one surface above needs every touched surface's check run
green, not just one, for Path 1 to apply; a diff touching any surface **not** listed
here (there are none outside the eight rows above, since `secret + identifier scan`
covers everything unconditionally) falls through to Path 2/3 per `evidence-paths.md`.

## Commands
| Suite | Command | Environment |
|---|---|---|
| backend unit + integration | `dotnet test backend/Waypoint.sln` | `export PATH="$HOME/.dotnet:$PATH"`; `WAYPOINT_TEST_PG_NETWORK=<docker network of this process>` (devcontainer: `git_devcontainer_default`) — see ../testing.md §Postgres test fixture |
| backend build (CI parity) | `dotnet build backend/Waypoint.sln -warnaserror` | same PATH |
| frontend unit | `cd frontend && npm ci && npm test` | Node per `frontend/.nvmrc` / README |
| frontend build + air-gap guard | `cd frontend && npm run build` | must fail on any external asset (ADR-0007) |
| lint | backend: `dotnet format backend/Waypoint.sln --verify-no-changes`; frontend: `cd frontend && npx oxlint`; shell (same set as CI): `find deploy scripts -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck` and `find .claude/skills -type f -name "*.sh" -print0 \| xargs -0 --no-run-if-empty shellcheck --shell=bash -S error`; the three container entrypoints (`backend/docker-entrypoint.sh`, `runners/download-runner/docker-entrypoint.sh`, `runners/compliance-runner/docker-entrypoint.sh`) are each shellchecked individually by their own workflow's `shellcheck (job)` (#1237, #1355, #1356) | |
| skill script tests | `find .claude/skills -type f -path "*/tests/*.sh" -print0 \| xargs -0 --no-run-if-empty -n1 bash` | `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8` required — self-contained, mocked `gh` on `PATH`, no network (see #1345) |
| coverage | backend: `dotnet test backend/Waypoint.sln --collect:"XPlat Code Coverage" --results-directory backend/TestResults` (run from the repo root; without `--results-directory` the Cobertura report lands under the test project's own `backend/Waypoint.Tests/TestResults/`, not the glob below — issue #1750, matching `.github/workflows/backend.yml`'s `working-directory: backend` + `--results-directory ./TestResults`, i.e. `backend/TestResults` from the repo root) then `python3 scripts/check-coverage-floor.py --report "backend/TestResults/**/coverage.cobertura.xml" --format cobertura --floor 88.0 --metric line`; frontend: `cd frontend && npm run test:coverage` then `python3 scripts/check-coverage-floor.py --report "frontend/coverage/coverage-summary.json" --format vitest-json-summary --floor 88.0 --metric line`; runner (Pester → JaCoCo): `pwsh -NoProfile -Command '$c = New-PesterConfiguration; $c.Run.Path = "<suite>.Tests.ps1"; $c.CodeCoverage.Enabled = $true; $c.CodeCoverage.Path = "<module>.psm1"; $c.CodeCoverage.OutputFormat = "JaCoCo"; $c.CodeCoverage.OutputPath = "coverage.xml"; Invoke-Pester -Configuration $c'` then `python3 scripts/check-coverage-floor.py --report coverage.xml --format jacoco --floor 88.0 --metric line` | gate: floors live in the workflow YAML (88.0 line for both backend and frontend), not in the script; no regression vs base. `src/screens/**` is measured (not excluded) as of issue #1314 — the frontend number reflects the whole app, screens included. **Runner (Pester → JaCoCo) caveat (issue #1714):** the gate reads the JaCoCo report-level LINE counter, which is a different quantity from the command-coverage percentage Pester prints to its own console — verified against a real Pester 6.1.0 run where the console reported `Covered 75% / 75%. 4 analyzed Commands in 1 File.` while the emitted JaCoCo XML's report-level `<counter type="LINE">` (`missed="0" covered="3"`) computed to 100.00%; trust the gate's LINE number, not the console line. Real Pester JaCoCo output also emits no report-level `<counter type="BRANCH">` element at all, so `--metric branch` is not usable against a Pester-produced report — `check-coverage-floor.py` fails loudly with `error: <report> has no report-level BRANCH counter` (confirmed in `scripts/check-coverage-floor.py`) rather than silently; the documented command above already uses `--metric line` for this reason. |
| coverage-gate script unit tests | `python3 -m unittest discover -s scripts/tests -v` | stdlib `unittest` only, no new deps; covers all three `check-coverage-floor.py` readers against invented fixtures under `scripts/tests/fixtures/`; run in CI by `.github/workflows/backend.yml`'s `coverage-gate tests (job)` (issue #1710), gated the same way as the workflow's other jobs |
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
