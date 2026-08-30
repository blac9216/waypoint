#!/usr/bin/env bash
# adr-index.sh — generate and (optionally) verify/write a Markdown index table
# of Architecture Decision Records.
#
# Usage: adr-index.sh [--root <repo-root>] [--adr-dir docs/adr] [--check | --write]
#
#   (default)  print the generated table to stdout
#   --check    compare the generated table against the block in
#              <adr-dir>/README.md between markers
#              <!-- adr-index:start --> / <!-- adr-index:end -->
#              (exit 1, README.md: ADR_INDEX_DRIFT, if it differs or the
#              markers are missing)
#   --write    replace that block in README.md with the generated table,
#              creating the markers at the end of the file if absent
#
# --root defaults to `git rev-parse --show-toplevel`, falling back to the
# current directory. --adr-dir is relative to --root unless given as an
# absolute path, and itself defaults to docs/adr. If the resolved ADR
# directory does not exist, that is not a usage error: adr-index.sh treats
# it as zero ADRs — the generated table is "0 ADRs" plus an empty
# (headers-only) table, and each mode proceeds normally against it (print
# prints it, --check compares it to README.md as usual, --write writes it
# as usual).
# Only a malformed command line (bad flag, missing argument) is a usage
# error (exit 2).
#
# Each ADR file NNNN-*.md must carry a `Status:` line within its first 15
# lines (accepted forms: `Status: Accepted`, `**Status:** Accepted`,
# `- Status: Accepted`; values: Proposed | Accepted | Superseded |
# Deprecated), and may carry `Supersedes: 0006` and/or
# `Superseded-by: 0013, 0014` (also accepts `Superseded by:`), and/or
# `Amends: 0013` and/or `Amended-by: 0017` (also accepts `Amended by:`) in
# the same region. Consistency problems are reported one per line on stderr
# as `<file>: <CODE> <message>` and cause a non-zero exit:
#   ADR_NO_STATUS            no Status line found in the first 15 lines
#   ADR_BAD_STATUS           Status value is not one of the four allowed
#   ADR_STATUS_TRAILING      Status line carries prose after the value; use
#                            Supersedes:/Superseded-by: lines instead
#   ADR_LINK_ASYMMETRIC      Supersedes/Superseded-by, or Amends/Amended-by,
#                            don't agree both ways
#   ADR_SUPERSEDED_WRONG_STATUS  has Superseded-by but Status != Superseded
#                            (an Amended-by line never affects Status)
#   ADR_MISSING_TARGET       Supersedes/Superseded-by/Amends/Amended-by
#                             references a number with no corresponding ADR
#                             file
#
# --write updates the README.md block even when findings are present (the
# findings are still printed to stderr and the exit code is still 1);
# --check and the default print mode do not emit a table when findings
# exist.
set -euo pipefail

usage() {
  echo "Usage: adr-index.sh [--root <repo-root>] [--adr-dir docs/adr] [--check | --write]" >&2
}

root_arg=""
adr_dir_arg="docs/adr"
mode="print"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --root)
      [[ $# -ge 2 ]] || { echo "adr-index.sh: --root requires an argument" >&2; exit 2; }
      root_arg="$2"
      shift 2
      ;;
    --root=*)
      root_arg="${1#--root=}"
      shift
      ;;
    --adr-dir)
      [[ $# -ge 2 ]] || { echo "adr-index.sh: --adr-dir requires an argument" >&2; exit 2; }
      adr_dir_arg="$2"
      shift 2
      ;;
    --adr-dir=*)
      adr_dir_arg="${1#--adr-dir=}"
      shift
      ;;
    --check)
      mode="check"
      shift
      ;;
    --write)
      mode="write"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "adr-index.sh: unrecognized argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

if [[ -n "$root_arg" ]]; then
  root="$root_arg"
else
  root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi

if [[ ! -d "$root" ]]; then
  echo "adr-index.sh: root directory not found: $root" >&2
  exit 2
fi
root="$(cd "$root" && pwd)"

if [[ "$adr_dir_arg" = /* ]]; then
  adr_dir="$adr_dir_arg"
else
  adr_dir="$root/$adr_dir_arg"
fi

# A missing ADR directory is not a usage error: treat it as zero ADRs and
# report that directly, regardless of --check/--write, since there is
# nothing to compare or write against.
if [[ ! -d "$adr_dir" ]]; then
  echo "0 ADRs"
  echo "| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |"
  echo "|---|---|---|---|---|---|---|---|"
  exit 0
fi

declare -a NUMS=()
declare -A TITLE=()
declare -A STATUS=()
declare -A STATUSREST=()
declare -A SUPERSEDES=()
declare -A SUPERSEDEDBY=()
declare -A AMENDS=()
declare -A AMENDEDBY=()
declare -A DECISION=()
declare -A FILENAME=()

# escape_cell TEXT — make TEXT safe as a single Markdown table cell.
escape_cell() {
  local text="$1"
  text="${text//|/\\|}"
  printf '%s' "$text"
}

# strip_title_prefix HEADING — drop a leading ADR-NNNN:/NNNN./NNNN — prefix.
strip_title_prefix() {
  local heading="$1"
  heading="$(sed -E \
    -e 's/^ADR-[[:digit:]]+:[[:space:]]*//' \
    -e 's/^[[:digit:]]+\.[[:space:]]*//' \
    -e 's/^[[:digit:]]+[[:space:]]+—[[:space:]]*//' \
    <<<"$heading")"
  printf '%s' "$heading"
}

# truncate_decision TEXT — cap TEXT at 100 characters, backing off to the
# last word boundary within that cap so words are never cut mid-word;
# appends "…" only when truncation actually happened.
truncate_decision() {
  local text="$1" max=100
  if [[ "${#text}" -le "$max" ]]; then
    printf '%s' "$text"
    return
  fi
  local cut="${text:0:$max}"
  if [[ "$cut" == *[[:space:]]* ]]; then
    cut="${cut%[[:space:]]*}"
  fi
  printf '%s…' "$cut"
}

shopt -s nullglob
files=("$adr_dir"/*.md)
shopt -u nullglob

for f in "${files[@]}"; do
  bn="$(basename "$f")"
  [[ "$bn" =~ ^([[:digit:]]+)-.*\.md$ ]] || continue
  num="${BASH_REMATCH[1]}"

  NUMS+=("$num")
  FILENAME["$num"]="$bn"

  heading_line="$(grep -m1 '^# ' "$f" || true)"
  heading="${heading_line#\# }"
  TITLE["$num"]="$(strip_title_prefix "$heading")"

  head15="$(head -n 15 "$f")"

  status_val=""
  status_rest=""
  supersedes_val=""
  supersededby_val=""
  amends_val=""
  amendedby_val=""
  while IFS= read -r line; do
    if [[ -z "$status_val" ]] && [[ "$line" =~ ^[[:space:]]*-?[[:space:]]*(\*\*Status:\*\*|Status:)[[:space:]]*(.*)$ ]]; then
      status_val="${BASH_REMATCH[2]}"
      status_val="${status_val%%[;,]*}"
      status_val="${status_val%%[[:space:]]*}"
      status_rest="${BASH_REMATCH[2]#"$status_val"}"
      status_rest="${status_rest#"${status_rest%%[![:space:]]*}"}"
    elif [[ -z "$supersedes_val" ]] && [[ "$line" =~ ^[[:space:]]*-?[[:space:]]*(\*\*Supersedes:\*\*|Supersedes:)[[:space:]]*(.*)$ ]]; then
      supersedes_val="${BASH_REMATCH[2]}"
    elif [[ -z "$supersededby_val" ]] && [[ "$line" =~ ^[[:space:]]*-?[[:space:]]*(\*\*Superseded[-[:space:]]by:\*\*|Superseded[-[:space:]]by:)[[:space:]]*(.*)$ ]]; then
      supersededby_val="${BASH_REMATCH[2]}"
    elif [[ -z "$amends_val" ]] && [[ "$line" =~ ^[[:space:]]*-?[[:space:]]*(\*\*Amends:\*\*|Amends:)[[:space:]]*(.*)$ ]]; then
      amends_val="${BASH_REMATCH[2]}"
    elif [[ -z "$amendedby_val" ]] && [[ "$line" =~ ^[[:space:]]*-?[[:space:]]*(\*\*Amended[-[:space:]]by:\*\*|Amended[-[:space:]]by:)[[:space:]]*(.*)$ ]]; then
      amendedby_val="${BASH_REMATCH[2]}"
    fi
  done <<<"$head15"

  STATUS["$num"]="$status_val"
  STATUSREST["$num"]="$status_rest"
  SUPERSEDES["$num"]="$supersedes_val"
  SUPERSEDEDBY["$num"]="$supersededby_val"
  AMENDS["$num"]="$amends_val"
  AMENDEDBY["$num"]="$amendedby_val"

  decision_line=""
  in_decision=0
  while IFS= read -r line; do
    if [[ "$in_decision" -eq 1 ]]; then
      if [[ "$line" =~ ^##[[:space:]] ]]; then
        break
      fi
      if [[ -n "${line//[[:space:]]/}" ]]; then
        decision_line="$line"
        break
      fi
    elif [[ "$line" =~ ^##[[:space:]]+Decision[[:space:]]*$ ]]; then
      in_decision=1
    fi
  done <"$f"
  DECISION["$num"]="$(truncate_decision "$decision_line")"
done

# normalize_num_list "0006, 0013" -> "0006 0013"
normalize_num_list() {
  local raw="$1" out=() part
  IFS=',' read -ra parts <<<"$raw"
  for part in "${parts[@]}"; do
    part="${part//[[:space:]]/}"
    [[ -n "$part" ]] && out+=("$part")
  done
  printf '%s\n' "${out[@]+"${out[@]}"}"
}

declare -a FINDINGS=()

for num in "${NUMS[@]}"; do
  file="${FILENAME[$num]}"
  status="${STATUS[$num]}"

  if [[ -n "${STATUSREST[$num]}" ]]; then
    FINDINGS+=("$file: ADR_STATUS_TRAILING status line has trailing text '${STATUSREST[$num]}'; move relationships to Supersedes:/Superseded-by: lines")
  fi
  if [[ -z "$status" ]]; then
    FINDINGS+=("$file: ADR_NO_STATUS no Status line found in the first 15 lines")
  elif [[ "$status" != "Proposed" && "$status" != "Accepted" && "$status" != "Superseded" && "$status" != "Deprecated" ]]; then
    FINDINGS+=("$file: ADR_BAD_STATUS unrecognized status value '$status'")
  fi

  if [[ -n "${SUPERSEDEDBY[$num]}" && "$status" != "Superseded" ]]; then
    FINDINGS+=("$file: ADR_SUPERSEDED_WRONG_STATUS has Superseded-by but status is '$status', not Superseded")
  fi

  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    if [[ -z "${FILENAME[$target]+x}" ]]; then
      FINDINGS+=("$file: ADR_MISSING_TARGET references $target via Supersedes, which has no ADR file")
      continue
    fi
    target_supersededby="$(normalize_num_list "${SUPERSEDEDBY[$target]}")"
    if ! grep -qx "$num" <<<"$target_supersededby"; then
      FINDINGS+=("$file: ADR_LINK_ASYMMETRIC supersedes $target, but ${FILENAME[$target]} does not list $num as Superseded-by")
    fi
  done < <(normalize_num_list "${SUPERSEDES[$num]}")

  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    if [[ -z "${FILENAME[$target]+x}" ]]; then
      FINDINGS+=("$file: ADR_MISSING_TARGET references $target via Superseded-by, which has no ADR file")
      continue
    fi
    target_supersedes="$(normalize_num_list "${SUPERSEDES[$target]}")"
    if ! grep -qx "$num" <<<"$target_supersedes"; then
      FINDINGS+=("$file: ADR_LINK_ASYMMETRIC is Superseded-by $target, but ${FILENAME[$target]} does not list $num as Supersedes")
    fi
  done < <(normalize_num_list "${SUPERSEDEDBY[$num]}")

  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    if [[ -z "${FILENAME[$target]+x}" ]]; then
      FINDINGS+=("$file: ADR_MISSING_TARGET references $target via Amends, which has no ADR file")
      continue
    fi
    target_amendedby="$(normalize_num_list "${AMENDEDBY[$target]}")"
    if ! grep -qx "$num" <<<"$target_amendedby"; then
      FINDINGS+=("$file: ADR_LINK_ASYMMETRIC amends $target, but ${FILENAME[$target]} does not list $num as Amended-by")
    fi
  done < <(normalize_num_list "${AMENDS[$num]}")

  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    if [[ -z "${FILENAME[$target]+x}" ]]; then
      FINDINGS+=("$file: ADR_MISSING_TARGET references $target via Amended-by, which has no ADR file")
      continue
    fi
    target_amends="$(normalize_num_list "${AMENDS[$target]}")"
    if ! grep -qx "$num" <<<"$target_amends"; then
      FINDINGS+=("$file: ADR_LINK_ASYMMETRIC is Amended-by $target, but ${FILENAME[$target]} does not list $num as Amends")
    fi
  done < <(normalize_num_list "${AMENDEDBY[$num]}")
done

if [[ "${#FINDINGS[@]}" -gt 0 ]]; then
  for finding in "${FINDINGS[@]}"; do
    echo "$finding" >&2
  done
  if [[ "$mode" != "write" ]]; then
    exit 1
  fi
fi

# format_num_list "0006 0013" -> "0006, 0013" (also handles raw comma input)
format_num_list() {
  local raw="$1"
  local normalized
  normalized="$(normalize_num_list "$raw" | paste -sd ',' - 2>/dev/null || true)"
  normalized="${normalized//,/, }"
  if [[ -z "$normalized" ]]; then
    printf -- '-'
  else
    printf '%s' "$normalized"
  fi
}

build_table() {
  echo "| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |"
  echo "|---|---|---|---|---|---|---|---|"
  local sorted=()
  if [[ "${#NUMS[@]}" -gt 0 ]]; then
    mapfile -t sorted < <(printf '%s\n' "${NUMS[@]}" | sort -n)
  fi
  for num in "${sorted[@]+"${sorted[@]}"}"; do
    local file title status supersedes supersededby amends amendedby decision
    file="${FILENAME[$num]}"
    title="$(escape_cell "${TITLE[$num]}")"
    status="$(escape_cell "${STATUS[$num]}")"
    supersedes="$(format_num_list "${SUPERSEDES[$num]}")"
    supersededby="$(format_num_list "${SUPERSEDEDBY[$num]}")"
    amends="$(format_num_list "${AMENDS[$num]}")"
    amendedby="$(format_num_list "${AMENDEDBY[$num]}")"
    decision="$(escape_cell "${DECISION[$num]}")"
    echo "| [$num]($file) | $title | $status | $supersedes | $supersededby | $amends | $amendedby | $decision |"
  done
}

table="$(build_table)"

case "$mode" in
  print)
    printf '%s\n' "$table"
    ;;
  check)
    readme="$adr_dir/README.md"
    if [[ ! -f "$readme" ]]; then
      echo "README.md: ADR_INDEX_DRIFT $readme does not exist" >&2
      exit 1
    fi
    existing="$(sed -n '/<!-- adr-index:start -->/,/<!-- adr-index:end -->/p' "$readme")"
    if [[ -z "$existing" ]]; then
      echo "README.md: ADR_INDEX_DRIFT markers <!-- adr-index:start --> / <!-- adr-index:end --> not found" >&2
      exit 1
    fi
    existing_body="$(sed '1d;$d' <<<"$existing")"
    if [[ "$existing_body" != "$table" ]]; then
      echo "README.md: ADR_INDEX_DRIFT generated table does not match the block in README.md" >&2
      exit 1
    fi
    echo "ADR index up to date"
    ;;
  write)
    readme="$adr_dir/README.md"
    [[ -f "$readme" ]] || : >"$readme"
    block="<!-- adr-index:start -->
$table
<!-- adr-index:end -->"
    if grep -q '<!-- adr-index:start -->' "$readme" 2>/dev/null && grep -q '<!-- adr-index:end -->' "$readme" 2>/dev/null; then
      tmp="$(mktemp "${TMPDIR:-/tmp}/adr-index-write.XXXXXX")"
      awk -v block="$block" '
        BEGIN { printing = 1 }
        /<!-- adr-index:start -->/ { print block; printing = 0; next }
        /<!-- adr-index:end -->/ { printing = 1; next }
        printing { print }
      ' "$readme" >"$tmp"
      mv "$tmp" "$readme"
    else
      if [[ -s "$readme" ]] && [[ "$(tail -c1 "$readme")" != "" ]]; then
        printf '\n' >>"$readme"
      fi
      printf '\n%s\n' "$block" >>"$readme"
    fi
    echo "wrote $readme"
    if [[ "${#FINDINGS[@]}" -gt 0 ]]; then
      exit 1
    fi
    ;;
esac
