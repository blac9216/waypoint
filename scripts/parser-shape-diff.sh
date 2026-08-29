#!/usr/bin/env bash
# Issue #1077 differential harness: runs the SAME invented shape corpus
# (backend/Waypoint.Tests/Core/ComplianceContent/{SemanticImport,Xccdf}/*ShapeInventoryTests.cs)
# through an OLD git ref's parser code and the current working tree's parser code, and
# reports any shape that resolved under the old ref but no longer resolves under the
# new one. This is a DIFFERENT property from real-content conformance (see
# docs/compliance-content-shape-inventory.md): conformance proves the parser handles
# what vendors ship today; this proves a candidate change did not silently stop
# handling a shape no shipped content happens to exercise yet (the PR #1084 round-2
# review's finding on issue #1077).
#
# Usage:
#   scripts/parser-shape-diff.sh <old-ref> [new-ref]
#
#   <old-ref>   Any git ref (branch, tag, sha) to compare against. Typically
#               origin/main. Must contain this differential harness itself
#               (ShapeVerdictDump.cs, *ShapeInventoryTests.cs) -- true for any ref at
#               or after this script's own commit.
#   [new-ref]   Optional git ref for the "new" side. Defaults to the working tree
#               (uninstalled/uncommitted changes included), which is the normal case
#               of diffing a candidate branch against main.
#
# Exit code is non-zero iff at least one shape regressed (resolved under old-ref, not
# resolved under new-ref).
set -euo pipefail

if [[ $# -lt 1 ]]; then
	echo "usage: $0 <old-ref> [new-ref]" >&2
	exit 2
fi

OLD_REF="$1"
NEW_REF="${2:-}"

REPO_ROOT="$(git rev-parse --show-toplevel)"
SCRATCH_ROOT="$(mktemp -d)"
trap 'rm -rf "$SCRATCH_ROOT"; git -C "$REPO_ROOT" worktree prune >/dev/null 2>&1 || true' EXIT

export PATH="$HOME/.dotnet:$PATH"

dump_ref() {
	local ref="$1" label="$2" out_json="$3"
	local wt_dir="$SCRATCH_ROOT/wt-$label"

	git -C "$REPO_ROOT" worktree add --detach "$wt_dir" "$ref" >/dev/null

	if [[ ! -f "$wt_dir/backend/Waypoint.Tests/Core/ComplianceContent/ShapeInventory/ShapeVerdictDump.cs" ]]; then
		echo "error: ref '$ref' does not contain the shape-inventory differential harness (ShapeVerdictDump.cs) -- this script can only diff refs at or after issue #1077's guard landed." >&2
		exit 3
	fi

	WAYPOINT_SHAPE_DUMP_PATH="$out_json" dotnet test "$wt_dir/backend/Waypoint.Tests/Waypoint.Tests.csproj" \
		--filter "FullyQualifiedName~ShapeVerdictDump" \
		--nologo -v quiet >&2

	if [[ ! -f "$out_json" ]]; then
		echo "error: dump did not produce $out_json for ref '$ref'" >&2
		exit 3
	fi
}

OLD_JSON="$SCRATCH_ROOT/old.json"
NEW_JSON="$SCRATCH_ROOT/new.json"

echo "== dumping verdicts for old ref: $OLD_REF ==" >&2
dump_ref "$OLD_REF" old "$OLD_JSON"

if [[ -n "$NEW_REF" ]]; then
	echo "== dumping verdicts for new ref: $NEW_REF ==" >&2
	dump_ref "$NEW_REF" new "$NEW_JSON"
else
	echo "== dumping verdicts for working tree ==" >&2
	WAYPOINT_SHAPE_DUMP_PATH="$NEW_JSON" dotnet test "$REPO_ROOT/backend/Waypoint.Tests/Waypoint.Tests.csproj" \
		--filter "FullyQualifiedName~ShapeVerdictDump" \
		--nologo -v quiet >&2
fi

python3 - "$OLD_JSON" "$NEW_JSON" <<'PYEOF'
import json
import sys

old = json.load(open(sys.argv[1]))
new = json.load(open(sys.argv[2]))

regressions = []
for shape_id, old_resolved in old.items():
	new_resolved = new.get(shape_id)
	if new_resolved is None:
		print(f"NOTE  {shape_id}: present in old dump, absent from new dump (shape removed)")
		continue
	if old_resolved and not new_resolved:
		regressions.append(shape_id)
	elif old_resolved != new_resolved:
		print(f"NOTE  {shape_id}: old={old_resolved} new={new_resolved} (intentional false-positive removal -- verify, not a regression by itself)")

for shape_id in sorted(set(new) - set(old)):
	print(f"NOTE  {shape_id}: new shape, not present in old dump")

if regressions:
	print()
	print(f"REGRESSION: {len(regressions)} shape(s) resolved under the old ref and no longer resolve under the new one:")
	for shape_id in sorted(regressions):
		print(f"  - {shape_id}")
	sys.exit(1)

print("No regressions: every shape that resolved under the old ref still resolves under the new ref.")
PYEOF
