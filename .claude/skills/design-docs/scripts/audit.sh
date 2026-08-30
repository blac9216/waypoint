#!/usr/bin/env bash
# audit.sh — Tier 1 (mechanical) audit of a repository's design-docs
# adoption. Parses docs/doc-manifest.md for the adopted shape,
# runs check-pointers.sh and adr-index.sh --check, runs the structural
# checks defined in references/standard.md (MADR sections, Diátaxis
# layout, index coverage, glossary, C4 architecture levels, design-set
# existence), and writes a filled-out copy of templates/gap-report.md.
#
# Usage: audit.sh --out <path> [--root <repo-root>]
#
# Exit status:
#   0  no Tier 1 findings
#   1  Tier 1 findings reported
#   2  usage error, or docs/doc-manifest.md is missing (the
#      repository has not adopted the standard) — a minimal report is
#      still written
#
# Findings are printed to stderr, one per line, as `path[:line] CODE
# message`. The report path and a one-line summary are printed to
# stdout.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
GAP_REPORT_TEMPLATE="$SKILL_DIR/templates/gap-report.md"

usage() {
  echo "Usage: audit.sh --out <path> [--root <repo-root>]" >&2
}

OUT=""
ROOT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="${2:-}"; shift 2 ;;
    --out=*) OUT="${1#--out=}"; shift ;;
    --root) ROOT="${2:-}"; shift 2 ;;
    --root=*) ROOT="${1#--root=}"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "audit.sh: unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

if [ -z "$OUT" ]; then
  echo "audit.sh: --out is required" >&2
  usage
  exit 2
fi

if [ -z "$ROOT" ]; then
  ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi
if [ ! -d "$ROOT" ]; then
  echo "audit.sh: root directory not found: $ROOT" >&2
  exit 2
fi
ROOT="$(cd "$ROOT" && pwd)"

if [ ! -f "$GAP_REPORT_TEMPLATE" ]; then
  echo "audit.sh: gap report template not found: $GAP_REPORT_TEMPLATE" >&2
  exit 2
fi

REPO_NAME=""
ORIGIN_URL="$(git -C "$ROOT" remote get-url origin 2>/dev/null || true)"
if [ -n "$ORIGIN_URL" ]; then
  REPO_NAME="$(basename "$ORIGIN_URL")"
  REPO_NAME="${REPO_NAME%.git}"
fi
[ -n "$REPO_NAME" ] || REPO_NAME="$(basename "$ROOT")"
TODAY="$(date +%Y-%m-%d)"

FINDINGS_FILE="$(mktemp "${TMPDIR:-/tmp}/audit-findings.XXXXXX")"
cleanup() { rm -f "$FINDINGS_FILE"; }
trap cleanup EXIT

# add_finding PATH LINE CODE MESSAGE... — LINE may be empty.
add_finding() {
  local path="$1" line="$2" code="$3"
  shift 3
  local message="$*"
  if [ -n "$line" ]; then
    printf '%s:%s %s %s\n' "$path" "$line" "$code" "$message" >> "$FINDINGS_FILE"
  else
    printf '%s %s %s\n' "$path" "$code" "$message" >> "$FINDINGS_FILE"
  fi
}

DOC_STANDARD="$ROOT/docs/doc-manifest.md"

# ---------------------------------------------------------------------------
# Not-adopted short circuit.
# ---------------------------------------------------------------------------
if [ ! -f "$DOC_STANDARD" ]; then
  echo "not adopted: docs/doc-manifest.md missing" >&2
  {
    sed -e "s#<repo>#$REPO_NAME#" -e "s#<date>#$TODAY#" "$GAP_REPORT_TEMPLATE"
  } | awk -v msg="NOTE: not adopted — docs/doc-manifest.md is missing; no checks were run." '
    /^## Summary$/ { print; getline; print "Tier 1 findings: 0 · Tier 2 findings: 0 (agent fills) · Clusters: 0 (agent fills)"; next }
    /^<!-- filled by audit.sh:/ { print; print msg; next }
    { print }
  ' > "$OUT"
  echo "$OUT"
  echo "audit: not adopted (docs/doc-manifest.md missing)"
  exit 2
fi

# ---------------------------------------------------------------------------
# Parse docs/doc-manifest.md for the adopted shape. Tolerant of
# whitespace; missing sections are simply skipped (with a NOTE line).
# ---------------------------------------------------------------------------
declare -a NOTES=()
# SKIPPED_CHECKS — entries for the report's "## Skipped checks" section: a
# check category was skipped because a doc-manifest.md section/line it
# depends on is missing (as opposed to a NOTES entry for a runtime
# condition, e.g. a configured path that doesn't exist on disk).
declare -a SKIPPED_CHECKS=()
declare -a DESIGN_SET=()
DIATAXIS_TUTORIALS=""
DIATAXIS_HOWTO=""
DIATAXIS_REFERENCE=""
DIATAXIS_EXPLANATION=""
INDEX_PATH=""
ADR_DIR=""
declare -a RATIONALE_AREAS=()
GLOSSARY_PATH=""
DOMAIN_MODEL_PATH=""
ARCHITECTURE_PATH=""

# Design set: bullets under "## Design set" until the next "## " heading.
if grep -q '^## Design set' "$DOC_STANDARD"; then
  while IFS= read -r line; do
    case "$line" in
      -\ *)
        p="${line#- }"
        p="$(printf '%s' "$p" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
        [ -n "$p" ] && DESIGN_SET+=("$p")
        ;;
    esac
  done < <(awk '/^## Design set/{f=1;next} /^## /{f=0} f' "$DOC_STANDARD")
else
  NOTES+=("Design set section missing from doc-manifest.md — design-set existence check skipped")
  SKIPPED_CHECKS+=("Design-set existence — needs '## Design set' section in doc-manifest.md")
fi

if [ "${#DESIGN_SET[@]}" -gt 0 ]; then
  for p in "${DESIGN_SET[@]}"; do
    bn="$(basename "$p")"
    if [ "$bn" = "architecture.md" ]; then
      ARCHITECTURE_PATH="$p"
    fi
  done
fi

# Diátaxis directories line.
diataxis_line="$(grep -m1 -E 'tutorials:.*how-to:.*reference:.*explanation:' "$DOC_STANDARD" || true)"
if [ -n "$diataxis_line" ]; then
  DIATAXIS_TUTORIALS="$(printf '%s\n' "$diataxis_line" | sed -n -E 's/.*tutorials:[[:space:]]*([^ ]+).*/\1/p')"
  DIATAXIS_HOWTO="$(printf '%s\n' "$diataxis_line" | sed -n -E 's/.*how-to:[[:space:]]*([^ ]+).*/\1/p')"
  DIATAXIS_REFERENCE="$(printf '%s\n' "$diataxis_line" | sed -n -E 's/.*reference:[[:space:]]*([^ ]+).*/\1/p')"
  DIATAXIS_EXPLANATION="$(printf '%s\n' "$diataxis_line" | sed -n -E 's/.*explanation:[[:space:]]*([^ ]+).*/\1/p')"
else
  NOTES+=("Diátaxis directories line missing from doc-manifest.md — Diátaxis checks skipped")
  SKIPPED_CHECKS+=("Diátaxis layout — needs the tutorials:/how-to:/reference:/explanation: line in doc-manifest.md")
fi

index_line="$(grep -m1 -E '^Index:' "$DOC_STANDARD" || true)"
if [ -n "$index_line" ]; then
  INDEX_PATH="$(printf '%s\n' "$index_line" | sed -n -E 's/^Index:[[:space:]]*([^[:space:]]+).*/\1/p')"
else
  NOTES+=("Index: line missing from doc-manifest.md — index-coverage check skipped")
fi

if [ -z "$DIATAXIS_TUTORIALS" ] || [ -z "$INDEX_PATH" ]; then
  index_missing_bits=""
  [ -z "$DIATAXIS_TUTORIALS" ] && index_missing_bits="${index_missing_bits}the tutorials:/how-to:/reference:/explanation: line, "
  [ -z "$INDEX_PATH" ] && index_missing_bits="${index_missing_bits}'Index:' line, "
  index_missing_bits="${index_missing_bits%, }"
  SKIPPED_CHECKS+=("Index coverage — needs $index_missing_bits in doc-manifest.md")
fi

adr_line="$(grep -m1 -E '^Directory:' "$DOC_STANDARD" || true)"
if [ -n "$adr_line" ]; then
  ADR_DIR="$(printf '%s\n' "$adr_line" | sed -n -E 's/^Directory:[[:space:]]*([^[:space:]]+).*/\1/p')"
  ADR_DIR="${ADR_DIR%/}"
else
  NOTES+=("ADR Directory: line missing from doc-manifest.md — adr-index.sh will use its default")
  ADR_DIR="docs/adr"
fi

# Rationale areas: bullets under "## Rationale areas", shape "- area → file".
if grep -q '^## Rationale areas' "$DOC_STANDARD"; then
  while IFS= read -r line; do
    case "$line" in
      -\ *)
        p="${line#- }"
        file="$(printf '%s' "$p" | sed -n -E 's/.*(→|->)[[:space:]]*([^[:space:]]+).*/\2/p')"
        [ -n "$file" ] && RATIONALE_AREAS+=("$file")
        ;;
    esac
  done < <(awk '/^## Rationale areas/{f=1;next} /^## /{f=0} f' "$DOC_STANDARD")
else
  NOTES+=("Rationale areas section missing from doc-manifest.md")
fi

glossary_line="$(grep -m1 -E 'CONTEXT.*\.md.*at repo root' "$DOC_STANDARD" || true)"
if [ -n "$glossary_line" ]; then
  GLOSSARY_PATH="$(printf '%s\n' "$glossary_line" | sed -n -E 's/^([^[:space:]]+).*/\1/p')"
else
  NOTES+=("Glossary line missing from doc-manifest.md — defaulting to CONTEXT.md")
  GLOSSARY_PATH="CONTEXT.md"
fi
domain_line="$(grep -m1 -E 'domain model:' "$DOC_STANDARD" || true)"
if [ -n "$domain_line" ]; then
  DOMAIN_MODEL_PATH="$(printf '%s\n' "$domain_line" | sed -n -E 's/.*domain model:[[:space:]]*([^[:space:]]+).*/\1/p')"
else
  NOTES+=("domain model: path missing from doc-manifest.md — domain-model term coverage skipped")
  SKIPPED_CHECKS+=("Domain-model term coverage — needs 'domain model:' line in doc-manifest.md")
fi

if [ -z "$ARCHITECTURE_PATH" ]; then
  NOTES+=("No architecture.md entry found in the design set — architecture (C4) check skipped")
  SKIPPED_CHECKS+=("Architecture (C4) — needs an architecture.md path listed under '## Design set' in doc-manifest.md")
fi

# ---------------------------------------------------------------------------
# 1. check-pointers.sh and adr-index.sh --check, forwarded verbatim.
# ---------------------------------------------------------------------------
CHECK_POINTERS_SH="$SCRIPT_DIR/check-pointers.sh"
ADR_INDEX_SH="$SCRIPT_DIR/adr-index.sh"

if [ -x "$CHECK_POINTERS_SH" ] || [ -f "$CHECK_POINTERS_SH" ]; then
  cp_out=""
  cp_rc=0
  cp_out="$(bash "$CHECK_POINTERS_SH" --root "$ROOT" --format text 2>/dev/null)" || cp_rc=$?
  if [ "$cp_rc" -eq 2 ]; then
    NOTES+=("check-pointers.sh usage error")
  elif [ -n "$cp_out" ]; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      echo "$line" >> "$FINDINGS_FILE"
    done <<< "$cp_out"
  fi
fi

if [ -x "$ADR_INDEX_SH" ] || [ -f "$ADR_INDEX_SH" ]; then
  if [ -d "$ROOT/$ADR_DIR" ]; then
    ai_rc=0
    ai_err="$(bash "$ADR_INDEX_SH" --root "$ROOT" --adr-dir "$ADR_DIR" --check 2>&1 1>/dev/null)" || ai_rc=$?
    if [ "$ai_rc" -eq 1 ] && [ -n "$ai_err" ]; then
      while IFS= read -r line; do
        [ -n "$line" ] || continue
        echo "$line" >> "$FINDINGS_FILE"
      done <<< "$ai_err"
    elif [ "$ai_rc" -eq 2 ]; then
      NOTES+=("adr-index.sh usage/setup error: $ai_err")
    fi
  else
    NOTES+=("ADR directory $ADR_DIR does not exist — adr-index.sh skipped")
  fi
fi

# ---------------------------------------------------------------------------
# 2. MADR sections + header Date: for each ADR.
# ---------------------------------------------------------------------------
if [ -d "$ROOT/$ADR_DIR" ]; then
  shopt -s nullglob
  for adr in "$ROOT/$ADR_DIR"/[[:digit:]]*.md; do
    [ -e "$adr" ] || continue
    rel="${adr#"$ROOT"/}"
    for section in "## Context" "## Decision Drivers" "## Considered Options" "## Decision" "## Consequences"; do
      if ! grep -qF "$section" "$adr"; then
        add_finding "$rel" "" "ADR_MISSING_SECTION" "missing '$section' section"
      fi
    done
    if ! head -n 15 "$adr" | grep -qE '^[[:space:]]*-?[[:space:]]*(\*\*Date:\*\*|Date:)'; then
      add_finding "$rel" "" "ADR_NO_DATE" "no Date: line in header block"
    fi
  done
  shopt -u nullglob
fi

# ---------------------------------------------------------------------------
# 3. Diátaxis layout.
# ---------------------------------------------------------------------------
if [ -n "$DIATAXIS_TUTORIALS" ] && [ -d "$ROOT/docs" ]; then
  kind_dirs=("$DIATAXIS_TUTORIALS:tutorials" "$DIATAXIS_HOWTO:how-to" "$DIATAXIS_REFERENCE:reference" "$DIATAXIS_EXPLANATION:explanation")

  while IFS= read -r mdfile; do
    rel="${mdfile#"$ROOT"/}"
    case "$rel" in
      docs/adr/*|docs/rationale/*|docs/process/*|docs/images/*|docs/README.md|docs/doc-manifest.md) continue ;;
    esac
    in_kind_dir=""
    expected_kind=""
    for entry in "${kind_dirs[@]}"; do
      dir="${entry%%:*}"
      kind="${entry##*:}"
      dir_norm="${dir%/}/"
      case "$rel" in
        "$dir_norm"*) in_kind_dir="$dir"; expected_kind="$kind" ;;
      esac
    done
    if [ -z "$in_kind_dir" ]; then
      add_finding "$rel" "" "DOC_OUTSIDE_KIND_DIR" "not inside a Diátaxis kind directory"
      continue
    fi
    kind_line="$(head -n 5 "$mdfile" | grep -m1 -E '^Kind:' || true)"
    if [ -z "$kind_line" ]; then
      add_finding "$rel" "" "DOC_KIND_MISSING" "no 'Kind:' line in first 5 lines"
    else
      found_kind="$(printf '%s\n' "$kind_line" | sed -n -E 's/^Kind:[[:space:]]*([^[:space:]]+).*/\1/p')"
      if [ "$found_kind" != "$expected_kind" ]; then
        add_finding "$rel" "" "DOC_KIND_MISMATCH" "Kind: $found_kind does not match directory ($expected_kind)"
      fi
    fi
  done < <(find "$ROOT/docs" -type f -name '*.md' | sort)
fi

# ---------------------------------------------------------------------------
# 4. Index coverage.
# ---------------------------------------------------------------------------
if [ -n "$INDEX_PATH" ] && [ -n "$DIATAXIS_TUTORIALS" ]; then
  index_file="$ROOT/$INDEX_PATH"
  if [ -f "$index_file" ]; then
    kind_dirs=("$DIATAXIS_TUTORIALS" "$DIATAXIS_HOWTO" "$DIATAXIS_REFERENCE" "$DIATAXIS_EXPLANATION")
    while IFS= read -r mdfile; do
      rel="${mdfile#"$ROOT"/}"
      bn="$(basename "$rel")"
      if ! grep -qF "$bn" "$index_file" && ! grep -qF "$rel" "$index_file"; then
        add_finding "$INDEX_PATH" "" "INDEX_MISSING_DOC" "$rel is not linked from the index"
      fi
    done < <(for d in "${kind_dirs[@]}"; do
      [ -d "$ROOT/$d" ] && find "$ROOT/$d" -type f -name '*.md'
    done | sort)

    index_dir="$(dirname "$index_file")"
    while IFS= read -r link; do
      [ -n "$link" ] || continue
      case "$link" in
        http://*|https://*|\#*) continue ;;
      esac
      target="${link%%#*}"
      [ -n "$target" ] || continue
      candidate="$index_dir/$target"
      if [ ! -f "$candidate" ]; then
        add_finding "$INDEX_PATH" "" "INDEX_DEAD_LINK" "link target does not exist: $link"
      fi
    done < <(awk '
      # Strip HTML comment blocks (possibly multi-line) before extracting
      # links, so a commented-out placeholder link does not read as a dead
      # link. A comment closed and reopened on the same line is handled by
      # looping within the line.
      {
        line = $0
        out = ""
        while (1) {
          if (in_comment) {
            e = index(line, "-->")
            if (e == 0) { line = ""; break }
            line = substr(line, e + 3)
            in_comment = 0
            continue
          }
          s = index(line, "<!--")
          if (s == 0) { out = out line; break }
          out = out substr(line, 1, s - 1)
          line = substr(line, s + 4)
          in_comment = 1
        }
        print out
      }
    ' "$index_file" | grep -oE '\]\([^)]+\.md[^)]*\)' | sed -E 's/^\]\(([^)]+)\)$/\1/')
  else
    NOTES+=("Index file $INDEX_PATH does not exist — index checks skipped")
  fi
fi

# ---------------------------------------------------------------------------
# 5. Glossary.
# ---------------------------------------------------------------------------
if [ -n "$GLOSSARY_PATH" ]; then
  glossary_file="$ROOT/$GLOSSARY_PATH"
  if [ ! -f "$glossary_file" ]; then
    add_finding "$GLOSSARY_PATH" "" "GLOSSARY_MISSING" "glossary file does not exist"
  else
    declare -A GLOSSARY_TERMS=()
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      lineno="${line%%:*}"
      content="${line#*:}"
      term="$(printf '%s\n' "$content" | sed -n -E 's/^\*\*([^*]+)\*\*.*/\1/p')"
      [ -n "$term" ] || continue
      if [ -n "${GLOSSARY_TERMS[$term]+x}" ]; then
        add_finding "$GLOSSARY_PATH" "$lineno" "GLOSSARY_DUPLICATE" "duplicate term '$term'"
      else
        GLOSSARY_TERMS["$term"]=1
      fi
      if printf '%s\n' "$content" | grep -qE '`[^`]*(/|\.cs|\.ts|\.sql)[^`]*`'; then
        add_finding "$GLOSSARY_PATH" "$lineno" "GLOSSARY_IMPLEMENTATION_DETAIL" "term '$term' entry contains an implementation-detail reference"
      fi
    done < <(awk '
      /^## Terms/ { f=1; next }
      /^## / { f=0 }
      f && /^\*\*[^*]+\*\*/ { print NR ":" $0 }
    ' "$glossary_file")

    if [ -n "$DOMAIN_MODEL_PATH" ] && [ -f "$ROOT/$DOMAIN_MODEL_PATH" ]; then
      while IFS= read -r term; do
        [ -n "$term" ] || continue
        if [ -z "${GLOSSARY_TERMS[$term]+x}" ]; then
          add_finding "$DOMAIN_MODEL_PATH" "" "GLOSSARY_TERM_UNLISTED" "term '$term' is not listed in $GLOSSARY_PATH"
        fi
      done < <(grep -oE '^\*\*[^*]+\*\*' "$ROOT/$DOMAIN_MODEL_PATH" | sed -E 's/^\*\*([^*]+)\*\*/\1/' | \
        awk -F'#|\\(|\\)|,|:' '
          # A term is real glossary text, not prose emphasis, only when the
          # bold text is <=4 words and contains none of # ( ) , : — a
          # cross-reference like "**Roll-off (issue #708, epic #706)**" or a
          # longer emphasised fragment fails this and is ignored.
          NF > 1 { next }
          { n = split($0, words, /[[:space:]]+/); if (n <= 4 && n > 0) print }
        ' | sort -u)
    fi
  fi
fi

# ---------------------------------------------------------------------------
# 6. Architecture (C4).
# ---------------------------------------------------------------------------
if [ -n "$ARCHITECTURE_PATH" ]; then
  arch_file="$ROOT/$ARCHITECTURE_PATH"
  if [ -f "$arch_file" ]; then
    declare -a LEVEL_LINES=()
    for level in Context Container Component; do
      lineno="$(grep -n -m1 -E "^##[[:space:]]+${level}([[:space:]]|\$)" "$arch_file" | cut -d: -f1 || true)"
      if [ -z "$lineno" ]; then
        add_finding "$ARCHITECTURE_PATH" "" "ARCH_MISSING_LEVEL" "missing '## $level' heading"
        LEVEL_LINES+=("0")
      else
        LEVEL_LINES+=("$lineno")
      fi
    done
    prev=0
    order_ok=1
    for lineno in "${LEVEL_LINES[@]}"; do
      if [ "$lineno" != "0" ]; then
        if [ "$lineno" -le "$prev" ] && [ "$prev" != "0" ]; then
          order_ok=0
        fi
        prev="$lineno"
      fi
    done
    if [ "$order_ok" -eq 0 ]; then
      add_finding "$ARCHITECTURE_PATH" "" "ARCH_LEVEL_ORDER" "Context/Container/Component headings are not in order"
    fi

    total_lines="$(wc -l < "$arch_file" | tr -d '[:space:]')"
    idx=0
    for level in Context Container Component; do
      start="${LEVEL_LINES[$idx]}"
      idx=$((idx + 1))
      [ "$start" != "0" ] || continue
      end="$total_lines"
      for other in "${LEVEL_LINES[@]}"; do
        if [ "$other" != "0" ] && [ "$other" -gt "$start" ] && [ "$other" -lt "$end" ]; then
          end="$other"
        fi
      done
      if ! sed -n "${start},${end}p" "$arch_file" | grep -qE '^```mermaid'; then
        add_finding "$ARCHITECTURE_PATH" "$start" "ARCH_NO_DIAGRAM" "'$level' section has no mermaid diagram"
      fi
    done
  else
    NOTES+=("Architecture doc $ARCHITECTURE_PATH does not exist — C4 check skipped")
  fi
fi

# ---------------------------------------------------------------------------
# 7. Design-set existence.
# ---------------------------------------------------------------------------
for p in "${DESIGN_SET[@]}"; do
  if [ ! -e "$ROOT/$p" ]; then
    add_finding "$p" "" "DESIGNSET_MISSING" "design-set path does not exist"
  fi
done

# ---------------------------------------------------------------------------
# Write the report.
# ---------------------------------------------------------------------------
count_findings=$(wc -l < "$FINDINGS_FILE" | tr -d '[:space:]')

declare -A GROUP_COUNT=()
declare -A GROUP_LINES=()
group_order=()
while IFS= read -r line; do
  [ -n "$line" ] || continue
  # Extract the CODE token: text between the first "] " boundary and the
  # message. Findings are "path[:line] CODE message" or "path CODE message".
  code="$(printf '%s\n' "$line" | grep -oE '[A-Z][A-Z0-9]*(_[A-Z0-9]+)+' | head -n1)"
  [ -n "$code" ] || code="UNKNOWN"
  if [ -z "${GROUP_COUNT[$code]+x}" ]; then
    GROUP_COUNT["$code"]=0
    group_order+=("$code")
  fi
  GROUP_COUNT["$code"]=$(( GROUP_COUNT["$code"] + 1 ))
  GROUP_LINES["$code"]="${GROUP_LINES[$code]:-}${line}"$'\n'
done < "$FINDINGS_FILE"

TIER1_BODY=""
for code in "${group_order[@]}"; do
  TIER1_BODY="${TIER1_BODY}### ${code} (${GROUP_COUNT[$code]})"$'\n'
  TIER1_BODY="${TIER1_BODY}${GROUP_LINES[$code]}"
done

chore_count=0
for code in ENTRY_NO_REFS ENTRY_TOO_SHORT ADR_NO_DATE DOC_KIND_MISSING; do
  chore_count=$(( chore_count + ${GROUP_COUNT[$code]:-0} ))
done

skipped_count="${#SKIPPED_CHECKS[@]}"
SKIPPED_BLOCK=""
if [ "$skipped_count" -gt 0 ]; then
  SKIPPED_BLOCK="## Skipped checks"$'\n\n'
  for s in "${SKIPPED_CHECKS[@]}"; do
    SKIPPED_BLOCK="${SKIPPED_BLOCK}- ${s}"$'\n'
  done
  SKIPPED_BLOCK="${SKIPPED_BLOCK}"$'\n'
fi

{
  sed -e "s#<repo>#$REPO_NAME#" -e "s#<date>#$TODAY#" "$GAP_REPORT_TEMPLATE"
} | awk -v tier1="$count_findings" -v skipped="$skipped_count" -v skipblock="$SKIPPED_BLOCK" '
  /^## Summary$/ { print; getline; print "Tier 1 findings: " tier1 " · Tier 2 findings: 0 (agent fills) · Clusters: (agent fills) · Skipped checks: " skipped; next }
  /^## Tier 1 — mechanical$/ { if (skipblock != "") printf "%s", skipblock; print; next }
  { print }
' > "$OUT.tmp"

# Splice in the Tier 1 body, notes, and the C2 Where: line.
NOTES_BLOCK=""
for n in "${NOTES[@]+"${NOTES[@]}"}"; do
  NOTES_BLOCK="${NOTES_BLOCK}NOTE: ${n}"$'\n'
done

awk -v body="$TIER1_BODY" -v notes="$NOTES_BLOCK" -v chore="$chore_count" '
  /^<!-- filled by audit.sh:/ {
    print
    if (notes != "") printf "%s", notes
    if (body != "") printf "%s", body
    next
  }
  /^### C2 — chore: trivial Tier 1 fixes$/ {
    print
    getline nextline
    # nextline is "What: · Where: · Size: S"
    sub(/Where: /, "Where: " chore " ENTRY_NO_REFS/ENTRY_TOO_SHORT/ADR_NO_DATE/DOC_KIND_MISSING finding(s) ", nextline)
    print nextline
    next
  }
  { print }
' "$OUT.tmp" > "$OUT"
rm -f "$OUT.tmp"

echo "$OUT"
echo "audit: Tier 1 findings: $count_findings"

if [ "$count_findings" -gt 0 ]; then
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    echo "$line" >&2
  done < "$FINDINGS_FILE"
  exit 1
fi

exit 0
