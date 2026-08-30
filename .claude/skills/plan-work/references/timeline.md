# Timeline placement

Issue-driven. Milestones never carry dependencies on each other — a milestone-level link
rots the moment one issue unblocks and the rest are still coupled.

`scripts/timeline.sh` (read-only) proposes; the agent applies `due_on` after the owner
confirms when anything is ambiguous.

1. Gather every open milestone's issues with their estimates and `blocked by` links
   (including links that cross milestones), and whether each milestone has **started**
   (any issue assigned, in progress or merged).
2. Per milestone: critical path through its own issues' dependencies using est. cycle
   days; total effort; projected duration = critical path adjusted by the observed
   parallelism (from history: median concurrent In-progress issues; default 1.5).
   The parallelism source is either `--parallelism` (explicit), `--history-dir`
   (explicit, pointed at `history.sh`'s `--out`), or a same-run default-`--out`
   guess — that last case is reported on stderr so a mismatched `--out` doesn't
   silently fall back to 1.5. Falling back to 1.5 is always reported on stderr, with
   wording that distinguishes the cause: `parallelism.txt` missing or empty reports
   "no history at `<path>`"; `parallelism.txt` present but failing the positive-number
   check reports "ignoring unusable parallelism `<value>` in `<path>`" instead.
3. Place: started milestones are **pinned** at their actual start; a milestone the owner
   is starting now begins **today** and overlaps whatever is running; unstarted
   milestones are laid out serially after the last scheduled one unless an issue
   dependency forces a different order. Existing `due_on` order breaks ties.
4. When two unstarted milestones have no dependency between them and no prior order,
   **ask the owner** which comes first.
5. Report: milestone → start, projected end, critical path length, effort, parallelism
   used, and the sample sizes behind the numbers. Apply `due_on` where it moved by more
   than a day.

`--milestones "A,B"` runs the same steps for the named milestones only (an exact,
comma-split match against milestone titles — not a substring match), holding
everything else fixed. Each split name is trimmed of leading/trailing whitespace
before matching, so a title with meaningful leading/trailing spaces can't be named
this way — use `--milestone` instead. A milestone with no open or closed issues
still gets a row, zeroed out, rather than disappearing from the report.

For a title containing a comma, or one with meaningful leading/trailing whitespace,
pass `--milestone <title>` instead (repeatable, one exact and untrimmed title per
flag). `--milestones` and `--milestone` combine into one selection. If any selection
is requested (either flag), a requested name that matches no open milestone is
reported on stderr; matching zero milestones in total is an error. An empty
`--milestones ""` or `--milestone ""` value is rejected outright — it can never mean
"select every open milestone".

## Exit codes

| Code | Meaning |
|---|---|
| `2` | Argument error — an unrecognized flag, a value-taking flag with no following value, an empty `--milestones`/`--milestone` value, or a `--defaults` value containing a part that isn't `S=<n>`, `M=<n>`, or `L=<n>` (a positive decimal number). |
| `3` | A `--milestones`/`--milestone` selection was requested but matched zero open milestones. |
| `4` | `--parallelism` was given a value that is not a positive decimal number matching `^[0-9]+([.][0-9]+)?$` (e.g. `0`, `-1`, `abc`, `.5`, `1e2`, and leading/trailing whitespace are all rejected; `0.5` is accepted). A `parallelism.txt` file with the same defect falls back to the 1.5 default instead of erroring — see "parallelism source" above. |
| `5` | A `blocked_by` cycle was detected while computing a milestone's critical path (jq's own error exit surfaces here). This is the only remaining cause of exit 5. |

## Flags

| Flag | Purpose |
|---|---|
| `--repo <owner/name>` | Target repository; defaults to the current repo. |
| `--milestones "A,B"` | Comma-split, trimmed, exact-match milestone title selection. |
| `--milestone <title>` | Repeatable, exact and untrimmed single-title selection; combines with `--milestones`. |
| `--parallelism <n>` | Explicit parallelism factor; overrides `--history-dir`/default. |
| `--history-dir <dir>` | Directory `history.sh` wrote `parallelism.txt` into. |
| `--defaults S=2,M=6,L=16` | Hour defaults per T-shirt size when an issue has no `est. cycle` hours. |
| `--out <dir>` | Output directory for `milestones.jsonl`, `issues.jsonl`, `projection.json`, `placement.json`, `timeline.md`. |
