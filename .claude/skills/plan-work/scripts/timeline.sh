#!/usr/bin/env bash
# timeline.sh — propose milestone due dates from issue-level estimates and dependencies. READ-ONLY (proposes; the agent applies).
# Usage: timeline.sh [--repo owner/name] [--milestones "A,B"] [--milestone <title>]... [--parallelism 1.3] [--history-dir <dir>] [--defaults S=2,M=6,L=16] [--out <dir>]
# Reads each open milestone's open issues: the `## Estimate` section (Size, est. cycle hours if given) and `blocked_by` links (REST).
# An `est. cycle` value in an issue body that isn't a plain ASCII decimal number (e.g. "1.2.3",
# or a non-ASCII digit such as "٢") is treated like a missing estimate — it falls back to the
# issue's size default (or the M default if it has none) — and is reported on stderr naming the
# issue number and the offending text, wording the fallback truthfully for each case; it never
# aborts the run (see #1271).
# --milestones takes an exact, comma-split list of milestone titles (no substring matching); each name is
# trimmed of leading/trailing whitespace, so a title with leading/trailing spaces can never be named this way.
# --milestone (singular, repeatable) takes one exact, untrimmed title per flag — use it for a title
# containing a comma, or one with meaningful leading/trailing whitespace. --milestones and --milestone
# combine into a single selection. If any selection is requested, a requested name that matches no
# milestone is reported on stderr, and matching zero milestones in total is an error (exit 3).
# --history-dir points at the directory history.sh wrote parallelism.txt into; without it (and without
# --parallelism) the default --out convention is assumed, and falling back to 1.5 is reported on stderr.
# A missing, empty, non-numeric, zero, or negative parallelism.txt falls back to 1.5 the same way.
# --defaults may name any subset of S/M/L; a size it omits keeps its built-in default (S=2, M=6, L=16),
# and a size named twice resolves last-wins. Empty values, empty parts, trailing commas and non-positive
# numbers are rejected (exit 2).
# Exit codes: 2 = argument error (unknown flag, a value-taking flag with no following value, an empty
# --milestones/--milestone value, or a --defaults value that is empty or has a part that isn't
# S=<n>/M=<n>/L=<n> with n an ASCII decimal number greater than zero, so a non-ASCII digit such as
# "٢" is an argument error too); 3 = a
# --milestones/--milestone selection was requested but matched zero open milestones; 4 = --parallelism
# was given a value that is not a positive decimal number matching ^[[:digit:]]+([.][[:digit:]]+)?$ (ASCII
# digits only, so non-ASCII digits such as "٢" are rejected too; "0", "-1", "abc", ".5", "1e2" and
# leading/trailing whitespace are all rejected); 5 = a blocked_by cycle was
# detected while computing a milestone's critical path (jq's own error exit surfaces here). A cycle is
# the only cause reachable from this script's own flag values -- no --defaults value can produce it any
# more, and a malformed in-body est. cycle value can't either (it falls back instead, see #1271) --
# but exit 5 is jq's generic error exit, so an unexpected jq failure would also surface as 5.
set -euo pipefail
REPO=""; ONLY=""; PAR=""; HISTORY_DIR=""; BUILTIN_DEF="S=2,M=6,L=16"; DEF="$BUILTIN_DEF"; OUT="${TMPDIR:-/tmp}/plan-work-timeline"; HOURS_PER_DAY=8
MILESTONE_ARGS=()
require_arg(){ [ "$#" -ge 2 ] || { echo "timeline.sh: $1 requires a value" >&2; exit 2; }; }
require_value(){ [ -n "$2" ] || { echo "timeline.sh: $1 requires a non-empty value" >&2; exit 2; }; }
is_positive_number(){ [[ "$1" =~ ^[[:digit:]]+([.][[:digit:]]+)?$ ]] && awk -v v="$1" 'BEGIN{exit !(v>0)}'; }
require_positive_number(){
  if ! is_positive_number "$2"; then
    echo "timeline.sh: $1 \"$2\" must be a positive decimal number matching ^[[:digit:]]+([.][[:digit:]]+)?\$" >&2
    exit 4
  fi
}
# Validates the whole value in one shot: at least one part, no empty/whitespace-only value and no
# empty parts (so "" and a trailing comma are both rejected rather than silently yielding an empty
# array that the per-part loop would never iterate over). A repeated size is accepted, last one wins.
require_defaults(){
  local raw="$2" part parts
  [[ "$raw" =~ ^[SML]=[[:digit:]]+([.][[:digit:]]+)?(,[SML]=[[:digit:]]+([.][[:digit:]]+)?)*$ ]] || {
    echo "timeline.sh: $1 \"$raw\" must be a comma-separated list of S=<n>,M=<n>,L=<n> (positive ASCII decimal numbers; any subset of sizes is allowed and an omitted size keeps its built-in default; an empty value, an empty part, or a trailing comma is rejected)" >&2
    exit 2
  }
  IFS=',' read -ra parts <<<"$raw"
  for part in "${parts[@]}"; do
    is_positive_number "${part#*=}" || {
      echo "timeline.sh: $1 \"$raw\" part \"$part\" must be greater than zero" >&2
      exit 2
    }
  done
}
while [ $# -gt 0 ]; do case $1 in
  --repo) require_arg "$@"; REPO=$2; shift 2;;
  --milestones) require_arg "$@"; require_value --milestones "$2"; ONLY=$2; shift 2;;
  --milestone) require_arg "$@"; require_value --milestone "$2"; MILESTONE_ARGS+=("$2"); shift 2;;
  --parallelism) require_arg "$@"; require_positive_number --parallelism "$2"; PAR=$2; shift 2;;
  --history-dir) require_arg "$@"; HISTORY_DIR=$2; shift 2;;
  --defaults) require_arg "$@"; require_defaults --defaults "$2"; DEF=$2; shift 2;;
  --out) require_arg "$@"; OUT=$2; shift 2;;
  *) echo "unknown arg $1" >&2; exit 2;;
esac; done
[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
mkdir -p "$OUT"; say(){ printf '%s\n' "$*" >&2; }
ONLY_LIST=()
if [ -n "$ONLY" ]; then
  IFS=',' read -ra split_names <<<"$ONLY"
  for name in "${split_names[@]}"; do
    ONLY_LIST+=("$(sed 's/^[[:space:]]*//;s/[[:space:]]*$//' <<<"$name")")
  done
fi
if [ "${#MILESTONE_ARGS[@]}" -gt 0 ]; then
  ONLY_LIST+=("${MILESTONE_ARGS[@]}")
fi
SELECTING=0
[ "${#ONLY_LIST[@]}" -gt 0 ] && SELECTING=1
MATCH_COUNT=()
if [ "$SELECTING" = 1 ]; then
  for i in "${!ONLY_LIST[@]}"; do MATCH_COUNT[i]=0; done
fi
if [ -n "$PAR" ]; then
  : # explicit --parallelism wins
else
  hist_dir="${HISTORY_DIR:-${OUT%/*}/plan-work-history}"
  hist_file="$hist_dir/parallelism.txt"
  PAR=$(cat "$hist_file" 2>/dev/null || true)
  if ! is_positive_number "$PAR"; then
    bad_par="$PAR"
    PAR=1.5
    if [ -s "$hist_file" ]; then
      say "parallelism: ignoring unusable parallelism \"$bad_par\" in $hist_file; falling back to default $PAR (pass --parallelism to set it explicitly)"
    else
      say "parallelism: no history at $hist_file; falling back to default $PAR (pass --history-dir to point at history.sh's --out, or --parallelism to set it explicitly)"
    fi
  fi
fi
# A --defaults value may name any subset of sizes; a size it omits falls back to the built-in table
# ($BUILTIN_DEF) so the jq stage below always gets three numbers. Without this, an omitted size fed
# jq an empty --arg and any issue carrying that size (or, for M, any unestimated issue) died with a
# raw "Expected JSON value" parse error at exit 5. A repeated size resolves last-wins (greedy .*X=).
size_default(){ local v; v=$(sed -n "s/.*$1=\([[:digit:].]*\).*/\1/p" <<<"$DEF"); [ -n "$v" ] || v=$(sed -n "s/.*$1=\([[:digit:].]*\).*/\1/p" <<<"$BUILTIN_DEF"); printf '%s' "$v"; }
defS=$(size_default S); defM=$(size_default M); defL=$(size_default L)
say "milestones (open) for $REPO …"
gh api --paginate "repos/$REPO/milestones?state=open&per_page=100" --jq '.[]|{number,title,due_on,created_at}' > "$OUT/milestones.jsonl"
: > "$OUT/issues.jsonl"
: > "$OUT/milestones_selected.jsonl"
while IFS= read -r ms; do
  mn=$(jq -r .number <<<"$ms"); mt=$(jq -r .title <<<"$ms")
  if [ "$SELECTING" = 1 ]; then
    matched=0
    for i in "${!ONLY_LIST[@]}"; do
      if [ "${ONLY_LIST[$i]}" = "$mt" ]; then matched=1; MATCH_COUNT[i]=$(( MATCH_COUNT[i] + 1 )); fi
    done
    [ "$matched" = 1 ] || continue
  fi
  printf '%s\n' "$ms" >> "$OUT/milestones_selected.jsonl"
  gh api --paginate "repos/$REPO/issues?milestone=$mn&state=all&per_page=100" --jq '.[]|select(.pull_request==null)|{number,state,created_at,closed_at,body:(.body//""),assignee:(.assignee.login//null),labels:[.labels[].name]}' | while IFS= read -r iss; do
    n=$(jq -r .number <<<"$iss")
    blocked=$(gh api "repos/$REPO/issues/$n/dependencies/blocked_by" --jq '[.[].number]' 2>/dev/null || echo '[]')
    # $hraw captures whatever token sits between "est. cycle:" and "h", loosely, so a malformed
    # value (e.g. "1.2.3", or a non-ASCII digit like the Arabic-indic "٢") is detected rather than
    # silently mis-parsed by tonumber (which aborts the whole run at exit 5 on bad input -- #1271).
    # $h re-validates $hraw against a strict single-decimal ASCII pattern ([0-9], not [[:digit:]]:
    # jq/Oniguruma's [[:digit:]] is Unicode-aware and would accept non-ASCII digits here, unlike
    # bash's [[ =~ ]] where [[:digit:]] is the ASCII-safe choice used elsewhere in this script).
    # A malformed (present but unparseable) estimate falls back exactly like a missing one, and is
    # reported on stderr below with the issue number and the offending text.
    rec=$(jq -c --argjson ms "$ms" --argjson blocked "$blocked" --arg S "$defS" --arg M "$defM" --arg L "$defL" '
      ([.body|capture("## Estimate\\s*\\n(?<e>[^#]*)")]|.[0].e // "") as $est |
      ([$est|capture("Size: *(?<s>[SML])")]|.[0].s // null) as $size |
      ([$est|capture("est\\. cycle:? *(?<hraw>\\S+) *h")]|.[0].hraw // null) as $hraw |
      (if $hraw!=null and ($hraw|test("^[0-9]+([.][0-9]+)?$")) then $hraw else null end) as $h |
      ($hraw!=null and $h==null) as $malformed |
      {milestone:$ms.number, milestone_title:$ms.title, issue:.number, state:.state, assignee:.assignee, epic:([.labels[]|select(.=="epic")]|length>0),
       size:$size, hours:(if $h!=null then ($h|tonumber) elif $size=="S" then ($S|tonumber) elif $size=="M" then ($M|tonumber) elif $size=="L" then ($L|tonumber) else ($M|tonumber) end),
       hours_source:(if $h!=null then "estimate" elif $malformed then (if $size!=null then "malformed-estimate-default-"+$size else "malformed-estimate-default-M" end) elif $size!=null then "size-default" else "no-estimate-default-M" end),
       malformed_estimate:(if $malformed then $hraw else null end), blocked_by:$blocked, created:.created_at, closed:.closed_at}' <<<"$iss")
    mal=$(jq -r '.malformed_estimate // empty' <<<"$rec")
    if [ -n "$mal" ]; then
      mal_size=$(jq -r '.size // empty' <<<"$rec")
      if [ -n "$mal_size" ]; then
        say "timeline: issue #$n has an unparseable est. cycle value \"$mal\"; falling back to its size default"
      else
        say "timeline: issue #$n has an unparseable est. cycle value \"$mal\"; falling back to the M default (no size)"
      fi
    fi
    printf '%s\n' "$rec" >> "$OUT/issues.jsonl"
  done
done < "$OUT/milestones.jsonl"
if [ "$SELECTING" = 1 ]; then
  unmatched=0
  for i in "${!ONLY_LIST[@]}"; do
    if [ "${MATCH_COUNT[$i]}" = 0 ]; then
      say "milestones: no open milestone titled \"${ONLY_LIST[$i]}\" (requested via --milestones/--milestone)"
      unmatched=1
    fi
  done
  selected_count=$(wc -l < "$OUT/milestones_selected.jsonl")
  if [ "$selected_count" -eq 0 ]; then
    say "milestones: none of the requested names matched an open milestone; nothing to do"
    exit 3
  fi
  [ "$unmatched" = 0 ] || say "milestones: continuing with $selected_count matched milestone(s)"
fi
say "$(wc -l < "$OUT/issues.jsonl") issues read"
# per-milestone: critical path (longest chain of hours through blocked_by within the milestone) + effort; started = any non-epic issue closed or assigned
# Iterates over every selected milestone (not just ones with issues) so a milestone with zero issues
# still gets a row, zeroed out, instead of silently disappearing from group_by.
jq -s --argjson par "$PAR" --argjson hpd "$HOURS_PER_DAY" --slurpfile selms "$OUT/milestones_selected.jsonl" '
  (group_by(.milestone) | map({key:(.[0].milestone|tostring), value:.}) | from_entries) as $bymilestone |
  $selms | map(
    .number as $mn | .title as $mt |
    ($bymilestone[$mn|tostring] // []) as $recs |
    ($recs|map(select(.epic|not))) as $iss |
    ($iss|map(select(.state=="open"))) as $open |
    ($open|map({key:(.issue|tostring),value:.})|from_entries) as $byn |
    # longest path over blocked_by, restricted to open issues in this milestone. Re-walks the graph on
    # every call (not memoized -- the per-milestone issue counts here are small enough that this is fine);
    # $path carries the current recursion ancestors so a blocked_by cycle raises a named error instead
    # of recursing until jq exhausts itself.
    def lp($n; $path):
      if ($path|index($n)) then error("timeline: blocked_by cycle detected at issue #\($n) in milestone \($mt)") else
      ($byn[$n|tostring]) as $i |
      if $i==null then 0 else ($i.hours + ([ $i.blocked_by[] | select($byn[tostring]!=null) | lp(.; $path+[$n]) ] | max // 0)) end
      end;
    ($open|map(lp(.issue; []))|max // 0) as $cp |
    ($open|map(.hours)|add // 0) as $effort |
    ($iss|map(select(.state=="closed" or .assignee!=null))|length>0) as $started |
    ($iss|map(select(.state=="closed" or .assignee!=null)|.created)|min) as $start_hint |
    {milestone:$mn, title:$mt, open:($open|length), closed:($iss|map(select(.state=="closed"))|length),
     critical_path_h:$cp, effort_h:$effort, projected_h:([ $cp, ($effort/$par) ]|max), projected_days:(([ $cp, ($effort/$par) ]|max)/$hpd*10|round/10),
     started:$started, started_hint:$start_hint, sources:($open|group_by(.hours_source)|map({(.[0].hours_source):length})|add // {}),
     cross_milestone_deps:($open|map(.blocked_by[]|select($byn[tostring]==null))|length)}
  ) ' "$OUT/issues.jsonl" > "$OUT/projection.json"
# serial placement proposal: started milestones pinned at their hint, others laid out after the last one, in current due_on order
jq --slurpfile ms "$OUT/milestones.jsonl" '
  . as $p | ($ms|map({key:(.number|tostring),value:.})|from_entries) as $mm |
  ($p|sort_by(($mm[(.milestone|tostring)].due_on // "9999")) ) as $ordered |
  (now) as $today |
  reduce $ordered[] as $m ({cursor:$today, rows:[]};
    . as $acc |
    (if $m.started and $m.started_hint!=null then ($m.started_hint|fromdate) else $acc.cursor end) as $s |
    ($s + ($m.projected_days*86400)) as $e0 |
    (if $m.started then ([$e0,$today]|max) else $e0 end) as $fin |
    {cursor:(if $m.started then $acc.cursor else $fin end),
     rows:($acc.rows + [$m + {start:($s|todate|.[0:10]), proposed_due:($fin|todate|.[0:10]), current_due:(($mm[($m.milestone|tostring)].due_on // "")|.[0:10])}])}) | .rows' "$OUT/projection.json" > "$OUT/placement.json"
{ echo "# Timeline proposal — $REPO ($(date -u +%F)) · parallelism $PAR · $HOURS_PER_DAY h/day"; echo; echo "| Milestone | open | critical path (h) | effort (h) | projected (d) | started | start | proposed due | current due | estimate sources |"; echo "|---|---|---|---|---|---|---|---|---|---|"; jq -r '.[]|"| \(.title) (#\(.milestone)) | \(.open) | \(.critical_path_h) | \(.effort_h) | \(.projected_days) | \(.started) | \(.start) | \(.proposed_due) | \(.current_due) | \(.sources) |"' "$OUT/placement.json"; echo; echo "Unstarted milestones are laid out serially in current due-date order; cross-milestone dependencies counted per milestone (see projection.json). Ambiguous order → ask the owner. Apply with: gh api -X PATCH repos/$REPO/milestones/<n> -f due_on=<date>T12:00:00Z"; } > "$OUT/timeline.md"
say "proposal → $OUT/timeline.md"
