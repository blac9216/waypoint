# Validation Epic Template

Opened when a live-validation loop starts; one per loop. Home for every bug the run files.
Closed (reason `completed`) by the validation agent when the run is green. Labels: `epic`
+ the validated domain's `area:*`. Same milestone as the validated epic (or none).

```markdown
## Validates
Epic #<E> — <name>. Issues whose `Verified` this loop decides: #a, #b, #c.

## Goal statements to prove
- <the story's claim, in operator terms — "a real STIG scan of a 9.x host produces HDF+CKL">
- …

## Steps
1. <step> — proves: <issue(s)> — success means: <observable>
2. …

## Bar
Zero-workaround: no manual grants, no copying files into containers, no configuration
beyond documented operator settings, no restarts. Any step needing one is a FAIL and a bug.

## Runs
- Run 1 — <date> — <PASS/FAIL counts> — summary comment: <link>
- Run 2 — …

## Status
One line: run number, what is being fixed, what is next.
```
