#!/usr/bin/env bash
# test_timeline_classifier.sh — regression test for timeline.sh's `est. cycle`
# classifier (parses/falls back/reports "unparseable" vs "over the MAX_HOURS
# ceiling"). Binds the fixture matrix from issue #1345: a bash `[0-9]` re-test
# on the est. cycle value is a *locale collation range* under a UTF-8 locale
# and matches non-ASCII digits (e.g. U+0662 "٢"), which is the defect class
# that recurred across #1264 (review rounds 2/3), #1277 and PR #1336 round 1.
# Self-contained: runs timeline.sh against a mocked `gh` on PATH serving
# fixture JSON built in a private scratch dir under $TMPDIR (or /tmp),
# removed on exit. No network access, no repository content read.
#
# Must run under a UTF-8 locale (LANG=en_US.UTF-8) — the defect class is
# invisible under LC_ALL=C, since glibc's [0-9] collation-range behavior is
# locale-dependent. See timeline.sh's own header comment and #1271/#1328.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TIMELINE_SH="$SCRIPT_DIR/../scripts/timeline.sh"
MAX_HOURS=100000

if ! locale -a 2>/dev/null | grep -qi '^en_US\.utf8$'; then
  echo "test_timeline_classifier: en_US.UTF-8 locale not installed; the defect class this test binds is invisible under any other locale (see #1345)" >&2
  exit 1
fi
export LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8

WORK="$(mktemp -d "${TMPDIR:-/tmp}/timeline-classifier-test.XXXXXX")"
cleanup(){ rm -rf "$WORK"; }
trap cleanup EXIT

FIXTURES="$WORK/fixtures"
BIN="$WORK/bin"
OUT="$WORK/out"
mkdir -p "$FIXTURES" "$BIN" "$OUT"

REPO="test-org/test-repo"

# ---------------------------------------------------------------------------
# Fixture matrix: id|est_cycle_raw|size|class|expected_hours(valid rows only)
# class: valid | unparseable | over
# Base values mirror the 20-row matrix verified by hand during the PR #1336
# review (round 1 comment on issue #1264) plus the non-ASCII rows #1345
# requires, deduplicated to one representative size per value. #1360 makes
# the Size: axis a full cross-product (every value exercised both with and
# without a Size: label, per #1345's original prose) via the shared
# expand_rows generator below, rather than hand-duplicated rows.
# ---------------------------------------------------------------------------
NINES41="99999999999999999999999999999999999999999"
BASE_ROWS=(
  # est|class|expected_hours|size(used for the "with Size:" variant)
  "5|valid|5|M"
  "0.5|valid|0.5|S"
  "100000|valid|100000|L"
  "1.2.3|unparseable||M"
  "1e2|unparseable||M"
  ".5|unparseable||M"
  "hh|unparseable||L"
  "-5|unparseable||S"
  "+5|unparseable||M"
  "100001|over||M"
  "100000.5|over||L"
  "${NINES41}|over||S"
  "٢|unparseable||M"
  "２|unparseable||S"
  "०|unparseable||M"
  "1٢|unparseable||M"
)

# expand_rows: for each base value, emit two rows — one with its assigned
# Size: label, one with no Size: label at all — so every value is covered
# both ways instead of the axis being sampled representatively.
expand_rows(){
  local id=0 base est class expected size
  ROWS=()
  for base in "${BASE_ROWS[@]}"; do
    IFS='|' read -r est class expected size <<<"$base"
    id=$((id+1)); ROWS+=("$id|$est|$size|$class|$expected")
    id=$((id+1)); ROWS+=("$id|$est||$class|$expected")
  done
}
expand_rows

issue_number(){ echo $(( 200 + $1 )); }

# ---------------------------------------------------------------------------
# Build fixture JSON: one open milestone, one issue per row, no dependencies.
# ---------------------------------------------------------------------------
printf '[{"number":1,"title":"Test Milestone","due_on":null,"created_at":"2020-01-01T00:00:00Z"}]' \
  > "$FIXTURES/milestones.json"
printf '[]' > "$FIXTURES/blocked_by.json"

: > "$WORK/issues_raw.ndjson"
for row in "${ROWS[@]}"; do
  IFS='|' read -r id est size class expected <<<"$row"
  n=$(issue_number "$id")
  body=$'## Estimate\n'
  [ -n "$size" ] && body+="Size: $size"$'\n'
  body+="est. cycle: $est h"$'\n'
  jq -cn --argjson number "$n" --arg body "$body" \
    '{number:$number,state:"open",pull_request:null,created_at:"2020-01-01T00:00:00Z",closed_at:null,body:$body,assignee:null,labels:[]}' \
    >> "$WORK/issues_raw.ndjson"
done
jq -s '.' "$WORK/issues_raw.ndjson" > "$FIXTURES/issues.json"

# ---------------------------------------------------------------------------
# Mock gh: serves the three endpoints timeline.sh calls, applying the real
# --jq expression (via the real jq binary) against the fixture, exactly as
# `gh api --jq` would against live API output. Refuses any non-GET method,
# including the glued -XPOST and --method=POST spellings (#1358).
# ---------------------------------------------------------------------------
cat > "$BIN/gh" <<'MOCKGH'
#!/usr/bin/env bash
set -euo pipefail
: "${MOCK_GH_FIXTURES:?MOCK_GH_FIXTURES must be set}"
if [ "${1:-}" != "api" ]; then
  echo "mock gh: unsupported command: $*" >&2
  exit 1
fi
shift
endpoint=""
jq_expr=""
method="GET"
while [ $# -gt 0 ]; do
  case "$1" in
    --paginate) shift ;;
    --jq) jq_expr="$2"; shift 2 ;;
    -X|--method) method="$2"; shift 2 ;;
    -X?*) method="${1#-X}"; shift ;;
    --method=*) method="${1#--method=}"; shift ;;
    *) endpoint="$1"; shift ;;
  esac
done
case "$endpoint" in
  repos/*/issues/*/dependencies/blocked_by) raw="$MOCK_GH_FIXTURES/blocked_by.json" ;;
  repos/*/issues\?milestone=*)              raw="$MOCK_GH_FIXTURES/issues.json" ;;
  repos/*/milestones*)                      raw="$MOCK_GH_FIXTURES/milestones.json" ;;
  *) echo "mock gh: unknown endpoint: $endpoint" >&2; exit 1 ;;
esac
if [ "$method" != "GET" ]; then
  echo "mock gh: refusing non-GET method ($method) on $endpoint" >&2
  exit 1
fi
if [ -n "$jq_expr" ]; then
  # -c -r together: -c keeps multi-line object/array results on one line
  # each (so a downstream `jq -s` sees one JSON doc per line, matching how
  # gh api --jq actually emits results), and -r additionally strips quotes
  # from scalar string results, matching `gh api --jq`'s raw-output
  # behavior for e.g. `.foo//empty`.
  jq -c -r "$jq_expr" "$raw"
else
  cat "$raw"
fi
MOCKGH
chmod +x "$BIN/gh"

# ---------------------------------------------------------------------------
# Run the classifier under test against the fixture.
# ---------------------------------------------------------------------------
# run_timeline captures timeline.sh's stdout/stderr into logs under $WORK
# (deleted by the `cleanup` EXIT trap above). If timeline.sh crashes, the
# uncaught non-zero return would, under this script's own `set -e`, exit
# test_timeline_classifier.sh immediately — the EXIT trap fires and the logs
# are gone before anything is printed (see #1350). Capture the exit status
# with `set +e`/`set -e` around the call and dump both logs on a non-zero
# exit, before returning control to the (still `set -e`) caller, so the
# diagnostic is emitted before the trap-triggered deletion rather than
# racing it.
run_timeline(){
  local out_dir="$1" rc=0
  set +e
  MOCK_GH_FIXTURES="$FIXTURES" PATH="$BIN:$PATH" \
    "$TIMELINE_SH" --repo "$REPO" --milestones "Test Milestone" --out "$out_dir" \
    > "$out_dir.stdout.log" 2> "$out_dir.stderr.log"
  rc=$?
  set -e
  if [ "$rc" -ne 0 ]; then
    echo "run_timeline: $TIMELINE_SH exited $rc" >&2
    echo "--- stdout ($out_dir.stdout.log) ---" >&2
    cat "$out_dir.stdout.log" >&2 || true
    echo "--- stderr ($out_dir.stderr.log) ---" >&2
    cat "$out_dir.stderr.log" >&2 || true
    return "$rc"
  fi
}

mkdir -p "$OUT/run"
run_timeline "$OUT/run"
STDERR_LOG="$OUT/run.stderr.log"
ISSUES_JSONL="$OUT/run/issues.jsonl"

fail=0
report(){ echo "FAIL: $*" >&2; fail=1; }

for row in "${ROWS[@]}"; do
  IFS='|' read -r id est size class expected <<<"$row"
  n=$(issue_number "$id")
  rec=$(jq -c --argjson n "$n" 'select(.issue==$n)' "$ISSUES_JSONL")
  if [ -z "$rec" ]; then
    report "row $id (issue #$n, est=\"$est\"): no record emitted in issues.jsonl"
    continue
  fi

  case "$class" in
    valid)
      got_source=$(jq -r '.hours_source' <<<"$rec")
      got_hours=$(jq -r '.hours' <<<"$rec")
      [ "$got_source" = "estimate" ] || report "row $id (issue #$n, est=\"$est\"): expected hours_source=estimate, got \"$got_source\""
      awk -v got="$got_hours" -v want="$expected" 'BEGIN{exit !(got==want)}' \
        || report "row $id (issue #$n, est=\"$est\"): expected hours=$expected, got $got_hours"
      if grep -q "issue #$n has" "$STDERR_LOG"; then
        report "row $id (issue #$n, est=\"$est\"): expected no fallback message on stderr, but found one"
      fi
      ;;
    unparseable|over)
      if [ -n "$size" ]; then
        want_source="malformed-estimate-default-$size"
        want_hours=$(case "$size" in S) echo 2;; M) echo 6;; L) echo 16;; esac)
        tail_phrase="falling back to its size default"
      else
        want_source="malformed-estimate-default-M"
        want_hours=6
        tail_phrase="falling back to the M default (no size)"
      fi
      if [ "$class" = "unparseable" ]; then
        cause_phrase="an unparseable est. cycle value \"$est\""
      else
        cause_phrase="an est. cycle value \"$est\" over the $MAX_HOURS-hour ceiling"
      fi
      want_line="timeline: issue #$n has $cause_phrase; $tail_phrase"

      got_source=$(jq -r '.hours_source' <<<"$rec")
      got_hours=$(jq -r '.hours' <<<"$rec")
      [ "$got_source" = "$want_source" ] || report "row $id (issue #$n, est=\"$est\"): expected hours_source=$want_source, got \"$got_source\""
      awk -v got="$got_hours" -v want="$want_hours" 'BEGIN{exit !(got==want)}' \
        || report "row $id (issue #$n, est=\"$est\"): expected hours=$want_hours, got $got_hours"
      if ! grep -qF "$want_line" "$STDERR_LOG"; then
        report "row $id (issue #$n, est=\"$est\"): expected stderr line not found: $want_line"
      fi
      ;;
    *)
      report "row $id: unknown class \"$class\""
      ;;
  esac
done

if [ "$fail" -ne 0 ]; then
  echo "--- stderr from timeline.sh run ---" >&2
  cat "$STDERR_LOG" >&2
  echo "test_timeline_classifier: FAILED" >&2
  exit 1
fi

echo "test_timeline_classifier: all ${#ROWS[@]} rows passed (repo=$REPO, LANG=$LANG)"
