# Documentation manifest — as adopted here

This repository follows the `design-docs` skill standard (see the skill's
`references/standard.md`). This file records the adopted shape; the audit reads it first.
Only the values below are repository-specific — the rules themselves are not restated.

## Design set
<!-- paths this standard governs; the audit checks exactly these -->
- docs/explanation/architecture.md
- docs/explanation/domain-model.md
- docs/explanation/security.md
- docs/explanation/roadmap.md
- docs/reference/api-contract.md
- docs/adr/
- docs/rationale/
- CONTEXT.md

## Diátaxis directories
tutorials: docs/tutorials/ · how-to: docs/how-to/ · reference: docs/reference/ · explanation: docs/explanation/
Index: docs/README.md

## ADRs
Directory: docs/adr/ · Range in use: 0001–NNNN · Normalisation ADR: NNNN (or "none — all ADRs created post-adoption")
Index markers: `<!-- adr-index:start -->` / `<!-- adr-index:end -->` in docs/adr/README.md

## Rationale areas
<!-- area → file; a `# why:` pointer may only target a listed file -->
- deploy → docs/rationale/deploy.md
- backend → docs/rationale/backend.md

## Glossary
CONTEXT.md at repo root · domain model: docs/explanation/domain-model.md

## CI
`check-pointers.sh` and `adr-index.sh --check` run in: .github/workflows/<name>.yml (always-report)
Scripts source: .claude/skills/design-docs/scripts/ (or: copied to scripts/docs/)

## Design path
Decisions are made with `interrogate`, decomposed with `plan-work`, recorded with
`design-docs` author mode, landed with `github-workflow`. Specs, plans and interrogation
records are never committed; the superpowers `brainstorming` architectural path
(committed specs) is not used here. Adopted under ADR-NNNN.
