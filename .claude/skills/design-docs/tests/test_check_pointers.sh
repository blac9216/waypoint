#!/usr/bin/env bash
# test_check_pointers.sh — regression test for check-pointers.sh, the
# design-docs skill's "why" pointer-integrity checker. Builds a fresh
# fixture repo per case under a private scratch dir under $TMPDIR (or
# /tmp), runs check-pointers.sh against it, and asserts the finding codes,
# exit status, and (for one case) that --format json emits parseable JSON.
# Self-contained: no network access, no repository content read.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHECK_POINTERS_SH="$SCRIPT_DIR/../scripts/check-pointers.sh"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/check-pointers-test.XXXXXX")"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

fail=0
report() { echo "FAIL: $*" >&2; fail=1; case_failed=1; }

# run_case NAME — runs case function NAME, tracking + printing PASS/FAIL for
# just that case (report() may fire multiple times per case; case_failed
# only needs to go from 0 to 1 once).
run_case() {
  local name="$1"
  case_failed=0
  "$name"
  if [ "$case_failed" -eq 0 ]; then
    echo "PASS: $name"
  else
    echo "FAIL: $name"
  fi
}

# make_fixture NAME — creates $WORK/fixtures/NAME as an empty repo dir with
# docs/rationale/ and src/ ready to populate, and echoes its path.
make_fixture() {
  local dir="$WORK/fixtures/$1"
  mkdir -p "$dir/docs/rationale" "$dir/src"
  printf '%s' "$dir"
}

# run_check FIXTURE_DIR [extra args...] — runs check-pointers.sh against a
# fixture, capturing stdout/stderr/exit code into globals for assertions.
run_check() {
  local dir="$1"; shift
  set +e
  CP_STDOUT=$("$CHECK_POINTERS_SH" --root "$dir" "$@" 2>"$WORK/stderr.log")
  CP_RC=$?
  set -e
  CP_STDERR="$(cat "$WORK/stderr.log")"
}

# ---------------------------------------------------------------------------
# Case: clean fixture passes (exit 0, no findings).
# ---------------------------------------------------------------------------
case_clean() {
  local dir; dir="$(make_fixture clean)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

This is line one of the body.
This is line two of the body.

Refs: #1
EOF
  cat > "$dir/src/compose.yaml" <<'EOF'
foo:
  # why: docs/rationale/deploy.md#compose-foo-bar
  bar: baz
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 0 ] || report "clean: expected exit 0, got $CP_RC (stdout: $CP_STDOUT)"
  [ -z "$CP_STDOUT" ] || report "clean: expected no findings on stdout, got: $CP_STDOUT"
  grep -qF "check-pointers: 0 findings" <<<"$CP_STDERR" || report "clean: expected summary '0 findings' on stderr, got: $CP_STDERR"
}

# ---------------------------------------------------------------------------
# Case: pointer references a slug that does not exist in the target file.
# ---------------------------------------------------------------------------
case_unresolved_slug() {
  local dir; dir="$(make_fixture unresolved)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

This is line one of the body.
This is line two of the body.

Refs: #1
EOF
  cat > "$dir/src/compose.yaml" <<'EOF'
foo:
  # why: docs/rationale/deploy.md#compose-does-not-exist
  bar: baz
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "unresolved_slug: expected exit 1, got $CP_RC"
  grep -qF "POINTER_UNRESOLVED" <<<"$CP_STDOUT" || report "unresolved_slug: expected POINTER_UNRESOLVED finding, got: $CP_STDOUT"
  grep -qF "src/compose.yaml:2:" <<<"$CP_STDOUT" || report "unresolved_slug: expected finding on src/compose.yaml:2, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: pointer references a rationale file that does not exist on disk.
# ---------------------------------------------------------------------------
case_missing_file() {
  local dir; dir="$(make_fixture missingfile)"
  cat > "$dir/src/compose.yaml" <<'EOF'
foo:
  # why: docs/rationale/nope.md#some-slug
  bar: baz
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "missing_file: expected exit 1, got $CP_RC"
  grep -qF "POINTER_BAD_FILE" <<<"$CP_STDOUT" || report "missing_file: expected POINTER_BAD_FILE finding, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: duplicate ### slug within one rationale file.
# ---------------------------------------------------------------------------
case_duplicate_slug() {
  local dir; dir="$(make_fixture duplicate)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

This is line one of the body.
This is line two of the body.

Refs: #1

### compose-foo-bar

Different entry, same slug text — an anchor collision.
Second line of body here.

Refs: #2
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "duplicate_slug: expected exit 1, got $CP_RC"
  grep -qF "SLUG_DUPLICATE" <<<"$CP_STDOUT" || report "duplicate_slug: expected SLUG_DUPLICATE finding, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: entry body too short (1 line).
# ---------------------------------------------------------------------------
case_body_too_short() {
  local dir; dir="$(make_fixture short)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

Only one line of body.

Refs: #1
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "body_too_short: expected exit 1, got $CP_RC"
  grep -qF "ENTRY_TOO_SHORT" <<<"$CP_STDOUT" || report "body_too_short: expected ENTRY_TOO_SHORT finding, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: entry body too long (7 lines).
# ---------------------------------------------------------------------------
case_body_too_long() {
  local dir; dir="$(make_fixture long)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

Line one of a much too long body.
Line two of a much too long body.
Line three of a much too long body.
Line four of a much too long body.
Line five of a much too long body.
Line six of a much too long body.
Line seven of a much too long body.

Refs: #1
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "body_too_long: expected exit 1, got $CP_RC"
  grep -qF "ENTRY_TOO_LONG" <<<"$CP_STDOUT" || report "body_too_long: expected ENTRY_TOO_LONG finding, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: entry missing its Refs: line.
# ---------------------------------------------------------------------------
case_missing_refs() {
  local dir; dir="$(make_fixture norefs)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

This is line one of the body.
This is line two of the body.
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "missing_refs: expected exit 1, got $CP_RC"
  grep -qF "ENTRY_NO_REFS" <<<"$CP_STDOUT" || report "missing_refs: expected ENTRY_NO_REFS finding, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: --format json emits an array that parses with python3's json module.
# ---------------------------------------------------------------------------
case_json_output() {
  local dir; dir="$(make_fixture json)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## compose.yaml

### compose-foo-bar

This is line one of the body.
This is line two of the body.

Refs: #1
EOF
  cat > "$dir/src/compose.yaml" <<'EOF'
foo:
  # why: docs/rationale/deploy.md#compose-does-not-exist
  bar: baz
EOF
  run_check "$dir" --format json
  [ "$CP_RC" -eq 1 ] || report "json_output: expected exit 1, got $CP_RC"
  if ! python3 -c 'import json,sys;json.load(sys.stdin)' <<<"$CP_STDOUT"; then
    report "json_output: stdout did not parse as JSON: $CP_STDOUT"
  fi
  grep -qF '"code":"POINTER_UNRESOLVED"' <<<"$CP_STDOUT" || report "json_output: expected POINTER_UNRESOLVED in JSON output, got: $CP_STDOUT"
}

# ---------------------------------------------------------------------------
# Case: "// why:" prefix (not just "# why:") is recognized.
case_fenced_heading() {
  local dir; dir="$(make_fixture fence)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## Format

Example entry:

```markdown
### example-slug

Some example why line one.
Some example why line two.

Refs: #000
```

## compose.yaml

### compose-real-entry

Reason line one.
Reason line two.

Refs: #1
EOF
  printf '# why: docs/rationale/deploy.md#compose-real-entry\n' > "$dir/src/a.sh"
  run_check "$dir"
  [ "$CP_RC" -eq 0 ] || report "fenced_heading: expected exit 0, got $CP_RC (stdout: $CP_STDOUT)"
}

case_slash_prefix() {
  local dir; dir="$(make_fixture slashprefix)"
  cat > "$dir/docs/rationale/deploy.md" <<'EOF'
## app.js

### app-retry-backoff

This is line one of the body.
This is line two of the body.

Refs: #1
EOF
  cat > "$dir/src/app.js" <<'EOF'
function retry() {
  // why: docs/rationale/deploy.md#app-retry-backoff
  return 1;
}
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 0 ] || report "slash_prefix: expected exit 0, got $CP_RC (stdout: $CP_STDOUT)"
  [ -z "$CP_STDOUT" ] || report "slash_prefix: expected no findings, got: $CP_STDOUT"

  # Same fixture, but point the "// why:" comment at a nonexistent slug, to
  # confirm the prefix is actually being scanned (not silently ignored).
  cat > "$dir/src/app.js" <<'EOF'
function retry() {
  // why: docs/rationale/deploy.md#app-does-not-exist
  return 1;
}
EOF
  run_check "$dir"
  [ "$CP_RC" -eq 1 ] || report "slash_prefix(negative): expected exit 1, got $CP_RC"
  grep -qF "POINTER_UNRESOLVED" <<<"$CP_STDOUT" || report "slash_prefix(negative): expected POINTER_UNRESOLVED, got: $CP_STDOUT"
}

run_case case_clean
run_case case_unresolved_slug
run_case case_missing_file
run_case case_duplicate_slug
run_case case_body_too_short
run_case case_body_too_long
run_case case_missing_refs
run_case case_json_output
run_case case_slash_prefix
run_case case_fenced_heading

if [ "$fail" -ne 0 ]; then
  echo "test_check_pointers: FAILED" >&2
  exit 1
fi

echo "test_check_pointers: PASS (9 cases)"
exit 0
