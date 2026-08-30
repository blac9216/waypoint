#!/usr/bin/env bash
# test_history.sh — fixture-driven regression test for history.sh (issue #1348).
# Extends the mock-`gh` harness from test_timeline_classifier.sh (#1345/#1347)
# to history.sh's own set of endpoints: closed-PR listing, PR detail,
# PR-conversation comments, issue detail (twice, different --jq), issue
# timeline, and issue comments (metrics). Self-contained: mocked `gh` on
# PATH serving fixture JSON built in a private scratch dir under $TMPDIR (or
# /tmp), removed on exit. No network access, no repository content read.
#
# Covers:
#  - documented output: one merged PR closing one issue produces a full
#    issues.jsonl record (type/areas/severity/priority/milestone/parent,
#    started/start_source, cycle_hours/cycle_days, rounds/findings,
#    additions/deletions/net_loc, size_est/estimate_text, metrics, deferred,
#    era) and a calibration.json/calibration.md aggregate.
#  - failure mode: a merged PR with no closing keyword in its body is
#    skipped entirely (no record, no further gh calls for it).
#  - failure mode: an unrecognized CLI flag exits 2 without any gh call.
#  - the mock's write-verb refusal (gh api -X POST) — same class of gap
#    flagged on #1348's PR #1347 review, fixed for both scripts here.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HISTORY_SH="$SCRIPT_DIR/../scripts/history.sh"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/history-test.XXXXXX")"
cleanup(){ rm -rf "$WORK"; }
trap cleanup EXIT

FIXTURES="$WORK/fixtures"
BIN="$WORK/bin"
OUT="$WORK/out"
mkdir -p "$FIXTURES/pulls" "$FIXTURES/issues" "$FIXTURES/timeline" "$FIXTURES/comments" "$BIN" "$OUT"

REPO="test-org/test-repo"
SINCE="2026-01-01"
ADOPT="2026-01-01"

fail=0
report(){ echo "FAIL: $*" >&2; fail=1; }

# ---------------------------------------------------------------------------
# Fixtures: two merged PRs.
#   #501 closes #301 (full-featured issue: estimate, labels, milestone,
#        parent, cross-reference, review-round finding, metrics comment).
#   #502 has no closing keyword — must be skipped entirely (no further gh
#        calls for its number; verified by there being no fixtures for it).
# ---------------------------------------------------------------------------
cat > "$FIXTURES/pulls_list.json" <<'JSON'
[
  {
    "number": 501,
    "body": "Fixes the thing.\n\nCloses #301",
    "created_at": "2026-01-01T00:00:00Z",
    "merged_at": "2026-01-10T12:00:00Z",
    "head": {"ref": "301-fix"},
    "merge_commit_sha": "aaaaaaa"
  },
  {
    "number": 502,
    "body": "Unrelated cleanup, references no issue.",
    "created_at": "2026-01-02T00:00:00Z",
    "merged_at": "2026-01-05T08:00:00Z",
    "head": {"ref": "cleanup"},
    "merge_commit_sha": "bbbbbbb"
  }
]
JSON

cat > "$FIXTURES/pulls/501.json" <<'JSON'
{"number": 501, "additions": 40, "deletions": 10, "changed_files": 3}
JSON

# PR #501 conversation comments: one "Changes Requested" round with two
# findings (1 blocker, 1 major) -> rounds=1, findings=[2].
cat > "$FIXTURES/comments/501.json" <<'JSON'
[
  {
    "body": "## PR Review — Changes Requested\n\n| # | Severity | Note |\n|---|---|---|\n| 1 | blocker | fix X |\n| 2 | major | fix Y |\n",
    "created_at": "2026-01-09T00:00:00Z"
  }
]
JSON

cat > "$FIXTURES/issues/301.json" <<'JSON'
{
  "number": 301,
  "title": "Fix the thing",
  "state": "closed",
  "created_at": "2026-01-01T00:00:00Z",
  "closed_at": "2026-01-10T12:30:00Z",
  "body": "## Estimate\nSize: M\nest. cycle: 6 h\n",
  "labels": [{"name": "area:tests"}, {"name": "bug"}, {"name": "severity:major"}, {"name": "priority:p2"}],
  "milestone": {"title": "M1"},
  "parent_issue_url": "https://api.github.com/repos/test-org/test-repo/issues/200"
}
JSON

# Timeline: assigned before merge (-> start_source=assigned) and one
# cross-reference to #205 (-> deferred=[205]).
cat > "$FIXTURES/timeline/301.json" <<'JSON'
[
  {"event": "assigned", "created_at": "2026-01-02T00:00:00Z"},
  {"event": "cross-referenced", "created_at": "2026-01-03T00:00:00Z", "source": {"issue": {"number": 205}}}
]
JSON

# Issue #301's own comments: carries the metrics HTML comment.
cat > "$FIXTURES/comments/301.json" <<'JSON'
[
  {"body": "some discussion"},
  {"body": "<!-- metrics {\"attempts\":2} -->"}
]
JSON

# ---------------------------------------------------------------------------
# Mock gh: routes by endpoint shape and trailing numeric id, applying the
# real --jq expression (via the real jq binary) against the fixture, exactly
# as `gh api --jq` would against live API output. Refuses any non-GET method
# (gh api -X POST/PATCH/..., including the glued -XPOST and --method=POST
# spellings — #1358) rather than silently serving a read fixture — the gap
# flagged on PR #1347's review of #1348.
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
  repos/*/pulls\?state=closed*)
    raw="$MOCK_GH_FIXTURES/pulls_list.json" ;;
  repos/*/pulls/*)
    num="${endpoint##*/}"; raw="$MOCK_GH_FIXTURES/pulls/$num.json" ;;
  repos/*/issues/*/comments\?per_page=100)
    num=$(echo "$endpoint" | sed -E 's#.*/issues/([0-9]+)/comments.*#\1#')
    raw="$MOCK_GH_FIXTURES/comments/$num.json" ;;
  repos/*/issues/*/timeline\?per_page=100)
    num=$(echo "$endpoint" | sed -E 's#.*/issues/([0-9]+)/timeline.*#\1#')
    raw="$MOCK_GH_FIXTURES/timeline/$num.json" ;;
  repos/*/issues/*)
    num="${endpoint##*/}"; raw="$MOCK_GH_FIXTURES/issues/$num.json" ;;
  *) echo "mock gh: unknown endpoint: $endpoint" >&2; exit 1 ;;
esac
if [ "$method" != "GET" ]; then
  echo "mock gh: refusing non-GET method ($method) on $endpoint" >&2
  exit 1
fi
if [ ! -f "$raw" ]; then
  echo "mock gh: no fixture for endpoint: $endpoint" >&2
  exit 1
fi
if [ -n "$jq_expr" ]; then
  # -c -r together: -c keeps multi-line object/array results on one line
  # each, matching how gh api --jq actually emits results; -r strips quotes
  # from scalar string results — history.sh relies on this for
  # `.parent_issue_url//empty` piped into `grep -oE '[0-9]+$'`.
  jq -c -r "$jq_expr" "$raw"
else
  cat "$raw"
fi
MOCKGH
chmod +x "$BIN/gh"

# ---------------------------------------------------------------------------
# Failure mode: unknown flag exits 2 before touching gh at all (no PATH
# override needed — if it reached gh, the run dir below would exist).
# ---------------------------------------------------------------------------
set +e
"$HISTORY_SH" --bogus-flag >"$OUT/badflag.stdout.log" 2>"$OUT/badflag.stderr.log"
rc=$?
set -e
[ "$rc" -eq 2 ] || report "unknown-flag: expected exit 2, got $rc"
grep -qF "unknown arg --bogus-flag" "$OUT/badflag.stderr.log" \
  || report "unknown-flag: expected 'unknown arg' message on stderr, got: $(cat "$OUT/badflag.stderr.log")"

# ---------------------------------------------------------------------------
# Run history.sh under test against the fixtures. Crash-path diagnostics:
# dump captured stdout/stderr before returning non-zero, so a crash here
# doesn't race the cleanup trap (`$OUT` lives under `$WORK`, which the EXIT
# trap `rm -rf`s) and lose the log, matching the pattern in
# test_timeline_classifier.sh (#1350).
# ---------------------------------------------------------------------------
run_history(){
  local out_dir="$1" rc=0
  set +e
  MOCK_GH_FIXTURES="$FIXTURES" PATH="$BIN:$PATH" \
    "$HISTORY_SH" --repo "$REPO" --since "$SINCE" --adoption-date "$ADOPT" --out "$out_dir" \
    > "$out_dir.stdout.log" 2> "$out_dir.stderr.log"
  rc=$?
  set -e
  if [ "$rc" -ne 0 ]; then
    echo "run_history: $HISTORY_SH exited $rc" >&2
    echo "--- stdout ($out_dir.stdout.log) ---" >&2
    cat "$out_dir.stdout.log" >&2 || true
    echo "--- stderr ($out_dir.stderr.log) ---" >&2
    cat "$out_dir.stderr.log" >&2 || true
    return "$rc"
  fi
}

mkdir -p "$OUT/run"
run_history "$OUT/run"
ISSUES_JSONL="$OUT/run/issues.jsonl"
CALIBRATION_JSON="$OUT/run/calibration.json"
CALIBRATION_MD="$OUT/run/calibration.md"

for f in "$ISSUES_JSONL" "$CALIBRATION_JSON" "$CALIBRATION_MD"; do
  [ -s "$f" ] || report "missing or empty output: $f"
done

# ---------------------------------------------------------------------------
# Assertions: PR #502 (no closing keyword) produced no record.
# ---------------------------------------------------------------------------
n502=$(jq -s '[.[]|select(.pr==502)]|length' <"$ISSUES_JSONL" 2>/dev/null || echo error)
[ "$n502" = "0" ] || report "PR #502 (no closing keyword): expected 0 records, got $n502"

# ---------------------------------------------------------------------------
# Assertions: the #301/#501 record.
# ---------------------------------------------------------------------------
rec=$(jq -c 'select(.issue==301)' "$ISSUES_JSONL")
[ -n "$rec" ] || report "no record emitted for issue #301"

if [ -n "$rec" ]; then
  check_eq(){ # field jq_path expected
    local field="$1" path="$2" want="$3" got
    got=$(jq -r "$path" <<<"$rec")
    [ "$got" = "$want" ] || report "issue #301: expected $field=$want, got $got"
  }
  check_eq pr           .pr            501
  check_eq type          .type          bug
  check_eq areas         '.areas|join(",")' "area:tests"
  check_eq severity      .severity      "severity:major"
  check_eq priority      .priority      "priority:p2"
  check_eq milestone     .milestone     M1
  check_eq parent        .parent        200
  check_eq start_source  .start_source  assigned
  check_eq started       .started       "2026-01-02T00:00:00Z"
  check_eq cycle_hours   .cycle_hours   204
  check_eq cycle_days    .cycle_days    8.5
  check_eq rounds        .rounds        1
  check_eq findings      '.findings|join(",")' 2
  check_eq additions     .additions     40
  check_eq deletions     .deletions     10
  check_eq net_loc       .net_loc       30
  # history.sh's Size: extraction now anchors to the trailing letter (#1351
  # fixed the self-match on the "S" in "Size:"), so a Size: M estimate
  # yields the single letter "M".
  check_eq size_est      .size_est      "M"
  check_eq metrics       '.metrics.attempts' 2
  check_eq deferred      '.deferred|join(",")' 205
  check_eq era           .era           post-adoption

  got_est=$(jq -r '.estimate_text' <<<"$rec")
  case "$got_est" in
    *"Size: M"*"est. cycle: 6 h"*) ;;
    *) report "issue #301: expected estimate_text to contain 'Size: M' and 'est. cycle: 6 h', got: $got_est" ;;
  esac
fi

# ---------------------------------------------------------------------------
# Assertions: calibration.json completeness and by_area_size, calibration.md.
# ---------------------------------------------------------------------------
completeness=$(jq -c '.completeness' "$CALIBRATION_JSON")
want_completeness='{"with_assigned_start":1,"with_estimate":1,"with_metrics":1,"with_area":1,"total":1}'
[ "$completeness" = "$want_completeness" ] || report "calibration.json completeness: expected $want_completeness, got $completeness"

area_row=$(jq -c --arg size "M" '.by_area_size[]|select(.area=="area:tests" and .size==$size)' "$CALIBRATION_JSON")
[ -n "$area_row" ] || report "calibration.json: expected a by_area_size row for area:tests, got none"

grep -qF "area:tests" "$CALIBRATION_MD" || report "calibration.md: expected the area:tests row in the table"

if [ "$fail" -ne 0 ]; then
  echo "--- stderr from history.sh run ---" >&2
  cat "$OUT/run.stderr.log" >&2 || true
  echo "test_history: FAILED" >&2
  exit 1
fi

echo "test_history: all assertions passed (repo=$REPO)"
