# shared helpers — sourced by the other scripts
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC2034 # consumed by scripts that source this file (rulesets.sh, project.sh, …), not here
MANIFESTS="$HERE/../manifests"
DRY=${DRY_RUN:-0}
say(){ printf '%s\n' "$*" >&2; }
run(){ if [ "$DRY" = 1 ]; then printf 'DRY: ' >&2; printf '%q ' "$@" >&2; printf '\n' >&2; else "$@"; fi; }
need(){ command -v "$1" >/dev/null || { say "missing: $1"; exit 3; }; }
need gh; need jq
repo_nwo(){ gh repo view --json nameWithOwner --jq .nameWithOwner; }
gql(){ # gql "<query>" '<variables json>' → runs via --input so list/ID variables are typed correctly
  local q=$1 v=${2:-'{}'}; if [ "$DRY" = 1 ]; then say "DRY gql: ${q:0:80}… vars=$v"; return 0; fi
  jq -n --arg q "$q" --argjson v "$v" '{query:$q,variables:$v}' | gh api graphql --input -; }
