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
   parallelism (from history: median concurrent In-progress issues; default 1.5). An
   issue body's `est. cycle` value that isn't a plain ASCII decimal number (e.g.
   `1.2.3`, or a non-ASCII digit such as `٢`), or that is a well-formed number greater
   than the `MAX_HOURS` sanity ceiling (`100000`), is treated like a missing estimate —
   it falls back to that issue's size default (or the `M` default if it has no
   size) — and is reported on stderr naming the issue number, the offending
   text, and which of the two distinct causes applies (`"an unparseable est.
   cycle value"` vs. `"an est. cycle value … over the 100000-hour
   ceiling"` — the emitted line expands the ceiling, so the literal token
   `MAX_HOURS` never appears in it), wording the fallback to match which one
   actually applied (`"falling
   back to its size default"` when the issue has a `Size:` label, `"falling
   back to the M default (no size)"` when it doesn't); it never aborts the run. The
   ceiling exists because jq 1.6 SIGABRTs (undocumented exit `134`) when a
   sufficiently large number (roughly 15+ digits) reaches the per-milestone
   projection stage — see #1269. The same `MAX_HOURS` ceiling bounds `--defaults`
   S/M/L values and `--parallelism` (flag or `parallelism.txt`): a flag value above
   it is an argument error (`--defaults` exit `2`, `--parallelism` exit `4`), and a
   `parallelism.txt` value above it falls back to the `1.5` default like any other
   unusable file value.
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

## `issues.jsonl` record fields

Each line is one open-milestone issue as read (before critical-path/effort computation).
`projection.json`'s `sources` map and `timeline.md`'s "estimate sources" column are a
per-milestone count grouped by `hours_source`, computed over **open, non-`epic`**
records only (while `issues.jsonl` itself holds every record read, including closed
issues and epics, so its line count exceeds the map's totals); `placement.json` and
`timeline.md` carry that same map through unchanged.

| Field | Meaning |
|---|---|
| `size` | The `Size:` letter (`S`/`M`/`L`) parsed from the issue body's `## Estimate` section, or `null` if the issue has no `Size:` line. |
| `hours` | The hour value actually used for critical-path/effort math: the parsed `est. cycle` value when present and well-formed, otherwise the size default (or the `M` default with no size). |
| `hours_source` | Which of those `hours` came from — see the vocabulary below. |
| `malformed_estimate` | The raw, unparseable `est. cycle` text (e.g. `1.2.3`, `٢`) when the issue had one, otherwise `null`. |

### `hours_source` vocabulary

| Value | Meaning | Emitted when |
|---|---|---|
| `estimate` | `hours` came from a well-formed, plain-ASCII-decimal `est. cycle` value in the issue body. | The issue has a parseable `est. cycle` value. |
| `size-default` | `hours` came from the size's `--defaults` value (built-in `S=2,M=6,L=16` unless overridden). | The issue has no `est. cycle` value (and none was present to be malformed) but does have a `Size:` label. |
| `no-estimate-default-M` | `hours` came from the `M` default. | The issue has neither an `est. cycle` value nor a `Size:` label. |
| `malformed-estimate-default-S` \| `-M` \| `-L` | `hours` came from that size's default because the in-body `est. cycle` value was present but unparseable (see `malformed_estimate`). | The issue has an unparseable `est. cycle` value and a `Size:` label of `S`, `M`, or `L` respectively. |
| `malformed-estimate-default-M` | `hours` came from the `M` default because the in-body `est. cycle` value was present but unparseable and the issue has no `Size:` label. | The issue has an unparseable `est. cycle` value and no `Size:` label. Same string as the sized `-M` case above — the malformed-with-`Size:M`-label and malformed-with-no-label paths both resolve to `M` and are not distinguishable from `hours_source` alone; `malformed_estimate` is non-null in both. |

## Exit codes

| Code | Meaning |
|---|---|
| `2` | Argument error — an unrecognized flag, a value-taking flag with no following value, an empty `--milestones`/`--milestone` value, or a `--defaults` value that is empty/whitespace-only or contains a part that isn't `S=<n>`, `M=<n>`, or `L=<n>` with `n` an ASCII decimal number greater than zero and no greater than the `MAX_HOURS` ceiling (`100000`) (an empty part, a trailing comma, or a non-ASCII digit such as `٢` is rejected too). |
| `3` | A `--milestones`/`--milestone` selection was requested but matched zero open milestones. |
| `4` | `--parallelism` was given a value that is not a positive decimal number no greater than `100000` (the same shared sanity ceiling as `MAX_HOURS`, applied here to a parallelism factor rather than hours), matching `^[[:digit:]]+([.][[:digit:]]+)?$` (ASCII digits only, so non-ASCII digits such as `٢` are rejected too; `0`, `-1`, `abc`, `.5`, `1e2`, and leading/trailing whitespace are all rejected; `0.5` is accepted). A `parallelism.txt` file with the same defect falls back to the 1.5 default instead of erroring — see "parallelism source" above. |
| `5` | A `blocked_by` cycle was detected while computing a milestone's critical path (jq's own error exit surfaces here). A cycle is the only cause reachable from the script's own flag values — a `--defaults` value can no longer reach exit 5, since it is either rejected with exit 2 or completed from the built-in table, and a malformed or over-`MAX_HOURS` in-body `est. cycle` value can't either (both fall back instead, see above) — but `5` is jq's generic error exit, so an unexpected jq failure would surface as `5` as well. Exit `134` (jq SIGABRT) is not reachable from any input this script accepts — see #1269. |

## Flags

| Flag | Purpose |
|---|---|
| `--repo <owner/name>` | Target repository; defaults to the current repo. |
| `--milestones "A,B"` | Comma-split, trimmed, exact-match milestone title selection. |
| `--milestone <title>` | Repeatable, exact and untrimmed single-title selection; combines with `--milestones`. |
| `--parallelism <n>` | Explicit parallelism factor; overrides `--history-dir`/default. |
| `--history-dir <dir>` | Directory `history.sh` wrote `parallelism.txt` into. |
| `--defaults S=2,M=6,L=16` | Hour defaults per T-shirt size when an issue has no `est. cycle` hours (`M` is also the fallback for an issue with no size at all). Any subset of sizes may be given; an omitted size keeps its built-in default from `S=2,M=6,L=16`, and a size given twice resolves last-wins. Each value must be greater than zero and no greater than the `MAX_HOURS` ceiling (`100000`); an empty value, an empty part or a trailing comma is an argument error (exit 2). |
| `--out <dir>` | Output directory for `milestones.jsonl`, `issues.jsonl`, `projection.json`, `placement.json`, `timeline.md`. |
