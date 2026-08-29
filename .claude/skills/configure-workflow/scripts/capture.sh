#!/usr/bin/env bash
# capture.sh — snapshot a live GitHub Project (fields, options, views, workflows) into manifests/project.json.
# Read-only. Usage: capture.sh --owner <login> --project <number> [--out <path>]
set -euo pipefail
OWNER=""; NUM=""; OUT="$(dirname "$0")/../manifests/project.json"
while [ $# -gt 0 ]; do case $1 in --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --out) OUT=$2; shift 2;; *) echo "unknown arg $1" >&2; exit 2;; esac; done
[ -n "$OWNER" ] && [ -n "$NUM" ] || { echo "usage: capture.sh --owner <login> --project <number> [--out path]" >&2; exit 2; }
PID=$(gh api graphql -f query='query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id}}}' -F o="$OWNER" -F n="$NUM" --jq '.data.user.projectV2.id' 2>/dev/null || true)
[ -n "$PID" ] || PID=$(gh api graphql -f query='query($o:String!,$n:Int!){organization(login:$o){projectV2(number:$n){id}}}' -F o="$OWNER" -F n="$NUM" --jq '.data.organization.projectV2.id')
gh api graphql -F id="$PID" -f query='query($id:ID!){node(id:$id){... on ProjectV2{title
  fields(first:40){nodes{... on ProjectV2FieldCommon{name dataType} ... on ProjectV2SingleSelectField{options{name color description}}}}
  views(first:30){nodes{number name layout filter
    fields(first:30){nodes{... on ProjectV2FieldCommon{name}}}
    groupByFields(first:5){nodes{... on ProjectV2FieldCommon{name}}}
    verticalGroupByFields(first:5){nodes{... on ProjectV2FieldCommon{name}}}
    sortByFields(first:5){nodes{field{... on ProjectV2FieldCommon{name}} direction}}}}
  workflows(first:30){nodes{name enabled}}}}}' \
| jq '{captured_from:{owner:"'"$OWNER"'",project:'"$NUM"',title:.data.node.title},
       custom_fields:[.data.node.fields.nodes[]|select(.dataType=="SINGLE_SELECT" or .dataType=="TEXT" or .dataType=="NUMBER" or .dataType=="DATE" or .dataType=="ITERATION")|select(.name!="Status" or true)|{name,dataType,options:(.options//[]|map({name,color,description}))}],
       views:[.data.node.views.nodes[]|{name,layout,filter:(.filter//""),columns:[.fields.nodes[].name],group_by:[.groupByFields.nodes[].name],board_columns:[.verticalGroupByFields.nodes[].name],sort:[.sortByFields.nodes[]|"\(.field.name):\(.direction)"]}],
       workflows:[.data.node.workflows.nodes[]|select(.enabled)|.name]}' > "$OUT"
echo "captured $(jq -r '.custom_fields|length' "$OUT") custom fields, $(jq -r '.views|length' "$OUT") views, $(jq -r '.workflows|length' "$OUT") enabled workflows → $OUT"
