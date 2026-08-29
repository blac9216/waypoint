# Testing

<!-- Scaffolded by configure-workflow; fill every <owner> marker. Dispatch prompts copy these commands verbatim. -->

## Required checks
<!-- names exactly as they appear on PR check runs; each must always report -->
{{REQUIRED_CHECKS}}

## Commands
| Suite | Command | Environment |
|---|---|---|
| unit | {{UNIT_CMD}} | {{UNIT_ENV}} |
| integration | {{INTEGRATION_CMD}} | {{INTEGRATION_ENV}} |
| lint | {{LINT_CMD}} | |
| coverage | {{COVERAGE_CMD}} | gate: {{COVERAGE_GATE}} |
| sanitize scan | {{SANITIZE_CMD}} | |

## Isolation on a shared host
<owner: how concurrent agents keep stacks apart — project names, ports, cleanup>

## Live testing
Pointer only: environment-specific recipes live in `docs/testing.local.md` (untracked).
