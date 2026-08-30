#!/usr/bin/env bash
# project.sh — bring an EXISTING Project up to manifests/project.json: custom fields + options, views (layout, filter, columns).
# Group-by / sort / workflows cannot be set via the API; they are printed as a UI checklist at the end.
# Usage: project.sh --owner <login> --project <number> [--audit]   (DRY_RUN=1 supported)
source "$(dirname "$0")/_lib.sh"
OWNER=""; NUM=""; AUDIT=0
while [ $# -gt 0 ]; do case $1 in --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --audit) AUDIT=1; shift;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$OWNER" ] && [ -n "$NUM" ] || { say "usage: project.sh --owner <login> --project <number> [--audit]"; exit 2; }
M="$MANIFESTS/project.json"
PID=$(gh api graphql -f query='query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id}}}' -F o="$OWNER" -F n="$NUM" --jq '.data.user.projectV2.id' 2>/dev/null || true)
[ -n "$PID" ] || PID=$(gh api graphql -f query='query($o:String!,$n:Int!){organization(login:$o){projectV2(number:$n){id}}}' -F o="$OWNER" -F n="$NUM" --jq '.data.organization.projectV2.id')
live=$(gh api graphql -F id="$PID" -f query='query($id:ID!){node(id:$id){... on ProjectV2{fields(first:40){nodes{... on ProjectV2FieldCommon{id name dataType} ... on ProjectV2SingleSelectField{options{id name}}}} views(first:30){nodes{id name layout filter fields(first:30){nodes{... on ProjectV2FieldCommon{name}}}}} workflows(first:30){nodes{name enabled}}}}}' --jq .data.node)
DRIFT=$(mktemp); trap 'rm -f "$DRIFT"' EXIT; : > "$DRIFT"
drift(){ echo 1 >> "$DRIFT"; }
# --- fields
jq -c '.custom_fields[]' "$M" | while IFS= read -r f; do
  name=$(jq -r .name <<<"$f"); type=$(jq -r .dataType <<<"$f")
  cur=$(jq -c --arg n "$name" '.fields.nodes[]|select(.name==$n)' <<<"$live")
  if [ -z "$cur" ]; then drift; say "create field $name ($type)"
    if [ $AUDIT = 0 ]; then
      if [ "$type" = SINGLE_SELECT ]; then opts=$(jq -c '[.options[]|{name,color:(.color//"GRAY"),description:(.description//"")}]' <<<"$f")
        gql 'mutation($p:ID!,$n:String!,$o:[ProjectV2SingleSelectFieldOptionInput!]!){createProjectV2Field(input:{projectId:$p,dataType:SINGLE_SELECT,name:$n,singleSelectOptions:$o}){projectV2Field{... on ProjectV2SingleSelectField{id}}}}' "$(jq -n --arg p "$PID" --arg n "$name" --argjson o "$opts" '{p:$p,n:$n,o:$o}')" >/dev/null
      else gql 'mutation($p:ID!,$n:String!,$t:ProjectV2CustomFieldType!){createProjectV2Field(input:{projectId:$p,dataType:$t,name:$n}){projectV2Field{... on ProjectV2Field{id}}}}' "$(jq -n --arg p "$PID" --arg n "$name" --arg t "$type" '{p:$p,n:$n,t:$t}')" >/dev/null; fi
    fi
  elif [ "$type" = SINGLE_SELECT ]; then
    wantopts=$(jq -r '[.options[].name]|join("|")' <<<"$f"); haveopts=$(jq -r '[.options[].name]|join("|")' <<<"$cur")
    if [ "$wantopts" != "$haveopts" ]; then drift; say "correct options on $name: [$haveopts] -> [$wantopts]"
      [ $AUDIT = 0 ] && gql 'mutation($f:ID!,$o:[ProjectV2SingleSelectFieldOptionInput!]!){updateProjectV2Field(input:{fieldId:$f,singleSelectOptions:$o}){projectV2Field{... on ProjectV2SingleSelectField{id}}}}' "$(jq -n --arg f "$(jq -r .id <<<"$cur")" --argjson o "$(jq -c '[.options[]|{name,color:(.color//"GRAY"),description:(.description//"")}]' <<<"$f")" '{f:$f,o:$o}')" >/dev/null
    fi
  fi
done
# --- views (re-read fields for ids after creation)
live=$(gh api graphql -F id="$PID" -f query='query($id:ID!){node(id:$id){... on ProjectV2{fields(first:40){nodes{... on ProjectV2FieldCommon{id name}}} views(first:30){nodes{id name layout filter fields(first:30){nodes{... on ProjectV2FieldCommon{name}}}}}}}}' --jq .data.node)
fid(){ local id; id=$(jq -r --arg n "$1" '.fields.nodes[]|select(.name==$n)|.id' <<<"$live"); [ -n "$id" ] || { say "error: view column '$1' does not match any project field"; exit 1; }; printf '%s\n' "$id"; }
jq -c '.views[]' "$M" | while IFS= read -r v; do
  name=$(jq -r .name <<<"$v"); layout=$(jq -r .layout <<<"$v"); filter=$(jq -r .filter <<<"$v")
  ids=$(jq -r '.columns[]' <<<"$v" | while read -r c; do fid "$c"; done | jq -R . | jq -sc .)
  cur=$(jq -c --arg n "$name" '.views.nodes[]|select(.name==$n)' <<<"$live")
  if [ -z "$cur" ]; then drift; say "create view $name ($layout) [$filter]"
    if [ $AUDIT = 0 ]; then
      if [ "$layout" = ROADMAP_LAYOUT ]; then vid=$(gql 'mutation($p:ID!,$n:String!,$l:ProjectV2ViewLayout!){createProjectV2View(input:{projectId:$p,name:$n,layout:$l}){projectV2View{id}}}' "$(jq -n --arg p "$PID" --arg n "$name" --arg l "$layout" '{p:$p,n:$n,l:$l}')" | jq -r .data.createProjectV2View.projectV2View.id)
      else vid=$(gql 'mutation($p:ID!,$n:String!,$l:ProjectV2ViewLayout!,$ids:[ID!]){createProjectV2View(input:{projectId:$p,name:$n,layout:$l,configuration:{visibleFieldIds:$ids}}){projectV2View{id}}}' "$(jq -n --arg p "$PID" --arg n "$name" --arg l "$layout" --argjson ids "$ids" '{p:$p,n:$n,l:$l,ids:$ids}')" | jq -r .data.createProjectV2View.projectV2View.id); fi
      [ -n "$filter" ] && [ "$DRY" != 1 ] && gql 'mutation($v:ID!,$f:String!){updateProjectV2View(input:{viewId:$v,filter:$f}){projectV2View{id}}}' "$(jq -n --arg v "$vid" --arg f "$filter" '{v:$v,f:$f}')" >/dev/null; fi
  else
    curf=$(jq -r '.filter//""' <<<"$cur"); curcols=$(jq -r '[.fields.nodes[].name]|join("|")' <<<"$cur"); wantcols=$(jq -r '.columns|join("|")' <<<"$v")
    [ "$layout" = ROADMAP_LAYOUT ] && curcols=$wantcols   # roadmap columns are not settable; ignore
    if [ "$curf" != "$filter" ] || [ "$curcols" != "$wantcols" ] || [ "$(jq -r .layout <<<"$cur")" != "$layout" ]; then drift; say "correct view $name"
      [ $AUDIT = 0 ] && [ "$layout" = ROADMAP_LAYOUT ] && gql 'mutation($v:ID!,$f:String!){updateProjectV2View(input:{viewId:$v,filter:$f}){projectV2View{id}}}' "$(jq -n --arg v "$(jq -r .id <<<"$cur")" --arg f "$filter" '{v:$v,f:$f}')" >/dev/null
      [ $AUDIT = 0 ] && [ "$layout" != ROADMAP_LAYOUT ] && gql 'mutation($v:ID!,$f:String!,$l:ProjectV2ViewLayout!,$ids:[ID!]){updateProjectV2View(input:{viewId:$v,filter:$f,layout:$l,configuration:{visibleFieldIds:$ids}}){projectV2View{id}}}' "$(jq -n --arg v "$(jq -r .id <<<"$cur")" --arg f "$filter" --arg l "$layout" --argjson ids "$ids" '{v:$v,f:$f,l:$l,ids:$ids}')" >/dev/null; fi
  fi
done
# --- extra views (report only)
jq -r '.views.nodes[].name' <<<"$live" | while IFS= read -r n; do jq -e --arg n "$n" '.views[]|select(.name==$n)' "$M" >/dev/null || say "note: view '$n' is not in the manifest (delete it in the UI, or capture to adopt it)"; done
# --- workflows (read-only)
wf=$(gh api graphql -F id="$PID" -f query='query($id:ID!){node(id:$id){... on ProjectV2{workflows(first:30){nodes{name enabled}}}}}' --jq .data.node)
jq -r '.workflows[]' "$M" | while IFS= read -r n; do en=$(jq -r --arg n "$n" '.workflows.nodes[]|select(.name==$n)|.enabled' <<<"$wf"); [ "$en" = true ] || { drift; say "workflow NOT enabled: $n"; }; done
say ""; say "UI checklist (cannot be set via API) — Project ▸ … ▸ Workflows / each view's ▾ menu:"
say "  workflows: $(jq -r '.workflows|join(" · ")' "$M"); 'Item added' → Triage; 'Item closed' → Done (reopened issues are re-triaged by maintenance)"
jq -r '.views[]|select((.group_by|length)>0 or (.board_columns|length)>0 or (.sort|length)>0)|"  view \(.name): group-by=\(.group_by|join(","))\(if (.board_columns|length)>0 then " board-columns="+(.board_columns|join(",")) else "" end)\(if (.sort|length)>0 then " sort="+(.sort|join(",")) else "" end)"' "$M" >&2
[ ! -s "$DRIFT" ] && say "project: in sync" || { [ $AUDIT = 1 ] && exit 1; say "project: applied (verify the UI checklist)"; }
