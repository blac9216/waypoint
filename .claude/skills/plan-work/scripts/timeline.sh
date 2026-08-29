#!/usr/bin/env bash
# timeline.sh — propose milestone due dates from issue-level estimates and dependencies. READ-ONLY (proposes; the agent applies).
# Usage: timeline.sh [--repo owner/name] [--milestones "A,B"] [--parallelism 1.3] [--defaults S=2,M=6,L=16] [--out <dir>]
# Reads each open milestone's open issues: the `## Estimate` section (Size, est. cycle hours if given) and `blocked_by` links (REST).
set -euo pipefail
REPO=""; ONLY=""; PAR=""; DEF="S=2,M=6,L=16"; OUT="${TMPDIR:-/tmp}/plan-work-timeline"; HOURS_PER_DAY=8
while [ $# -gt 0 ]; do case $1 in --repo) REPO=$2; shift 2;; --milestones) ONLY=$2; shift 2;; --parallelism) PAR=$2; shift 2;; --defaults) DEF=$2; shift 2;; --out) OUT=$2; shift 2;; *) echo "unknown arg $1" >&2; exit 2;; esac; done
[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
mkdir -p "$OUT"; say(){ printf '%s\n' "$*" >&2; }
[ -n "$PAR" ] || { PAR=$(cat "${OUT%/*}/plan-work-history/parallelism.txt" 2>/dev/null || echo 1.5); }
defS=$(sed -n 's/.*S=\([0-9.]*\).*/\1/p' <<<"$DEF"); defM=$(sed -n 's/.*M=\([0-9.]*\).*/\1/p' <<<"$DEF"); defL=$(sed -n 's/.*L=\([0-9.]*\).*/\1/p' <<<"$DEF")
say "milestones (open) for $REPO …"
gh api --paginate "repos/$REPO/milestones?state=open&per_page=100" --jq '.[]|{number,title,due_on,created_at}' > "$OUT/milestones.jsonl"
: > "$OUT/issues.jsonl"
while IFS= read -r ms; do
  mn=$(jq -r .number <<<"$ms"); mt=$(jq -r .title <<<"$ms")
  if [ -n "$ONLY" ] && ! grep -qF "$mt" <<<"$ONLY"; then continue; fi
  gh api --paginate "repos/$REPO/issues?milestone=$mn&state=all&per_page=100" --jq '.[]|select(.pull_request==null)|{number,state,created_at,closed_at,body:(.body//""),assignee:(.assignee.login//null),labels:[.labels[].name]}' | while IFS= read -r iss; do
    n=$(jq -r .number <<<"$iss")
    blocked=$(gh api "repos/$REPO/issues/$n/dependencies/blocked_by" --jq '[.[].number]' 2>/dev/null || echo '[]')
    jq -c --argjson ms "$ms" --argjson blocked "$blocked" --arg S "$defS" --arg M "$defM" --arg L "$defL" '
      ([.body|capture("## Estimate\\s*\\n(?<e>[^#]*)")]|.[0].e // "") as $est |
      ([$est|capture("Size: *(?<s>[SML])")]|.[0].s // null) as $size |
      ([$est|capture("est\\. cycle:? *(?<h>[0-9.]+) *h")]|.[0].h // null) as $h |
      {milestone:$ms.number, milestone_title:$ms.title, issue:.number, state:.state, assignee:.assignee, epic:([.labels[]|select(.=="epic")]|length>0),
       size:$size, hours:(if $h!=null then ($h|tonumber) elif $size=="S" then ($S|tonumber) elif $size=="M" then ($M|tonumber) elif $size=="L" then ($L|tonumber) else ($M|tonumber) end),
       hours_source:(if $h!=null then "estimate" elif $size!=null then "size-default" else "no-estimate-default-M" end), blocked_by:$blocked, created:.created_at, closed:.closed_at}' <<<"$iss" >> "$OUT/issues.jsonl"
  done
done < "$OUT/milestones.jsonl"
say "$(wc -l < "$OUT/issues.jsonl") issues read"
# per-milestone: critical path (longest chain of hours through blocked_by within the milestone) + effort; started = any non-epic issue closed or assigned
jq -s --argjson par "$PAR" --argjson hpd "$HOURS_PER_DAY" '
  group_by(.milestone) | map(
    (.[0]) as $m0 | (map(select(.epic|not))) as $iss |
    ($iss|map(select(.state=="open"))) as $open |
    ($open|map({key:(.issue|tostring),value:.})|from_entries) as $byn |
    # longest path: recursive memo over blocked_by restricted to open issues in this milestone
    def lp($n): ($byn[$n|tostring]) as $i | if $i==null then 0 else ($i.hours + ([ $i.blocked_by[] | select($byn[tostring]!=null) | lp(.) ] | max // 0)) end;
    ($open|map(lp(.issue))|max // 0) as $cp |
    ($open|map(.hours)|add // 0) as $effort |
    ($iss|map(select(.state=="closed" or .assignee!=null))|length>0) as $started |
    ($iss|map(select(.state=="closed" or .assignee!=null)|.created)|min) as $start_hint |
    {milestone:$m0.milestone, title:$m0.milestone_title, open:($open|length), closed:($iss|map(select(.state=="closed"))|length),
     critical_path_h:$cp, effort_h:$effort, projected_h:([ $cp, ($effort/$par) ]|max), projected_days:(([ $cp, ($effort/$par) ]|max)/$hpd*10|round/10),
     started:$started, started_hint:$start_hint, sources:($open|group_by(.hours_source)|map({(.[0].hours_source):length})|add // {}),
     cross_milestone_deps:($open|map(.blocked_by[]|select($byn[tostring]==null))|length)}
  ) ' "$OUT/issues.jsonl" > "$OUT/projection.json"
# serial placement proposal: started milestones pinned at their hint, others laid out after the last one, in current due_on order
jq -s --slurpfile ms "$OUT/milestones.jsonl" '
  .[0] as $p | ($ms|map({key:(.number|tostring),value:.})|from_entries) as $mm |
  ($p|sort_by(($mm[(.milestone|tostring)].due_on // "9999")) ) as $ordered |
  (now) as $today |
  reduce $ordered[] as $m ({cursor:$today, rows:[]};
    . as $acc |
    (if $m.started and $m.started_hint!=null then ($m.started_hint|fromdate) else $acc.cursor end) as $s |
    ($s + ($m.projected_days*86400)) as $e0 |
    (if $m.started then ([$e0,$today]|max) else $e0 end) as $fin |
    {cursor:(if $m.started then $acc.cursor else $fin end),
     rows:($acc.rows + [$m + {start:($s|todate|.[0:10]), proposed_due:($fin|todate|.[0:10]), current_due:(($mm[($m.milestone|tostring)].due_on // "")|.[0:10])}])}) | .rows' <(jq -s . "$OUT/projection.json") > "$OUT/placement.json"
{ echo "# Timeline proposal — $REPO ($(date -u +%F)) · parallelism $PAR · $HOURS_PER_DAY h/day"; echo; echo "| Milestone | open | critical path (h) | effort (h) | projected (d) | started | start | proposed due | current due | estimate sources |"; echo "|---|---|---|---|---|---|---|---|---|---|"; jq -r '.[]|"| \(.title) (#\(.milestone)) | \(.open) | \(.critical_path_h) | \(.effort_h) | \(.projected_days) | \(.started) | \(.start) | \(.proposed_due) | \(.current_due) | \(.sources) |"' "$OUT/placement.json"; echo; echo "Unstarted milestones are laid out serially in current due-date order; cross-milestone dependencies counted per milestone (see projection.json). Ambiguous order → ask the owner. Apply with: gh api -X PATCH repos/$REPO/milestones/<n> -f due_on=<date>T12:00:00Z"; } > "$OUT/timeline.md"
say "proposal → $OUT/timeline.md"
