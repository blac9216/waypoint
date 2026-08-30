# Research phase

Research exists to replace assumptions with facts before decisions are made on them.
Findings live on issues; the owner signs off before anything is built on them.

1. **Decide the lanes.** One lane per independent question (a vendor tool's real command
   surface; an API contract; what the predecessor system does; a security constraint).
   Three to six is typical; more means the ask is two stories.
2. **File the research epic** ([templates/research-epic.md](templates/research-epic.md))
   under the story's milestone (or milestone-less for a bounded ask), then one **lane
   issue** per question ([templates/research-lane.md](templates/research-lane.md)) as its
   children, `area:*` per lane, Backlog until dispatched.
3. **Dispatch lanes** as background agents (Sonnet; Opus only for a lane that must reason
   about security or contracts). Each lane writes its **findings comment**
   ([templates/lane-findings.md](templates/lane-findings.md)) on its own issue: facts with
   sources, what could not be established, and what it changes for the design. No
   committed files; evidence stays in scratch.
4. **Composite.** When every lane has reported, write the **composite findings comment**
   ([templates/composite-findings.md](templates/composite-findings.md)) on the research
   epic: the facts that change the design, ranked, with the decisions they force.
5. **Hard stop.** Post the **sign-off request**
   ([templates/signoff-request.md](templates/signoff-request.md)) and wait. Nothing
   downstream starts until the owner ratifies or amends the findings.
6. **Propagate.** Every affected existing issue gets a comment naming the finding and what
   it changes; the interrogation that follows takes the ratified findings as settled facts.
   Close the research epic (`completed`) once propagated.
