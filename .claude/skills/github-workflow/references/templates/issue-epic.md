# Epic Issue Template

An epic is one domain inside a delivery story (or a milestone-less theme). It exists so
the larger intent survives context compaction: its **body** holds the durable goal and
scope; its **comment thread** holds the events — what landed, what a review found, what
was decided. Nothing in the body is a log. Always more than one child; never more than
100 (closed children count). Labels: `epic` + ≥1 `area:*`. Assign the milestone if the
domain belongs to a story.

```markdown
## Goal
The single objective this domain delivers, in two or three sentences a reader can
absorb with nothing else loaded.

## Motivation
Why it matters and what it unblocks.

## Scope
What is in.

### Non-goals
What is deliberately out — usually "owned by sibling epic #N".

## Design
Pointers into `docs/` (architecture, ADRs, contract sections). Point, do not copy. Owner
decisions for this domain are recorded as comments on this epic.

## Sub-issues
Linked as native sub-issues; order lives in `blocked by` dependencies and `priority:*`.
Any hard ordering that dependencies cannot express is stated here in one line.

## Status
One line: what is in flight, what is blocked, what is next. Rewritten whole on every touch.
```
