# Estimation

Estimates exist to place work on the roadmap, not to hold anyone to a number. They are
per issue; everything else is derived.

## Calibration — `scripts/history.sh`

Read-only. For every closed issue with a merged PR it collects: start (first `assigned`
timeline event → first branch commit → PR opened, whichever exists first), PR opened,
merged, review rounds (count of `## PR Review — Changes Requested|Decomposition
Requested` comments), findings per round, additions/deletions/files, labels (area, type,
severity, priority), parent epic, milestone, the filed **Estimate** (size, cycle, due) if
the issue has one, the **metrics closing comment** if one exists, and deferred issues
the PR spawned (cross-references). Missing fields are recorded as missing, and every
aggregate states its sample size — a repository without the workflow yields a thinner
table, not a wrong one.

Output (scratch, JSON + a markdown view): per **area × size** bucket, `n`, p50/p80
cycle hours (start→merged; agent cycles are sub-day), p50 rounds, first-pass approval rate, median LOC. Size for
historical issues comes from the filed estimate when present, else from actual net LOC
(S ≤100, M ≤400, L >400). A second table compares **estimated vs actual** where both
exist — that is the calibration signal the owner asked for.

Defaults when a bucket has `n < 3`: fall back to the area's all-sizes median, then the
repository median, then the built-in defaults (S 2h · M 6h · L 16h, 1.5 rounds) — and say
which fallback was used on every estimate.

## Per-issue estimate

Size class from the design: the files it touches, whether it adds a migration, a UI
screen, a new transport — anything that has historically meant more rounds. Write the
**Estimate** section: `Size · est. cycle (bucket, n, fallback if any) · est. completion`.
Completion comes from sequencing ([timeline.md](timeline.md)), is rough, and is
re-projected by github-workflow at every session start.
