# The design-docs standard

Normative. A repository adopts it by writing `docs/doc-manifest.md`
([../templates/doc-manifest.md](../templates/doc-manifest.md)), which may narrow but not
contradict what is here. Each section states the rule, then why, then what the audit checks.

Contents: [Design set](#design-set) · [ADRs](#adrs) · [ADR index](#adr-index) ·
[Normalising existing ADRs](#normalising-existing-adrs) · [Rationale index](#rationale-index) ·
[Architecture doc (C4)](#architecture-doc-c4) · [Glossary (CONTEXT.md)](#glossary-contextmd) ·
[Diátaxis layout](#diátaxis-layout) · [What is never committed](#what-is-never-committed)

## Design set

The documents this standard governs: ADRs, rationale files, the architecture doc, the
glossary, the domain model, the security model, the API contract, the roadmap — whatever
`doc-manifest.md` lists under *design set*. Process docs (`docs/process/`) belong to
configure-workflow; `AGENTS.md` and testing docs are outside the set but are audited for
pointer validity because they link into it.

## ADRs

**Rule.** One file per decision, `docs/adr/NNNN-<kebab-slug>.md`, numbers sequential and
never reused. Header block, then MADR sections in this order:

```markdown
# ADR-NNNN: <title as a decision, not a topic>

Status: Proposed | Accepted | Superseded | Deprecated
Supersedes: 0006          (whole-ADR replacement; omit if none; comma-separate several)
Superseded-by: 0013, 0014 (omit if none)
Amends: 0013              (partial supersede — this ADR replaces part of 0013; omit if none)
Amended-by: 0017          (omit if none)
Date: YYYY-MM-DD

## Context
## Decision Drivers
## Considered Options
## Decision
## Consequences
```

- *Context* — the situation and forces, written so a reader with no history understands
  why a decision was needed.
- *Decision Drivers* — bullet list of the criteria that mattered (constraints, qualities,
  costs). These are what the options were judged against.
- *Considered Options* — each option with one or two lines of pros/cons. Rejected options
  are recorded because they will be proposed again otherwise.
- *Decision* — what was chosen, stated as the thing itself ("Runners claim their own
  jobs"), numbered when there are several parts.
- *Consequences* — what follows, good and bad, and what it commits the project to.

**Immutability.** Once `Accepted`, Context, Decision Drivers, Considered Options and
Decision are byte-immutable. Consequences may be appended to with a consequence *of* the
standing decision (never a narrowing — "instead" or "no longer" means a superseding ADR),
attributable to the issue that established it. A **partial supersede** — the new ADR
replaces one part of an older one that otherwise stands — uses `Amends:` / `Amended-by:`;
the older ADR keeps `Status: Accepted`, and the new ADR's Context names the part replaced
("replaces §4 of ADR-0013"). Header lines carry numbers only; the *which part* lives in
prose.

**Why.** MADR's extra two sections are where the value is: the drivers and the rejected
options are what stop the next engineer from relitigating the choice. The header block is
machine-readable so the index below cannot lie. Immutability is what makes an ADR a record
rather than a wiki page.

**Audit.** File name pattern; header block present and parseable; all five sections
present (a placeholder body is a finding for ADRs dated after adoption); status/link
symmetry (A supersedes B ⇔ B superseded-by A; A amends B ⇔ B amended-by A); a Superseded
status iff a Superseded-by line; an Amended-by line never changes status.

## ADR index

**Rule.** `docs/adr/README.md` holds the amend/supersede rules and, between
`<!-- adr-index:start -->` / `<!-- adr-index:end -->`, the table produced by
`scripts/adr-index.sh --write`. Nobody edits the table by hand. `AGENTS.md` tells agents
to read the table first and open only the active ADRs they need.

**Why.** Twenty-five ADRs are too many to load to learn that six are superseded. A
generated table is the one form that stays true.

**Audit.** `adr-index.sh --check` — drift between the table and the files is a finding.

## Normalising existing ADRs

A repository adopting this standard with ADRs already accepted normalises them
**uniformly** — every ADR to the full MADR frame — under a single new ADR ("Adopt MADR
format; normalise NNNN–NNNN") that records the rule change and authorises exactly one
backfill pass. In that pass:

- existing Context and Decision prose is kept verbatim and only re-homed under the new
  headings;
- Decision Drivers and Considered Options are reconstructed from the originating issues
  and PRs, and each reconstructed section opens with
  `_Backfilled under ADR-NNNN from #<issue>, #<pr>._` so a reader can tell reconstruction
  from the original text;
- the README rule stays, with one sentence pointing at the normalisation ADR as the
  exception.

The PRs that do this carry a reviewer note: *review as formatting + backfill only —
verify the original Context and Decision are byte-identical (diff them against `main`),
then scrutinise the backfilled sections against their cited sources.* A reviewer who
re-argues an accepted decision in one of these PRs is reviewing the wrong thing.

## Rationale index

**Rule.** Code carries short section markers and terse one-line warnings only — no issue,
ADR or PR numbers in code. A warning that needs a why ends with a pointer:

```
# why: docs/rationale/<area>.md#<kebab-slug>      (also // why: and <!-- why: -->)
```

`docs/rationale/<area>.md` — one file per code area (`deploy`, `backend`, `frontend`,
`ci`…), listed in `doc-manifest.md`. One `##` section per source file or small directory;
one `###` entry per slug. **Slugs are unique across the whole file** (GitHub anchors are
file-global; a duplicate silently becomes `#slug-1` and the pointer resolves to the wrong
entry while looking correct in review) — prefix with the service or file
(`postgres-healthcheck-start-period`, not `healthcheck-start-period`). Entry body is 2–6
lines of reasoning, trade-off or constraint — never a restatement of the code — and ends
with a `Refs:` line (issues, ADRs, PRs): the only place that provenance lives.

**Why.** Comments that cite tickets are archaeology by the time anyone reads them; the
reasoning is what the reader needs, and it needs one home that a link can reach. The
6-line cap is a claim-count limit: an entry that needs more is two entries.

**Audit.** `check-pointers.sh`: every pointer resolves, slugs unique, 2–6 lines, `Refs:`
present. Repo-wide; areas without a rationale file yet are simply areas with no pointers.

## Architecture doc (C4)

**Rule.** `docs/explanation/architecture.md` (or the adopted path) is organised as three
C4 levels, each a `##` heading with an inline mermaid diagram followed by prose:

1. **Context** — the system as one box, its users and the external systems it touches.
2. **Container** — the deployable/runnable units (services, databases, proxies, runners)
   and the protocols between them.
3. **Component** — inside each container that has meaningful internal structure, the
   major components and their responsibilities.

Cross-cutting concerns (job engine, modes, update flow) follow the levels as their own
sections. No level 4; code is documented by code and the rationale index. Diagrams are
mermaid inline so the picture and the prose change in the same diff; `docs/images/` is
for screenshots.

**Why.** "Where does X live?" should have exactly one answer per level of zoom, and a
diagram nobody can regenerate is a diagram that is wrong.

**Audit.** The three level headings exist in order; each has at least one ` ```mermaid `
block; container names in the Container diagram match the deploy topology's service
names (Tier 2, agent-checked).

## Glossary (CONTEXT.md)

**Rule.** Root `CONTEXT.md` is the glossary: one entry per domain term, a one-line
definition, the canonical spelling, and rejected synonyms ("not: host, machine"). Nothing
about implementation. The domain-model doc, ADRs and architecture doc use the glossary's
words; when a design session sharpens or splits a term, `CONTEXT.md` is updated in the
same PR. A multi-context repository uses `CONTEXT-MAP.md` at root pointing to per-context
glossaries.

**Why.** Vocabulary drift is the earliest form of design drift, and a root file is the one
place every agent and the domain-modeling skill look.

**Audit.** Terms unique; each bolded/headed term in the domain-model doc appears in the
glossary; glossary entries contain no code paths or type names.

## Diátaxis layout

**Rule.** `docs/` has four directories — `tutorials/`, `how-to/`, `reference/`,
`explanation/` — plus `adr/`, `rationale/`, `process/`, `images/`. Every markdown file
under `docs/` (outside `process/`) sits in one of the four and carries `Kind: <kind>` on
the line after its title. ADRs and rationale are explanation by construction and carry no
marker. `docs/README.md` and `docs/doc-manifest.md` are exempt, sitting at the docs root
by design rather than in a kind directory; `docs/README.md` indexes by kind (generated
tables are welcome; hand-written is acceptable because the audit checks it).

Assignment guide: architecture, domain model, security model, roadmap → explanation;
API contract, CLI/config references, label catalogues → reference; deploy/upgrade/bring-up
runbooks → how-to; first-run walkthroughs → tutorials.

**Why.** The four kinds have different readers and different failure modes; a runbook
that explains and a reference that teaches both fail their reader. Directories make the
kind visible in `ls` and in every link.

**Audit.** Every doc in a kind directory; `Kind:` marker matches its directory; README
index lists every doc and nothing that does not exist.

## What is never committed

Design specs, interrogation records, plans, research findings, audit gap reports. They
live on GitHub objects (epic threads, issues) or in scratch. The repository holds the
outcome — an ADR, a rationale entry, an architecture-doc change — never the deliberation.
An agent that auto-loads a skill wanting `docs/superpowers/specs/` or `docs/design/`
declines that path; `doc-manifest.md` records the ruling so the decline has a citation.
