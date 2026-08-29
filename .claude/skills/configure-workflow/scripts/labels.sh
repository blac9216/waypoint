#!/usr/bin/env bash
# labels.sh — apply the canonical label set (+ repo area:* labels from docs/process/labels.md) and prune extras.
# Usage: labels.sh [--repo owner/name] [--areas docs/process/labels.md] [--no-prune] [--audit]
# Idempotent. --audit reports drift and exits 1 on any; DRY_RUN=1 prints the commands.
source "$(dirname "$0")/_lib.sh"
REPO=""; AREAS="docs/process/labels.md"; PRUNE=1; AUDIT=0
while [ $# -gt 0 ]; do case $1 in --repo) REPO=$2; shift 2;; --areas) AREAS=$2; shift 2;; --no-prune) PRUNE=0; shift;; --audit) AUDIT=1; shift;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$REPO" ] || REPO=$(repo_nwo)
want=$(jq -c '.labels[]' "$MANIFESTS/labels.json")
default_area_color=$(jq -r '.area_prefix.default_color' "$MANIFESTS/labels.json")
# area labels: table rows "| area:x | color | description |" in docs/process/labels.md
if [ -f "$AREAS" ]; then
  areas=$(grep -E '^\|\s*area:' "$AREAS" | sed -E 's/^\|//; s/\|$//' | awk -F'|' -v c="$default_area_color" '{gsub(/^ +| +$/,"",$1); gsub(/^ +| +$/,"",$2); gsub(/^ +| +$/,"",$3); if($2=="")$2=c; printf "{\"name\":\"%s\",\"color\":\"%s\",\"description\":\"%s\"}\n",$1,$2,$3}')
  want=$(printf '%s\n%s\n' "$want" "$areas")
else say "note: $AREAS not found — no area:* labels applied (create the table first; see the skill)"; fi
have=$(gh label list --repo "$REPO" --limit 300 --json name,color,description | jq -c '.[]')
drift=0
while IFS= read -r l; do [ -n "$l" ] || continue
  n=$(jq -r .name <<<"$l"); c=$(jq -r .color <<<"$l"); d=$(jq -r .description <<<"$l")
  cur=$(jq -c --arg n "$n" 'select(.name==$n)' <<<"$have")
  if [ -z "$cur" ]; then drift=1; say "create  $n"; [ $AUDIT = 1 ] || run gh label create "$n" --repo "$REPO" --color "$c" --description "$d"
  elif [ "$(jq -r .color <<<"$cur")" != "$c" ] || [ "$(jq -r .description <<<"$cur")" != "$d" ]; then drift=1; say "correct $n"; [ $AUDIT = 1 ] || run gh label edit "$n" --repo "$REPO" --color "$c" --description "$d"
  fi
done <<<"$want"
if [ $PRUNE = 1 ]; then
  wanted_names=$(jq -r .name <<<"$want" | sort)
  while IFS= read -r n; do [ -n "$n" ] || continue
    if ! grep -qx "$n" <<<"$wanted_names"; then
      open=$(gh issue list --repo "$REPO" --state open --label "$n" --limit 1 --json number --jq length)
      if [ "$open" = 0 ]; then drift=1; say "prune   $n (unused)"; [ $AUDIT = 1 ] || run gh label delete "$n" --repo "$REPO" --yes
      else drift=1; say "KEEP    $n — non-canonical but on open issues; retag them first"; fi
    fi
  done <<<"$(jq -r .name <<<"$have")"
fi
[ $drift = 0 ] && say "labels: in sync" || { [ $AUDIT = 1 ] && exit 1; say "labels: applied"; }
