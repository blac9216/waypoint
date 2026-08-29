# Decomposition

What comes out must pass github-workflow's readiness gate without a question: it should
look exactly like planning produced it.

**Home first, create second.** Before filing anything new, look for an existing open epic
whose scope already covers the ask (its body says so, and it has headroom under the
100-child cap). If one fits, the work is homed there under its existing milestone — no new
milestone, no new epic — and the decision record goes on that epic's thread. A new
milestone exists only for a story that spans more than one domain epic and fits none of
the open ones; a new epic only when a domain has no home. Creating structure that already
exists is the most common planning error and the hardest to undo.

**Milestone** — from github-workflow's `milestone-description` template. Written before
anything is assigned to it; `due_on` set by the projection step. Only feature stories
get one; hardening, CI and tooling stay milestone-less.

**Domain epics** — one per domain of the story, from the `issue-epic` template, `epic` +
`area:*`, more than one child each, under the milestone. Their bodies carry goal, scope
and a design pointer; the decision record from the interrogation is on the design-record
epic's thread.

**Issues** — from the bug / enhancement / chore templates, every section filled:
- **Acceptance criteria the reviewer can prove at merge.** Anything needing the real
  environment goes in *Verified expectation* as `pending-live`, not in the criteria.
- **`area:*`** (≥1, from `docs/process/labels.md`), type, severity for bugs, `priority:*`.
- **Dependencies** as native `blocked by` links — including across milestones. A
  **consumes / produces** line wherever a sibling relies on a contract this issue creates
  (an endpoint, a table, a module interface), so parallel implementers agree on names.
- **Estimate**: size class from the design and the files it will touch
  ([estimate.md](estimate.md)); **L is split before filing** — two issues with a
  dependency beat one that the reviewer will send back for decomposition.
- **Home**: sub-issue of its domain epic; milestone set.
- **Board**: Backlog (label + column). The owner releases to Ready.

**Order of filing**: milestone → epics → issues → dependencies (need both numbers) →
board columns. Record every number in scratch as it is created; a compaction mid-filing
must not lose the map. File everything before presenting — a plan that exists only in
chat is not a plan.

**Self-review before presenting**: no `<…>` placeholders left; no issue contradicts the
decision record; nothing outside the story's scope crept in; no criterion readable two
ways.
