# Documentation

Kind: reference

Organised by [Diátaxis](https://diataxis.fr) kind. Decision records and the rationale
index are explanation by construction and have their own directories. The standard this
tree follows is recorded in [doc-manifest.md](doc-manifest.md). This adoption PR creates
the kind directories; the docs below still live at their current paths — moving them is
tracked as remediation from the baseline audit, not done here.

## Tutorials — learning by doing
(none yet)

## How-to — task recipes
- [testing.md](testing.md) — bring-up, isolation, and the test commands this repo runs

## Reference — facts and contracts
- [api-contract.md](api-contract.md) — REST resources, SSE events, state machines, schema, data ledger

## Explanation — why things are the way they are
- [architecture.md](architecture.md) — system architecture: components, job engine, modes, update flow
- [domain-model.md](domain-model.md) — sites, targets, credentials, runs, roles, open questions
- [security.md](security.md) — secrets threat model and mandatory leakage controls
- [roadmap.md](roadmap.md) — build sequencing: what gets built first and why
- [compliance-parity.md](compliance-parity.md) — planned compliance execution parity contract (epic #726)
- [compliance-content-shape-inventory.md](compliance-content-shape-inventory.md) — vendor-content parser shape inventory (issue #1077 guard)
- [ui/design-brief.md](ui/design-brief.md) — screen inventory, reconciliation notes, data ledger

## Decisions and rationale
- [Architecture Decision Records](adr/README.md) — read the index table first
- [Rationale index](rationale/) — the evicted "why" behind `# why:` pointers in code

## Process
- [process/](process/) — how work is tracked, tested and validated here
