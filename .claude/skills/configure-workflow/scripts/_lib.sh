# shared helpers — sourced by the other scripts
# shellcheck shell=bash
set -euo pipefail
# Enforce the bash >= 4 floor these scripts rely on (case-folding parameter
# expansion, e.g. ${var,,}, in grant.sh) with a named failure instead of the
# runtime "bad substitution" bash 3.2 (still the system /bin/bash on macOS)
# would hit at expansion time (#1338). The check itself is bash-3-safe:
# BASH_VERSINFO exists since bash 2.
if [ -z "${BASH_VERSINFO:-}" ] || [ "${BASH_VERSINFO[0]}" -lt 4 ]; then
  printf '%s\n' "configure-workflow scripts require bash >= 4 (found: ${BASH_VERSION:-unknown}); on macOS install a newer bash (e.g. 'brew install bash') and invoke scripts with that binary" >&2
  exit 3
fi
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
