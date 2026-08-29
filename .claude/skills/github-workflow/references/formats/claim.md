# Claim value format

Board field `Claimed by` (text): `<repo-slug>-<NN> @ <ISO-8601 UTC>`

- `repo-slug`: the repository name, lowercase.
- `NN`: two-digit number, lowest free at session start.
- timestamp: last refresh (start, wave start, merge). Stale rule: >24h old AND no
  PR/commit/comment activity on the item since.

Example: `waypoint-03 @ 2026-08-29T15:10Z`
