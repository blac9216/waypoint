#!/usr/bin/env bash
# Issue #1077 differential harness: runs the SAME invented shape corpus
# (backend/Waypoint.Tests/Core/ComplianceContent/{SemanticImport,Xccdf}/*ShapeInventoryTests.cs)
# through an OLD git ref's parser code and the current working tree's parser code, and
# reports any shape that resolved under the old ref but no longer resolves under the
# new one. This is a DIFFERENT property from real-content conformance (see
# docs/compliance-content-shape-inventory.md): conformance proves the parser handles
# what vendors ship today; this proves a candidate change did not silently stop
# handling a shape THAT IS ALREADY AN INVENTORY ROW, including rows no shipped content
# happens to exercise yet (the PR #1084 round-2 review's finding on issue #1077).
#
# SCOPE LIMIT (PR #1098 round-1 review): the corpus this script diffs IS the inventory.
# A shape nobody wrote down is invisible to it -- old and new both simply lack the row,
# so no regression can be reported. Discovering unenumerated shapes is the job of the
# opt-in real-content check against new upstream content, and of review. See the doc's
# "What this guard does and does not cover" section.
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
# Exit code is non-zero iff at least one shape regressed. Two transitions count as a
# regression:
#   * a shape the inventory documents as ACCEPTED that resolved under old-ref and no
#     longer resolves under new-ref (a silently-dropped shape); and
#   * a shape the inventory documents as REJECTED that is now accepted (a dropped
#     protection -- zip-slip, the recursion bound, the XCCDF requirement).
# A documented-ACCEPT shape that was rejected under old-ref and resolves under new-ref
# is the intentional-fix direction and is reported as a NOTE, not a failure.
#
# The accept/reject verdict comes from the leading word of the inventory row's Expected
# column. A flip whose row is missing, or whose Expected cell does not start with a
# recognizable "Accepted"/"Rejected", is UNVERIFIABLE and also exits non-zero -- this
# classification fails closed in both directions.
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
	local ref="$1" label="$2" out_json="$3" out_expected_json="${4:-}"
	local wt_dir="$SCRATCH_ROOT/wt-$label"

	git -C "$REPO_ROOT" worktree add --detach "$wt_dir" "$ref" >/dev/null

	if [[ ! -f "$wt_dir/backend/Waypoint.Tests/Core/ComplianceContent/ShapeInventory/ShapeVerdictDump.cs" ]]; then
		echo "error: ref '$ref' does not contain the shape-inventory differential harness (ShapeVerdictDump.cs) -- this script can only diff refs at or after issue #1077's guard landed." >&2
		exit 3
	fi

	if [[ -n "$out_expected_json" ]]; then
		WAYPOINT_SHAPE_DUMP_PATH="$out_json" WAYPOINT_SHAPE_EXPECTED_DUMP_PATH="$out_expected_json" \
			dotnet test "$wt_dir/backend/Waypoint.Tests/Waypoint.Tests.csproj" \
			--filter "FullyQualifiedName~ShapeVerdictDump" \
			--nologo -v quiet >&2
	else
		WAYPOINT_SHAPE_DUMP_PATH="$out_json" dotnet test "$wt_dir/backend/Waypoint.Tests/Waypoint.Tests.csproj" \
			--filter "FullyQualifiedName~ShapeVerdictDump" \
			--nologo -v quiet >&2
	fi

	if [[ ! -f "$out_json" ]]; then
		echo "error: dump did not produce $out_json for ref '$ref'" >&2
		exit 3
	fi

	if [[ -n "$out_expected_json" && ! -f "$out_expected_json" ]]; then
		echo "error: dump did not produce $out_expected_json for ref '$ref' -- this script can only diff refs at or after issue #1120's shared classification dump landed." >&2
		exit 3
	fi
}

OLD_JSON="$SCRATCH_ROOT/old.json"
NEW_JSON="$SCRATCH_ROOT/new.json"
NEW_EXPECTED_JSON="$SCRATCH_ROOT/new-expected.json"

echo "== dumping verdicts for old ref: $OLD_REF ==" >&2
dump_ref "$OLD_REF" old "$OLD_JSON"

if [[ -n "$NEW_REF" ]]; then
	echo "== dumping verdicts for new ref: $NEW_REF ==" >&2
	dump_ref "$NEW_REF" new "$NEW_JSON" "$NEW_EXPECTED_JSON"
else
	echo "== dumping verdicts for working tree ==" >&2
	WAYPOINT_SHAPE_DUMP_PATH="$NEW_JSON" WAYPOINT_SHAPE_EXPECTED_DUMP_PATH="$NEW_EXPECTED_JSON" \
		dotnet test "$REPO_ROOT/backend/Waypoint.Tests/Waypoint.Tests.csproj" \
		--filter "FullyQualifiedName~ShapeVerdictDump" \
		--nologo -v quiet >&2
fi

if [[ ! -f "$NEW_EXPECTED_JSON" ]]; then
	echo "error: shape classification dump not found at $NEW_EXPECTED_JSON -- the differential needs it to tell a documented-accept shape from a documented-reject one." >&2
	exit 3
fi

python3 - "$OLD_JSON" "$NEW_JSON" "$NEW_EXPECTED_JSON" <<'PYEOF'
import json
import sys

old = json.load(open(sys.argv[1]))
new = json.load(open(sys.argv[2]))

# The inventory doc's "Expected" column is the authority on whether a shape is
# supposed to be ACCEPTED or REJECTED. A documented-reject shape turning into an
# accepted one means a protection (zip-slip, the recursion bound, the XCCDF
# requirement) was dropped -- that is a regression, not a note.
#
# `expectation` is produced by ShapeInventoryDoc.ClassifyShapes (via ShapeVerdictDump,
# WAYPOINT_SHAPE_EXPECTED_DUMP_PATH) -- the SAME code that splits each row into columns
# and classifies its Expected cell for ShapeInventoryDoc.AssertExpectedVocabulary /
# AssertCompleteness. This script no longer re-parses the markdown table with an
# independent implementation: issue #1120 found the two readers split a row's columns
# differently (the C# walked back to the last UNESCAPED pipe; this script treated every
# `|` as a delimiter), so a cell engineered to contain a literal escaped pipe --
# `Rejected ... \| Accepted ...` -- classified as REJECTED in the test suite but as
# ACCEPTED here, printing NOTE/exit 0 for a dropped protection. Reading the classification
# from one shared, already-tested definition instead of a second parser removes that
# class of drift entirely, rather than just patching this script's regex to match.
#
# Classification FAILS CLOSED (PR #1098 round-2 review, preserved by ClassifyShapes):
# a cell whose leading word does not normalize to "accepted"/"rejected" yields `null`
# here, and an unclassified rejected->accepted flip is reported as UNVERIFIABLE with
# exit 1, exactly as a missing row is.
expectation = json.load(open(sys.argv[3]))

regressions = []
dropped_protections = []
unclassified = []
for shape_id, old_resolved in old.items():
    new_resolved = new.get(shape_id)
    if new_resolved is None:
        print(f"NOTE  {shape_id}: present in old dump, absent from new dump (shape removed)")
        continue
    if old_resolved and not new_resolved:
        regressions.append(shape_id)
    elif old_resolved != new_resolved:
        # old=False, new=True: a shape that was rejected is now accepted.
        documented = expectation.get(shape_id)
        if documented == "reject":
            dropped_protections.append(shape_id)
        elif documented == "accept":
            print(f"NOTE  {shape_id}: rejected under the old ref, accepted under the new one; the inventory documents this shape as accepted, so this is the intentional-fix direction -- verify it is deliberate.")
        else:
            unclassified.append(shape_id)

for shape_id in sorted(set(new) - set(old)):
    print(f"NOTE  {shape_id}: new shape, not present in old dump")

failed = False

if regressions:
    print()
    print(f"REGRESSION: {len(regressions)} shape(s) resolved under the old ref and no longer resolve under the new one:")
    for shape_id in sorted(regressions):
        print(f"  - {shape_id}")
    failed = True

if dropped_protections:
    print()
    print(f"DROPPED PROTECTION: {len(dropped_protections)} shape(s) the inventory documents as REJECTED are now accepted under the new ref:")
    for shape_id in sorted(dropped_protections):
        print(f"  - {shape_id}")
    print("A documented-reject shape becoming accepted means the guard behind it (zip-slip path check, recursion bound, XCCDF requirement) no longer fires. If the rejection was genuinely wrong, change the inventory row's Expected column in the same commit.")
    failed = True

if unclassified:
    print()
    print(f"UNVERIFIABLE: {len(unclassified)} shape(s) flipped from rejected to accepted but have no inventory row whose Expected column starts with a recognized 'Accepted'/'Rejected' verdict:")
    for shape_id in sorted(unclassified):
        print(f"  - {shape_id}")
    print("Without a classifiable Expected cell this script cannot tell a dropped protection from an intentional fix, so it fails closed. Add the row, or restore its Expected column to begin with 'Accepted' or 'Rejected'.")
    failed = True

if failed:
    sys.exit(1)

print("No regressions: every shape that resolved under the old ref still resolves under the new ref, and no documented-reject shape became accepted.")
PYEOF
