#!/usr/bin/env bash
# check-pointers.sh — verify "why" pointer integrity between code comments
# and a rationale index (see docs/rationale/deploy.md for the convention
# this checks: `# why: docs/rationale/<area>.md#<slug>` comments must each
# resolve to a real `### <slug>` heading, and every rationale entry must
# carry a 2-6 line body plus a `Refs:` line, with slugs unique per file).
#
# Usage: check-pointers.sh [--root <repo-root>] [--format text|json]
#                           [--rationale-dir docs/rationale]
#
# Exit status: 0 = no findings, 1 = findings reported, 2 = usage error.
# A one-line summary (`check-pointers: N findings`) always goes to stderr.
set -euo pipefail

usage() {
  echo "Usage: check-pointers.sh [--root <repo-root>] [--format text|json] [--rationale-dir docs/rationale]" >&2
}

ROOT=""
FORMAT="text"
RATIONALE_DIR="docs/rationale"

while [ $# -gt 0 ]; do
  case "$1" in
    --root) ROOT="${2:-}"; shift 2 ;;
    --root=*) ROOT="${1#--root=}"; shift ;;
    --format) FORMAT="${2:-}"; shift 2 ;;
    --format=*) FORMAT="${1#--format=}"; shift ;;
    --rationale-dir) RATIONALE_DIR="${2:-}"; shift 2 ;;
    --rationale-dir=*) RATIONALE_DIR="${1#--rationale-dir=}"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "check-pointers: unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

case "$FORMAT" in
  text|json) ;;
  *) echo "check-pointers: --format must be 'text' or 'json'" >&2; exit 2 ;;
esac

if [ "$FORMAT" = "json" ] && ! command -v jq >/dev/null 2>&1; then
  echo "check-pointers: --format json requires jq on PATH" >&2
  exit 2
fi

if [ -z "$RATIONALE_DIR" ]; then
  echo "check-pointers: --rationale-dir must not be empty" >&2
  exit 2
fi

if [ -z "$ROOT" ]; then
  ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi
if [ ! -d "$ROOT" ]; then
  echo "check-pointers: root directory not found: $ROOT" >&2
  exit 2
fi
ROOT="$(cd "$ROOT" && pwd)"

FINDINGS_FILE="$(mktemp "${TMPDIR:-/tmp}/check-pointers-findings.XXXXXX")"
cleanup() { rm -f "$FINDINGS_FILE"; }
trap cleanup EXIT

# Escape ERE metacharacters in the (usually literal) rationale-dir path.
RATIONALE_DIR_ESC=$(printf '%s' "$RATIONALE_DIR" | sed -e 's/[.[\*^$()+?{|]/\\&/g')

# Pointer bodies are kebab-case: lowercase letters, digits, hyphens. Use
# POSIX classes rather than [a-z0-9] ranges so behavior does not depend on
# locale collation (see the locale digit-range trap noted in this repo's
# other test suites).
SLUG_CLASS='[[:lower:][:digit:]-]+'
POINTER_PATTERN="${RATIONALE_DIR_ESC}/[^ ]+\\.md#${SLUG_CLASS}"
FULL_PATTERN="(#|//|<!--)[[:space:]]*why:[[:space:]]*${POINTER_PATTERN}"

cd "$ROOT"

# ---------------------------------------------------------------------------
# Phase 1: find every "# why:" (or "// why:" / "<!-- why:" ) pointer under
# root and confirm it resolves to a real "### <slug>" heading.
# ---------------------------------------------------------------------------
while IFS= read -r matchline; do
  [ -n "$matchline" ] || continue
  file="${matchline%%:*}"
  rest="${matchline#*:}"
  lineno="${rest%%:*}"
  content="${rest#*:}"
  pointer=$(printf '%s\n' "$content" | grep -oE "$POINTER_PATTERN" | head -n1) || true
  [ -n "$pointer" ] || continue
  mdpath="${pointer%%#*}"
  slug="${pointer#*#}"
  relfile="${file#./}"
  if [ ! -f "$ROOT/$mdpath" ]; then
    printf '%s\t%s\t%s\t%s\n' "$relfile" "$lineno" "POINTER_BAD_FILE" "rationale file not found: $mdpath" >> "$FINDINGS_FILE"
    continue
  fi
  if ! grep -qxF "### $slug" "$ROOT/$mdpath"; then
    printf '%s\t%s\t%s\t%s\n' "$relfile" "$lineno" "POINTER_UNRESOLVED" "no '### $slug' heading in $mdpath" >> "$FINDINGS_FILE"
  fi
done < <(grep -rIn \
  --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj \
  -E "$FULL_PATTERN" . 2>/dev/null)

# ---------------------------------------------------------------------------
# Phase 2: validate every docs/rationale/*.md file itself — slug uniqueness,
# 2-6 line entry bodies, and a Refs: line per entry.
# ---------------------------------------------------------------------------
AWK_PROG='
{
  line = $0
  # Headings inside fenced code blocks are examples, not anchors; the lines
  # still count toward the enclosing entry body.
  if (line ~ /^```/) { infence = !infence; if (heading_line != "") bodylines++; next }
  if (infence) { if (heading_line != "") bodylines++; next }
  if (line ~ /^### /) {
    if (heading_line != "") { finalize() }
    slug = line
    sub(/^### /, "", slug)
    heading_line = NR
    heading_slug = slug
    count[slug]++
    if (count[slug] > 1) {
      printf "%d\tSLUG_DUPLICATE\tduplicate slug %s in this file\n", NR, slug
    }
    bodylines = 0
    refs = 0
    next
  }
  if (line ~ /^## /) {
    if (heading_line != "") { finalize() }
    heading_line = ""
    next
  }
  if (heading_line != "") {
    if (line ~ /^Refs:/) { refs = 1; next }
    if (line ~ /^[[:space:]]*$/) { next }
    bodylines++
  }
}
END {
  if (heading_line != "") { finalize() }
}
function finalize() {
  if (bodylines < 2)
    printf "%d\tENTRY_TOO_SHORT\tentry %s body has %d line(s), need 2-6\n", heading_line, heading_slug, bodylines
  else if (bodylines > 6)
    printf "%d\tENTRY_TOO_LONG\tentry %s body has %d line(s), need 2-6\n", heading_line, heading_slug, bodylines
  if (refs == 0)
    printf "%d\tENTRY_NO_REFS\tentry %s has no Refs: line\n", heading_line, heading_slug
}
'

if [ -d "$ROOT/$RATIONALE_DIR" ]; then
  for mdfile in "$ROOT/$RATIONALE_DIR"/*.md; do
    [ -e "$mdfile" ] || continue
    relmd="${mdfile#"$ROOT"/}"
    while IFS=$'\t' read -r lineno code message; do
      [ -n "$lineno" ] || continue
      printf '%s\t%s\t%s\t%s\n' "$relmd" "$lineno" "$code" "$message" >> "$FINDINGS_FILE"
    done < <(awk "$AWK_PROG" "$mdfile")
  done
fi

# ---------------------------------------------------------------------------
# Report.
# ---------------------------------------------------------------------------
count_findings=$(wc -l < "$FINDINGS_FILE" | tr -d '[:space:]')

if [ "$FORMAT" = "json" ]; then
  if [ "$count_findings" -eq 0 ]; then
    echo "[]"
  else
    sort -t "$(printf '\t')" -k1,1 -k2,2n "$FINDINGS_FILE" | \
      while IFS=$'\t' read -r f l c m; do
        jq -cn --arg file "$f" --arg line "$l" --arg code "$c" --arg message "$m" \
          '{file: $file, line: ($line | tonumber), code: $code, message: $message}'
      done | jq -cs '.'
  fi
else
  sort -t "$(printf '\t')" -k1,1 -k2,2n "$FINDINGS_FILE" | \
    while IFS=$'\t' read -r f l c m; do
      printf '%s:%s: %s %s\n' "$f" "$l" "$c" "$m"
    done
fi

echo "check-pointers: $count_findings findings" >&2

if [ "$count_findings" -gt 0 ]; then
  exit 1
fi
exit 0
