#!/usr/bin/env bash
# test_adr_index.sh — regression test for adr-index.sh's table generation,
# --check/--write round trip, and cross-ADR consistency checks (asymmetric
# supersedes/superseded-by links, missing/bad Status, missing targets).
# Self-contained: builds fixture ADR trees in a private scratch dir under
# $TMPDIR (or /tmp), removed on exit. No network access.
#
# Uses [[:digit:]] rather than [0-9] in any pattern match, per this repo's
# locale-collation-range convention (a bash `[0-9]` range test matches
# non-ASCII digits under a UTF-8 locale).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADR_INDEX_SH="$SCRIPT_DIR/../scripts/adr-index.sh"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/adr-index-test.XXXXXX")"
cleanup(){ rm -rf "$WORK"; }
trap cleanup EXIT

PASS=0
FAIL=0

note() { printf '%s\n' "$1"; }
pass() { PASS=$((PASS + 1)); note "PASS: $1"; }
fail() { FAIL=$((FAIL + 1)); note "FAIL: $1"; }

# fresh_dir NAME — new empty ADR dir under $WORK/NAME, path echoed.
fresh_dir() {
  local d="$WORK/$1"
  mkdir -p "$d"
  printf '%s' "$d"
}

# write_adr DIR FILENAME CONTENT
write_adr() {
  local dir="$1" name="$2" content="$3"
  printf '%s' "$content" >"$dir/$name"
}

expected_table_clean='| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |
|---|---|---|---|---|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first | Accepted | - | - | - | - | v1 ships as a Docker Compose stack. |
| [0002](0002-database.md) | Postgres for app data | Accepted | - | - | - | - | One PostgreSQL instance, with separate databases. |'

# ---------------------------------------------------------------------------
# Case 1: clean set renders the expected table.
# ---------------------------------------------------------------------------
d="$(fresh_dir case1)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
write_adr "$d" 0002-database.md '# 0002. Postgres for app data

**Status:** Accepted

## Decision

One PostgreSQL instance, with separate databases.
'
if actual="$("$ADR_INDEX_SH" --adr-dir "$d")" && [[ "$actual" == "$expected_table_clean" ]]; then
  pass "adr-index.sh renders the expected table for a clean ADR set"
else
  fail "adr-index.sh clean-set table mismatch: $actual"
fi

# ---------------------------------------------------------------------------
# Case 2: --check passes when README.md's block matches.
# ---------------------------------------------------------------------------
d="$(fresh_dir case2)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
cat >"$d/README.md" <<'EOF'
# ADRs

<!-- adr-index:start -->
| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |
|---|---|---|---|---|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first | Accepted | - | - | - | - | v1 ships as a Docker Compose stack. |
<!-- adr-index:end -->
EOF
if "$ADR_INDEX_SH" --adr-dir "$d" --check >"$WORK/case2.out" 2>"$WORK/case2.err"; then
  pass "adr-index.sh --check passes on a matching README.md block"
else
  fail "adr-index.sh --check unexpectedly failed on a matching block: $(cat "$WORK/case2.err")"
fi

# ---------------------------------------------------------------------------
# Case 3: --check detects drift.
# ---------------------------------------------------------------------------
d="$(fresh_dir case3)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
cat >"$d/README.md" <<'EOF'
# ADRs

<!-- adr-index:start -->
| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |
|---|---|---|---|---|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first (STALE) | Accepted | - | - | - | - | v1 ships as a Docker Compose stack. |
<!-- adr-index:end -->
EOF
set +e
"$ADR_INDEX_SH" --adr-dir "$d" --check >"$WORK/case3.out" 2>"$WORK/case3.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'README.md: ADR_INDEX_DRIFT' "$WORK/case3.err"; then
  pass "adr-index.sh --check reports ADR_INDEX_DRIFT on a stale README.md block"
else
  fail "adr-index.sh --check did not detect drift (rc=$rc): $(cat "$WORK/case3.err")"
fi

# --check also detects missing markers entirely.
d="$(fresh_dir case3b)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
printf '# ADRs\n\nno markers here\n' >"$d/README.md"
set +e
"$ADR_INDEX_SH" --adr-dir "$d" --check >"$WORK/case3b.out" 2>"$WORK/case3b.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'README.md: ADR_INDEX_DRIFT' "$WORK/case3b.err"; then
  pass "adr-index.sh --check reports ADR_INDEX_DRIFT when markers are absent"
else
  fail "adr-index.sh --check did not detect absent markers (rc=$rc): $(cat "$WORK/case3b.err")"
fi

# ---------------------------------------------------------------------------
# Case 4: --write round-trips (write then --check passes; re-write is stable).
# ---------------------------------------------------------------------------
d="$(fresh_dir case4)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
cat >"$d/README.md" <<'EOF'
# ADRs

Some prose before the index.

<!-- adr-index:start -->
stale content
<!-- adr-index:end -->

Some prose after the index.
EOF
if "$ADR_INDEX_SH" --adr-dir "$d" --write >/dev/null \
   && "$ADR_INDEX_SH" --adr-dir "$d" --check >/dev/null 2>"$WORK/case4.err" \
   && grep -q 'Some prose before' "$d/README.md" \
   && grep -q 'Some prose after' "$d/README.md"; then
  pass "adr-index.sh --write round-trips into a --check pass and preserves surrounding prose"
else
  fail "adr-index.sh --write round-trip failed: $(cat "$WORK/case4.err" 2>/dev/null)"
fi

# --write also works when README.md has no markers yet (creates them).
d="$(fresh_dir case4b)"
write_adr "$d" 0001-packaging.md '# ADR-0001: Docker Compose first

Status: Accepted

## Decision

v1 ships as a Docker Compose stack.
'
printf '# ADRs\n' >"$d/README.md"
if "$ADR_INDEX_SH" --adr-dir "$d" --write >/dev/null \
   && "$ADR_INDEX_SH" --adr-dir "$d" --check >/dev/null 2>"$WORK/case4b.err"; then
  pass "adr-index.sh --write creates markers when absent and the result passes --check"
else
  fail "adr-index.sh --write-without-markers round-trip failed: $(cat "$WORK/case4b.err" 2>/dev/null)"
fi

# ---------------------------------------------------------------------------
# Case 5: asymmetric supersedes/superseded-by link is reported.
# ---------------------------------------------------------------------------
d="$(fresh_dir case5)"
write_adr "$d" 0001-old.md '# ADR-0001: Old thing

Status: Superseded

## Decision

Old decision.
'
write_adr "$d" 0002-new.md '# ADR-0002: New thing

Status: Accepted
Supersedes: 0001

## Decision

New decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case5.out" 2>"$WORK/case5.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'ADR_LINK_ASYMMETRIC' "$WORK/case5.err"; then
  pass "adr-index.sh reports ADR_LINK_ASYMMETRIC when Supersedes has no matching Superseded-by"
else
  fail "adr-index.sh did not detect asymmetric link (rc=$rc): $(cat "$WORK/case5.err")"
fi

# ---------------------------------------------------------------------------
# Case 6: missing Status line is reported.
# ---------------------------------------------------------------------------
d="$(fresh_dir case6)"
write_adr "$d" 0001-nostatus.md '# ADR-0001: No status here

## Decision

Some decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case6.out" 2>"$WORK/case6.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'ADR_NO_STATUS' "$WORK/case6.err"; then
  pass "adr-index.sh reports ADR_NO_STATUS when no Status line is present"
else
  fail "adr-index.sh did not detect missing status (rc=$rc): $(cat "$WORK/case6.err")"
fi

# ---------------------------------------------------------------------------
# Case 7: reference to a non-existent ADR number is reported.
# ---------------------------------------------------------------------------
d="$(fresh_dir case7)"
write_adr "$d" 0002-new.md '# ADR-0002: New thing

Status: Accepted
Supersedes: 0001

## Decision

New decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case7.out" 2>"$WORK/case7.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'ADR_MISSING_TARGET' "$WORK/case7.err"; then
  pass "adr-index.sh reports ADR_MISSING_TARGET when Supersedes references an absent ADR"
else
  fail "adr-index.sh did not detect missing target (rc=$rc): $(cat "$WORK/case7.err")"
fi

# ---------------------------------------------------------------------------
# Case 8: bad status value and superseded-wrong-status are reported.
# ---------------------------------------------------------------------------
d="$(fresh_dir case8)"
write_adr "$d" 0001-bad.md '# ADR-0001: Bad status

Status: Draft

## Decision

Some decision.
'
write_adr "$d" 0002-wrongstatus.md '# ADR-0002: Wrong status

Status: Accepted
Superseded-by: 0001

## Decision

Some decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case8.out" 2>"$WORK/case8.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] \
   && grep -q 'ADR_BAD_STATUS' "$WORK/case8.err" \
   && grep -q 'ADR_SUPERSEDED_WRONG_STATUS' "$WORK/case8.err"; then
  pass "adr-index.sh reports ADR_BAD_STATUS and ADR_SUPERSEDED_WRONG_STATUS"
else
  fail "adr-index.sh did not detect bad-status/wrong-status findings (rc=$rc): $(cat "$WORK/case8.err")"
fi

# ---------------------------------------------------------------------------
# Case 9: free-form prose after the status value is flagged (ADR_STATUS_TRAILING)
d="$WORK/case9"; mkdir -p "$d"
write_adr "$d" 0001-trailing.md '# ADR-0001: Trailing prose

Status: Accepted; delivery superseded by [ADR-0002](0002-x.md)

## Decision

Some decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case9.out" 2>"$WORK/case9.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'ADR_STATUS_TRAILING' "$WORK/case9.err"; then
  pass "adr-index.sh reports ADR_STATUS_TRAILING for prose after the status value"
else
  fail "adr-index.sh did not flag trailing status prose (rc=$rc): $(cat "$WORK/case9.err")"
fi

# ---------------------------------------------------------------------------
# Case 10: a symmetric Amends/Amended-by pair renders both cells and exits 0;
# an Amended-by line never changes status expectations (status stays
# Accepted, no ADR_SUPERSEDED_WRONG_STATUS).
# ---------------------------------------------------------------------------
d="$(fresh_dir case10)"
write_adr "$d" 0001-old.md '# ADR-0001: Old thing

Status: Accepted
Amended-by: 0002

## Decision

Old decision.
'
write_adr "$d" 0002-new.md '# ADR-0002: New thing

Status: Accepted
Amends: 0001

## Decision

New decision.
'
if actual="$("$ADR_INDEX_SH" --adr-dir "$d")" \
   && grep -qF '| [0001](0001-old.md) | Old thing | Accepted | - | - | - | 0002 | Old decision. |' <<<"$actual" \
   && grep -qF '| [0002](0002-new.md) | New thing | Accepted | - | - | 0001 | - | New decision. |' <<<"$actual"; then
  pass "adr-index.sh renders a symmetric Amends/Amended-by pair in both cells and exits 0"
else
  fail "adr-index.sh symmetric amends/amended-by pair mismatch: $actual"
fi

# ---------------------------------------------------------------------------
# Case 11: asymmetric Amends (no reciprocal Amended-by) is reported.
# ---------------------------------------------------------------------------
d="$(fresh_dir case11)"
write_adr "$d" 0001-old.md '# ADR-0001: Old thing

Status: Accepted

## Decision

Old decision.
'
write_adr "$d" 0002-new.md '# ADR-0002: New thing

Status: Accepted
Amends: 0001

## Decision

New decision.
'
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/case11.out" 2>"$WORK/case11.err"
rc=$?
set -e
if [[ "$rc" -ne 0 ]] && grep -q 'ADR_LINK_ASYMMETRIC' "$WORK/case11.err"; then
  pass "adr-index.sh reports ADR_LINK_ASYMMETRIC when Amends has no matching Amended-by"
else
  fail "adr-index.sh did not detect asymmetric amends link (rc=$rc): $(cat "$WORK/case11.err")"
fi

# ---------------------------------------------------------------------------
# Case 12: --write updates the README block even when findings are present
# (a bad-status ADR is still a finding on stderr, exit 1, but the block is
# refreshed).
# ---------------------------------------------------------------------------
d="$(fresh_dir case12)"
write_adr "$d" 0001-bad.md '# ADR-0001: Bad status

Status: Draft

## Decision

Some decision.
'
cat >"$d/README.md" <<'EOF'
# ADRs

<!-- adr-index:start -->
stale content
<!-- adr-index:end -->
EOF
before="$(cat "$d/README.md")"
set +e
"$ADR_INDEX_SH" --adr-dir "$d" --write >"$WORK/case12.out" 2>"$WORK/case12.err"
rc=$?
set -e
after="$(cat "$d/README.md")"
if [[ "$rc" -eq 1 ]] \
   && grep -q 'ADR_BAD_STATUS' "$WORK/case12.err" \
   && [[ "$before" != "$after" ]] \
   && grep -qF '0001-bad.md' "$d/README.md"; then
  pass "adr-index.sh --write updates the README block even when findings are present (rc=1)"
else
  fail "adr-index.sh --write-with-findings failed (rc=$rc): $(cat "$WORK/case12.err")"
fi

# ---------------------------------------------------------------------------
# Case: an ADR directory that exists but is empty renders an empty table, exit 0
d="$WORK/empty"; mkdir -p "$d"
set +e
"$ADR_INDEX_SH" --adr-dir "$d" >"$WORK/empty.out" 2>"$WORK/empty.err"
rc=$?
set -e
if [[ "$rc" -eq 0 ]] && grep -q '^| # |' "$WORK/empty.out"; then
  pass "adr-index.sh handles an empty ADR directory"
else
  fail "adr-index.sh failed on an empty ADR directory (rc=$rc): $(cat "$WORK/empty.err")"
fi

# ---------------------------------------------------------------------------
note ""
note "adr-index.sh: $PASS passed, $FAIL failed"
if [[ "$FAIL" -gt 0 ]]; then
  exit 1
fi
