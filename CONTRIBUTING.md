# Contributing to Waypoint

Thank you for helping improve Waypoint. Contributions can include code, tests,
documentation, design feedback, and reproducible bug reports.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you begin

1. Read the [architecture](docs/architecture.md), relevant
   [architecture decisions](docs/adr/), and [roadmap](docs/roadmap.md). The documents
   distinguish implemented behavior from the target design.
2. Search existing [issues](https://github.com/blac9216/waypoint/issues) and pull
   requests before proposing duplicate work.
3. Open or claim an issue before starting a material change. For a large feature,
   agree on review-sized slices before implementation.
4. Keep changes focused. Do not combine unrelated cleanup with a feature or fix.

For support or design questions that do not yet describe an actionable change, start
with a GitHub discussion when that feature is available; otherwise open a narrowly
scoped issue.

## Development environment

Waypoint uses:

- .NET 8 for the control plane and runner services
- React and TypeScript for the frontend
- PostgreSQL for application state, jobs, and events
- Docker Compose for the appliance topology
- PowerShell for domain execution

Follow the component-specific setup instructions in [backend/README.md](backend/README.md),
[frontend/README.md](frontend/README.md), and [deploy/README.md](deploy/README.md).
Before starting containers or integration tests, read [docs/testing.md](docs/testing.md)
in full. Its Compose isolation and remote-Docker guidance is mandatory on shared
development hosts.

## Making a change

- Branch from the current `main` branch and use a descriptive branch name tied to the
  issue when possible.
- Follow the existing style and keep public contracts aligned with
  [docs/api-contract.md](docs/api-contract.md) and
  [docs/domain-model.md](docs/domain-model.md).
- Add or update tests for changed behavior. A bug fix should include a regression test
  whenever practical.
- Update documentation when behavior, configuration, security boundaries, or operator
  workflows change.
- Preserve accepted ADRs. A change that reverses an accepted decision requires a new
  superseding ADR rather than silently editing history.
- Keep commits logically focused. Human-authored commits should not use the repository's
  `AI:` commit prefix.

## Validation

Run the checks for every area your change affects. The canonical commands and
environment requirements live with each component:

- Backend build, formatting, unit tests, and PostgreSQL integration tests:
  [backend/README.md](backend/README.md)
- Frontend build, lint, unit tests, coverage, and air-gap asset checks:
  [frontend/README.md](frontend/README.md)
- Compose and live-stack validation: [deploy/README.md](deploy/README.md) and
  [docs/testing.md](docs/testing.md)
- Public-repository sanitization: [CLAUDE.md](CLAUDE.md) and the scanner under
  [`.github/sanitize/`](.github/sanitize/)

Report exactly what you ran in the pull request. Do not describe an unexecuted check
as passing. If a required check cannot run, explain why and what evidence is available
instead.

## Public repository and sensitive data

This repository is public. Never submit real:

- Hostnames, IP addresses, domains, inventories, scan results, or logs from private
  infrastructure
- Passwords, tokens, certificates, private keys, credential hashes, or vault content
- Account, entitlement, site, support-contract, customer, employer, or government
  identifiers
- Exported configuration or artifacts that may contain any of the above

Use fictional names under `example.internal` and documentation address ranges such as
`192.0.2.0/24` or `198.51.100.0/24`. If sensitive information is committed, notify the
maintainer immediately; deletion in a later commit is not sufficient.

Do not open a public issue containing a security vulnerability that would put users or
deployments at immediate risk. Use GitHub's private vulnerability-reporting feature
when available, or contact the repository maintainer privately through their GitHub
profile.

## Licensing and external material

Contributions are submitted under the project's [Apache-2.0 License](LICENSE).
Do not copy code, scripts, examples, or assets unless their license is compatible with
the repository's borrowing policy in [CLAUDE.md](CLAUDE.md). Preserve required source
headers and update [NOTICE](NOTICE) when attribution is required.

Never commit account-gated vendor executables or content. Waypoint supports operators
acquiring those materials under their own authorization; the public repository does
not distribute them.

## Pull requests

A pull request should:

- Link the issue it resolves.
- Explain what changed and why.
- Describe risk and rollback considerations.
- Provide reproducible test steps with expected results.
- Stay within a reviewable scope; split work that mixes independent concerns.
- Pass applicable CI and address review feedback before merge.

Use an imperative, scoped title such as `fix(runner): retain lease during shutdown`.
Screenshots are useful for visual changes, but they must contain only invented data.
