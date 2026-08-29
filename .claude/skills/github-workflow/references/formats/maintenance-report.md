# Maintenance report format

One `maintenance` event in the session log per pass:

```json
{"ts":"…","event":"maintenance","claim":"…","trigger":"start|resume|wave|serial-3|morning-cleanup",
 "triage":{"drained":N,"ready":N,"backlog":N,"duplicates":N,"folded":N},
 "host":{"load":x,"mem_free_pct":x,"disk_free_gb":x,"disk_delta_gb":x,"leftover_procs":N},
 "cleanup":{"containers":N,"images":N,"volumes":N,"networks":N,"worktrees":N,"files":N},
 "rules":{"main_checkout_dirty":false,"files_outside_allowed":N,"evidence_in_tree":N,"subagent_violations":N},
 "state":{"open_off_board":N,"stale_claims":N,"epics_under_2":N,"epics_over_cap":N,"closed_parent_open_child":N,"pending_live":N,"milestone_rewritten":false},
 "actions":["…"]}
```
