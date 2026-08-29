#!/usr/bin/env bash
# history.sh — build the estimation calibration table from repository history. READ-ONLY.
# Usage: history.sh [--repo owner/name] [--since YYYY-MM-DD (default: 90 days ago; full history is ~1h and near the hourly API limit)] [--adoption-date YYYY-MM-DD] [--out <dir>]   (REST GET only)
# Output: <out>/issues.jsonl (one record per closed issue with a merged PR), <out>/calibration.json, <out>/calibration.md
set -euo pipefail
REPO=""; SINCE=$(date -u -d "90 days ago" +%F 2>/dev/null || date -u -v-90d +%F); ADOPT=""; OUT="${TMPDIR:-/tmp}/plan-work-history"
while [ $# -gt 0 ]; do case $1 in --repo) REPO=$2; shift 2;; --since) SINCE=$2; shift 2;; --adoption-date) ADOPT=$2; shift 2;; --out) OUT=$2; shift 2;; *) echo "unknown arg $1" >&2; exit 2;; esac; done
[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
mkdir -p "$OUT"; : > "$OUT/issues.jsonl"
say(){ printf '%s\n' "$*" >&2; }
say "collecting merged PRs for $REPO since $SINCE …"
# merged PRs with closing keywords → issue numbers
gh api --paginate "repos/$REPO/pulls?state=closed&per_page=100&sort=updated&direction=desc" \
 --jq '.[]|select(.merged_at!=null and .merged_at>="'"$SINCE"'")|{pr:.number,body:(.body//""),created:.created_at,merged:.merged_at,head:.head.ref,sha:.merge_commit_sha}' > "$OUT/prs.jsonl"
say "$(wc -l < "$OUT/prs.jsonl") merged PRs"
while IFS= read -r pr; do
  n=$(jq -r .pr <<<"$pr"); issues=$(jq -r '.body|[scan("(?i)(?:closes|fixes|resolves) #([0-9]+)")|.[0]]|unique|.[]' <<<"$pr")
  [ -n "$issues" ] || continue
  prf=$(gh api "repos/$REPO/pulls/$n" --jq '{additions,deletions,changed_files}')
  comments=$(gh api --paginate "repos/$REPO/issues/$n/comments?per_page=100" --jq '[.[]|{b:.body,t:.created_at}]' | jq -s 'add // []')
  rounds=$(jq '[.[]|select(.b|test("^## PR Review — (Changes Requested|Decomposition Requested)"))]|length' <<<"$comments"); [ -n "$rounds" ] || rounds=0
  findings=$(jq -c '[.[]|select(.b|test("^## PR Review — Changes Requested"))|(.b|[scan("\n\\| [0-9]+ \\| (blocker|major|minor)")]|length)]' <<<"$comments"); [ -n "$findings" ] || findings='[]'
  for i in $issues; do
    iss=$(gh api "repos/$REPO/issues/$i" --jq '{title,state,created:.created_at,closed:.closed_at,labels:[.labels[].name],milestone:(.milestone.title//null),body:(.body//"")}') || continue
    tl=$(gh api --paginate "repos/$REPO/issues/$i/timeline?per_page=100" --jq '[.[]|{e:.event,t:.created_at,src:(.source.issue.number//null)}]' | jq -s 'add // []')
    merged_at=$(jq -r .merged <<<"$pr"); assigned=$(jq -r --arg m "$merged_at" '[.[]|select(.e=="assigned" and .t<$m)|.t]|min//empty' <<<"$tl")
    xref=$(jq -c '[.[]|select(.e=="cross-referenced" and .src!=null)|.src]|unique' <<<"$tl"); [ -n "$xref" ] || xref='[]'
    icomments=$(gh api --paginate "repos/$REPO/issues/$i/comments?per_page=100" --jq '[.[]|.body]' | jq -s 'add // []')
    metrics=$(jq -r '[.[]|capture("<!-- metrics (?<m>\\{.*?\\}) -->";"s")|.m]|last//empty' <<<"$icomments" | jq -c . 2>/dev/null || true); [ -n "$metrics" ] || metrics=null
    est=$(jq -r '.body|capture("## Estimate\\s*\\n(?<e>[^#]*)")|.e' <<<"$iss" 2>/dev/null | tr '\n' ' ' | sed 's/  */ /g' || true)
    size=$(grep -oE 'Size: *[SML]' <<<"$est" | head -1 | grep -oE '[SML]' || true)
    parent=$(gh api "repos/$REPO/issues/$i" --jq '.parent_issue_url//empty' 2>/dev/null | grep -oE '[0-9]+$' || true)
    jq -nc --argjson pr "$pr" --argjson prf "$prf" --argjson iss "$iss" --arg assigned "$assigned" --argjson xref "$xref" --argjson metrics "$metrics" --arg est "$est" --arg size "$size" --arg parent "$parent" --arg adopt "$ADOPT" --argjson rounds "$rounds" --argjson findings "$findings" --argjson i "$i" '
      ($iss.labels|map(select(startswith("area:")))) as $areas |
      ($iss.labels|map(select(.=="bug" or .=="enhancement" or .=="chore" or .=="epic"))|.[0]) as $type |
      (if $assigned!="" then $assigned elif $pr.created!=null then $pr.created else $iss.created end) as $start |
      {issue:$i, title:$iss.title, pr:$pr.pr, type:$type, areas:$areas, severity:($iss.labels|map(select(startswith("severity:")))|.[0]), priority:($iss.labels|map(select(startswith("priority:")))|.[0]),
       milestone:$iss.milestone, parent:($parent|if .=="" then null else tonumber end),
       created:$iss.created, started:$start, start_source:(if $assigned!="" then "assigned" elif $pr.created!=null then "pr-open" else "issue-created" end),
       pr_opened:$pr.created, merged:$pr.merged, closed:$iss.closed,
       cycle_hours:(((($pr.merged|fromdate)-($start|fromdate))/3600*10|round)/10),
       cycle_days:(((($pr.merged|fromdate)-($start|fromdate))/86400*100|round)/100),
       rounds:$rounds, findings:$findings, additions:$prf.additions, deletions:$prf.deletions, files:$prf.changed_files,
       net_loc:($prf.additions-$prf.deletions|if .<0 then -. else . end),
       size_est:(if $size!="" then $size else null end), estimate_text:(if $est!="" then $est else null end), metrics:$metrics,
       deferred:$xref, era:(if $adopt=="" then null elif $iss.created>=$adopt then "post-adoption" else "pre-adoption" end)}' >> "$OUT/issues.jsonl"
  done
done < "$OUT/prs.jsonl"
say "$(wc -l < "$OUT/issues.jsonl") issue records → $OUT/issues.jsonl"
# calibration: area × size (size = estimate if present else actual net LOC bucket)
jq -s '
  def bucket: if .size_est!=null then .size_est elif .net_loc<=100 then "S" elif .net_loc<=400 then "M" else "L" end;
  def pct(p): sort | if length==0 then null else .[((length-1)*p)|floor] end;
  def stats: {n:length, cycle_h_p50:(map(.cycle_hours)|pct(0.5)), cycle_h_p80:(map(.cycle_hours)|pct(0.8)), rounds_p50:(map(.rounds)|pct(0.5)), first_pass_rate:((map(select(.rounds==0))|length)/(length|if .==0 then 1 else . end)*100|round), loc_p50:(map(.net_loc)|pct(0.5))};
  (map(. + {bucket:bucket})) as $all |
  { repo_median:($all|stats),
    by_size:($all|group_by(.bucket)|map({size:.[0].bucket} + stats)),
    by_area_size:($all|map(. as $r|($r.areas|if length==0 then ["(none)"] else . end)[]|{area:., size:$r.bucket, r:$r})|group_by([.area,.size])|map({area:.[0].area,size:.[0].size} + (map(.r)|stats))),
    estimate_vs_actual:($all|map(select(.size_est!=null))|map({issue,size_est,net_loc,actual_bucket:(if .net_loc<=100 then "S" elif .net_loc<=400 then "M" else "L" end),cycle_hours,rounds})),
    completeness:{with_assigned_start:($all|map(select(.start_source=="assigned"))|length), with_estimate:($all|map(select(.size_est!=null))|length), with_metrics:($all|map(select(.metrics!=null))|length), with_area:($all|map(select(.areas|length>0))|length), total:($all|length)},
    parallelism_note:"observed parallelism requires overlapping started..merged windows; see calibration.md" }' "$OUT/issues.jsonl" > "$OUT/calibration.json"
# observed parallelism: average number of concurrently open (started..merged) issues over the active period
jq -s '[.[]|{s:(.started|fromdate),e:(.merged|fromdate)}]|sort_by(.s)|. as $w|( [$w[].s,$w[].e]|min ) as $t0|( [$w[].e]|max ) as $t1| if $t1<=$t0 then 1 else ([$w[]|(.e-.s)]|add)/($t1-$t0) end' "$OUT/issues.jsonl" > "$OUT/parallelism.txt"
{ echo "# Calibration — $REPO ($(date -u +%F))"; echo; echo "Records: $(jq -s length "$OUT/issues.jsonl") · completeness: $(jq -c .completeness "$OUT/calibration.json") · observed parallelism: $(printf '%.2f' "$(cat "$OUT/parallelism.txt")")"; echo; echo "| Area | Size | n | cycle p50 (h) | cycle p80 (h) | rounds p50 | first-pass % | LOC p50 |"; echo "|---|---|---|---|---|---|---|---|"; jq -r '.by_area_size[]|"| \(.area) | \(.size) | \(.n) | \(.cycle_h_p50) | \(.cycle_h_p80) | \(.rounds_p50) | \(.first_pass_rate) | \(.loc_p50) |"' "$OUT/calibration.json"; echo; echo "By size: $(jq -c '.by_size' "$OUT/calibration.json")"; echo; echo "Repo median: $(jq -c '.repo_median' "$OUT/calibration.json")"; echo; echo "Estimate vs actual (n=$(jq '.estimate_vs_actual|length' "$OUT/calibration.json")): $(jq -c '.estimate_vs_actual' "$OUT/calibration.json")"; } > "$OUT/calibration.md"
say "calibration → $OUT/calibration.md"
