---
name: design-docs
description: Set up, apply and audit a repository's architecture-documentation framework — ADRs (MADR sections, generated status index), a C4-levelled architecture doc, a root CONTEXT.md glossary, Diátaxis-organised docs/, and the rationale index (`# why:` pointers from code to docs/rationale/). Use it whenever the owner asks to adopt or refresh documentation standards, "audit the docs", "check for doc drift", "normalise the ADRs", "where should this decision be written down", or whenever plan-work or interrogate reaches a durable design decision that needs an ADR, a rationale entry or an architecture-doc change — consult it before writing any of those, even if the owner did not name the skill.
---

# Design docs

The repository's design record is the thing agents read before they build and the thing
that rots first. This skill owns its **framework**: the standard the design set is written
to, the procedure for adopting that standard in a repository, the authoring rules other
skills consult when a decision becomes durable, and the audit that measures drift. It
does not decide designs (interrogate does), decompose work (plan-work does), or land
changes (github-workflow does). Keep those seams: in **audit** mode this skill edits
nothing and files nothing — it hands plan-work a gap report.

## Modes — pick one, say which

| Owner says | Mode | Read next |
|---|---|---|
| "adopt the docs standard", "set up ADRs/rationale here", "refresh the framework" | **adopt** | [references/adopt.md](references/adopt.md) |
| a design decision needs writing down; plan-work/interrogate call for an ADR, rationale entry, C4 or glossary change | **author** | [references/authoring.md](references/authoring.md) |
| "audit the docs", "doc drift", "normalise the ADRs", morning cleanup | **audit** | [references/audit.md](references/audit.md) |

Every mode starts the same way: read `docs/process/documentation.md` if it exists — it is
the repository's adopted shape (which doc kinds exist, rationale areas, ADR range, Diátaxis
directories) and overrides the defaults in the standard. If it does not exist, the
repository has not adopted the framework: **adopt** is the only valid mode, and any other
request is answered by proposing adoption first.

## The standard in one screen

[references/standard.md](references/standard.md) is normative; this is the map.

- **ADRs** — `docs/adr/NNNN-<slug>.md`, MADR sections (Context · Decision Drivers ·
  Considered Options · Decision · Consequences), a machine-readable `Status:` /
  `Supersedes:` / `Superseded-by:` (whole) or `Amends:` / `Amended-by:` (partial) header block, immutable Context and Decision once
  accepted. `docs/adr/README.md` carries a **generated** status table
  (`scripts/adr-index.sh`) so an agent knows what is active without opening 25 files.
- **Rationale index** — code carries only section markers and one-line warnings; a warning
  that needs a "why" points at `docs/rationale/<area>.md#<kebab-slug>`. Entries are 2–6
  lines plus a `Refs:` line. Repo-wide; slugs unique per file (`scripts/check-pointers.sh`).
- **architecture.md** — C4 levels: Context → Container → Component, each with an inline
  mermaid diagram. No level 4 (code) — that is what the code is for.
- **CONTEXT.md** at the repo root — the glossary, terms only, no implementation.
  `docs/domain-model.md` (or the repo's equivalent) holds relationships and rules and
  must use the glossary's words.
- **Diátaxis** — every doc lives in one of `docs/{tutorials,how-to,reference,explanation}/`
  and declares its kind. ADRs and rationale are explanation; the API contract is reference.
- **Specs are never committed.** Designs are decided on GitHub (interrogate → plan-work);
  what survives the build is written into the design set. There is no `docs/design/` or
  `docs/superpowers/`.

## Scripts

All bash, shellcheck-clean, tested under `tests/` (the repository skills harness runs
them). Each exits 0 clean / 1 findings / 2 usage.

| Script | Does | Used by |
|---|---|---|
| `scripts/check-pointers.sh [--root R] [--format text\|json]` | every `# why:` pointer resolves; slugs unique; entry length and `Refs:` | audit; repo CI (adopt wires it) |
| `scripts/adr-index.sh [--root R] [--check\|--write]` | generate the ADR status table; `--check` fails on drift, `--write` updates README | author (after any ADR change); audit; CI |
| `scripts/audit.sh --out <scratch-path> [--root R]` | runs Tier 1 checks, writes the gap report scaffold with Tier 2 agent tasks listed | audit |

## Where this sits in the flow

```
interrogate ──decisions──▶ plan-work ──issues──▶ github-workflow ──PRs──▶ github-pr-review
      │                        │                       │
      └── author mode ◀────────┘                       └── check-pointers / adr-index in CI
                                                              ▲
design-docs audit ──gap report (scratch)──▶ plan-work ────────┘
```

The superpowers `brainstorming` architectural path (committed specs → writing-plans) is
**not** used in repositories that adopt this framework; `documentation.md` says so, and
its useful ideas — the spike/bounded/architectural ratchet, "design it twice", the
three-part ADR trigger test — live in [references/authoring.md](references/authoring.md).

## Rules that do not bend

- Facts are looked up; decisions are the owner's. An audit finding is a fact; whether to
  fix it is a decision plan-work takes to the owner.
- Never reconstruct history silently. A backfilled ADR section says it was backfilled and
  from where ([references/standard.md#normalising-existing-adrs](references/standard.md#normalising-existing-adrs)).
- Provenance lives in exactly one place per mechanism: the ADR's header and `Refs:` lines,
  never in code comments.
- The gap report is ephemeral: scratch only, never committed, never pasted whole into an
  issue — plan-work extracts what it needs.
