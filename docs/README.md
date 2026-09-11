# Documentation

Kind: reference

Organised by [Diátaxis](https://diataxis.fr) kind. Decision records and the rationale
index are explanation by construction and have their own directories. The standard this
tree follows is recorded in [doc-manifest.md](doc-manifest.md). The four core explanation
docs (architecture, domain model, security, roadmap) have moved into `explanation/`; the
remaining docs below still live at their current paths — moving them is tracked as
remediation from the baseline audit, not done here.

## Tutorials — learning by doing
(none yet)

## How-to — task recipes
- [how-to/testing.md](how-to/testing.md) — bring-up, isolation, and the test commands this repo runs
- [how-to/ui-prototype.md](how-to/ui-prototype.md) — UI prototype handoff and per-screen visual/interaction reference

## Reference — facts and contracts
- [api-contract.md](api-contract.md) — REST resources, SSE events, state machines, schema, data ledger

## Explanation — why things are the way they are
- [explanation/architecture.md](explanation/architecture.md) — system architecture: components, job engine, modes, update flow
- [explanation/domain-model.md](explanation/domain-model.md) — sites, targets, credentials, runs, roles, open questions
- [explanation/security.md](explanation/security.md) — secrets threat model and mandatory leakage controls
- [explanation/roadmap.md](explanation/roadmap.md) — build sequencing: what gets built first and why
- [compliance-parity.md](compliance-parity.md) — planned compliance execution parity contract (epic #726)
- [compliance-content-shape-inventory.md](compliance-content-shape-inventory.md) — vendor-content parser shape inventory (issue #1077 guard)
- [ui/design-brief.md](ui/design-brief.md) — screen inventory, reconciliation notes, data ledger

## Decisions and rationale
- [Architecture Decision Records](adr/README.md) — read the index table first
- [Rationale index](rationale/) — the evicted "why" behind `# why:` pointers in code

## Process
- [process/](process/) — how work is tracked, tested and validated here
