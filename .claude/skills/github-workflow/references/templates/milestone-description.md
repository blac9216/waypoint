# Milestone Description Template

A milestone is a delivery story. Its description is the rolled-up state a newcomer reads
first. It is **rewritten wholly** — never patched — and rarely: when a story-wide
assumption changes, or when validation closes. Write it before assigning anything to the
milestone; it tells the assigner what belongs. Set a due date (target or actual close)
so the roadmap view can show it.

```markdown
## Goal
The delivery story in two or three sentences.

## Scope / Non-goals
What the story includes; what is explicitly another story.

## Design
Pointers into `docs/` — architecture, ADRs, contract, process. Never copies.

## Epics
- #N <domain> — <state in three words>
- …
Close gate: #<issue> under <epic>.

## Current state
The rolled-up summary. Replace the whole section when it changes.

## Decision ledger
- YYYY-MM-DD — <assumption that changed and the decision taken>. Entries are never deleted.

## Records
Design-record epics, grill threads, validation summaries — links only.
```
