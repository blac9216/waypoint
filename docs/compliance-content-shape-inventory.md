# Vendor-content parser shape inventory

Status: **guard for issue [#1077](https://github.com/blac9216/waypoint/issues/1077)**'s
fixture-shape-blindness defect class -- three instances landed in one session (issues
#1073, #1071, and #1071's own fix) because a parser was validated only against the
input shapes its author already had in mind. This document is the **authority** for
the structural shapes each vendor-content parser is known to need to handle. It is not
descriptive of test code; test code is checked against it.

Each section below is parsed directly out of this file by a matching
`*ShapeInventoryTests` class (`backend/Waypoint.Tests/Core/ComplianceContent/...`),
which:

1. Builds an **invented** minimal fixture for every row (never real vendor/DISA
   content) and asserts the parser produces the row's documented expected result.
2. Asserts the reverse direction too: every shape ID the test fixtures implement has a
   documented row here. A row added here with no fixture, or a fixture added with no
   row here, fails the build -- the two cannot silently drift apart the way the
   layout table and `VendorHierarchyInterpreter` did before issue #959 (see
   `docs/compliance-parity.md`'s "Recognized on-disk import layouts", the pattern this
   document generalizes).

This inventory only grows when a new shape is discovered (typically via the opt-in
real-content conformance check below) and added here first, with its fixture and
assertion added alongside in the same change.

## What this guard does and does not cover

Read this before trusting the sections below to tell you a parser is safe. The
guard is strong **over the shapes enumerated in this file** and has no reach at all
outside them. Stated precisely, and verified by PR #1098's round-1 review defeating
the guard twice:

**Machine-enforced (the build fails):**

- **Doc row <-> fixture, both directions.** `ShapeInventoryDoc.AssertCompleteness`
  compares the rows parsed out of this file against each `*ShapeInventoryTests`
  class's `ImplementedShapeIds`. A row with no fixture fails; a fixture with no row
  fails.
- **Per-shape behaviour.** Every row has an invented fixture asserting the row's
  documented Expected result -- for a reject row, a specific error substring, so a
  malformed fixture cannot masquerade as a shape rejection.
- **Regression on an enumerated shape.** `scripts/parser-shape-diff.sh` fails when a
  documented-accept shape stops resolving between two refs, and when a
  documented-reject shape starts being accepted (a dropped zip-slip check, recursion
  bound, or XCCDF requirement).
- **The Expected column is machine-parsed, and its vocabulary is enforced.** The
  script tells those two cases apart by reading the **leading word of each row's
  Expected cell**, normalized (leading whitespace and markdown emphasis/backticks
  stripped, case-folded) and required to be `Accepted` or `Rejected`; everything after
  that word is prose for humans. The wording is not left to chance: the completeness
  assertion (`ShapeInventoryDoc.AssertExpectedVocabulary`, run from
  `AssertCompleteness`) fails the build on any row whose Expected cell does not open
  with one of those two words -- so a reworded cell cannot silently reach the script.
  If one ever does, the script **fails closed**: a rejected-to-accepted flip it cannot
  classify is reported as `UNVERIFIABLE` with exit 1, the same as a flip with no row
  at all. Both the missing-row and the unrecognized-wording paths fail in the safe
  direction. (PR #1098's round-2 review defeated the earlier version of this by
  bolding the word in a reject row, which downgraded a dropped zip-slip check to a
  soft note with exit 0.)
- **The Expected verdict word is bound to what the fixture actually asserts.**
  Enforcing the vocabulary (above) still let a cell say `Accepted` for a shape whose
  fixture asserts rejection -- a doc-only edit that silently disarmed the differential
  for that shape while the suite stayed green at 27/27 (issue #1121). Each
  `*ShapeInventoryTests` class now declares a single table of per-shape expectations
  (`StigZipReaderShapeInventoryTests.ShapeExpectations`; `InspecManifestShapeInventoryTests.ShapeExpectations`,
  whose rows are accept-flavoured except `tab-block-indentation`, its one reject case since
  issue #1103) and derives every shape list it uses from that one table: the
  shape IDs it implements, the reject set handed to
  `ShapeInventoryDoc.AssertCompleteness`, and the `[MemberData]` rows of its
  accept/reject theories. `AssertCompleteness` cross-checks every
  row's classified verdict against that reject set: a row saying `Accepted` for a shape
  whose fixture rejects it, or `Rejected` for one whose fixture accepts it, fails the
  build. Because the set and the theory rows have one source, the set cannot lie about
  the fixtures either -- dropping a shape from the reject set does not leave its
  rejection case standing, it moves that shape into `ShapeIsAccepted`, which then fails
  against a parser that really does reject it, and deleting the shape outright fails the
  completeness assertion. This closes the specific channel PR #1098's own merged fix
  left open, and moves "the verdict word agrees with the fixture" from review-enforced
  to machine-enforced.
- **The C# and script column splits cannot drift.** Previously the C# reader walked a
  row back to its last *unescaped* pipe while `scripts/parser-shape-diff.sh` split on
  every `|`, so a self-contradictory cell containing a literal escaped pipe --
  `Rejected with an unsafe-path error \| Accepted only for entries already
  normalized.` -- classified as rejected in the test suite but as accepted in the
  script, printing `NOTE`/exit 0 for a dropped zip-slip check (issue #1120). The script
  no longer parses the table itself: `ShapeVerdictDump` now also dumps
  `ShapeInventoryDoc.ClassifyShapes`'s per-shape verdicts (the same code
  `AssertExpectedVocabulary`/`AssertCompleteness` already assert against) to JSON when
  `WAYPOINT_SHAPE_EXPECTED_DUMP_PATH` is set, and the script reads that JSON instead of
  re-parsing markdown. One classification, read by both checks, replaces two
  implementations that happened to agree. That single split
  (`ShapeInventoryDoc.LastColumn`) has its own direct coverage in
  `ShapeInventoryDocColumnSplitTests`, because no row of the tables below carries a
  `\|` in its *Expected* cell -- the only escaped pipe in the inventory sits in
  `block-scalar-literal-description`'s *Scenario* cell, which is not the last pipe on
  its line and so cannot tell the escape-aware walk-back from a naive last-pipe search.
  Those tests assert the walk-back over synthetic row remainders -- including the
  self-contradictory `Rejected ... \| Accepted ...` cell above -- so the escape
  handling cannot be deleted with the suite green, and the coverage does not depend on
  the wording of any row.

**NOT machine-enforced (review-enforced only -- this is where you must add a row by
hand):**

- **Parser branches are not bound to rows.** The completeness assertion binds this
  document to the fixture set, *not* to the parser's code. A new parser branch --
  a new accepted key, a new archive layout, a new fallback -- can be added with no
  row and no fixture and the suite stays green. Issue #1077's framing ("a parser that
  gains a branch without a corresponding inventory row ... fails the build") is the
  intent, not what is implemented; static analysis of parser branches was judged not
  reasonably implementable. **If you touch a parser listed here, add the row
  yourself.**
- **Unenumerated shapes are invisible to every guard here.** A shape that is not a row
  is not in the corpus, so the per-shape assertions never exercise it and the
  differential -- which diffs the corpus against itself -- can never report it as a
  regression. If no shipped content happens to use it either, the real-content check
  is blind to it as well. PR #1098's review demonstrated exactly this: dropping input
  names carried by a *quoted* YAML scalar passed all three guards (the targeted
  suite green at 27/27 -- the per-shape theories plus the completeness, real-content
  and dump facts -- `385/385 real manifests accepted, 0 rejected`, and
  `No regressions`, exit 0). Growing the inventory is a deliberate human act; the machinery only keeps
  it from shrinking.

The practical rule: this file is worth exactly what the last engineer put into it.
Discovering new shapes remains the job of pointing the real-content check at fresh
upstream content, and of review reading the parser diff against this table.

## Real-content conformance and differential checks

Two guards live alongside the per-shape assertions above, for the reasons the PR #1084
round-2 review recorded on issue #1077: a real-content conformance check alone would
**not** have caught a fix that silently stopped resolving a shape no shipped manifest
happens to use today.

- **Opt-in real-content conformance** (`RealContentConformanceTests`): walks a locally
  cloned vendor content repository at `/workspaces/git/dod-compliance-and-automation`
  (read-only) and reports, per parser, how many real artifacts it accepts versus
  rejects. Skips cleanly (no assertion, no failure) when that path does not exist, so
  CI never depends on vendor content.
- **Differential harness** (`scripts/parser-shape-diff.sh`): runs the *same* shape
  corpus defined below through an old ref's parser code and the working tree's parser
  code, and fails on any shape that resolved under the old ref but no longer does
  under the new one, or that the table below documents as **rejected** and the new ref
  now accepts (a dropped zip-slip check, recursion bound, or XCCDF requirement). This
  is the check that catches a fix silently losing an **enumerated** shape that no
  current real content happens to exercise -- conformance against today's content
  cannot see that class of regression at all.

  Its reach stops at the table. The corpus it diffs *is* this inventory, so a shape
  nobody wrote down is absent from both sides of the diff and can never be reported
  as a regression; see "What this guard does and does not cover" above.

## `InspecManifestParser` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/InspecManifest.cs`)

Shapes of the `inputs:` (and legacy `attributes:`) block of an `inspec.yml` manifest.
Every row here exists because a sibling parser (`Get-WaypointNsxProfileAuthInputKeySet`
in `WaypointScan.psm1`, issue #1071) missed one of these shapes in real content, or
because fixing it (PR #1084) silently broke another one.

| Shape ID | Description | Expected |
|---|---|---|
| `indented-dash-sequence` | `inputs:` entries as an indented `  - name: ...` sequence (the shape every existing test used before this inventory). | Accepted; resolves the input by name. |
| `column0-dash-sequence` | `inputs:` entries as a column-0 `- name: ...` sequence (no indentation before the dash) -- the shape every shipped NSX manifest actually used per issue #1071. | Accepted; resolves the input by name. |
| `name-not-first-key` | An entry mapping lists `description:` before `name:` -- the shape PR #1084's first fix commit silently stopped resolving (issue #1077's motivating regression). | Accepted; resolves the input by name regardless of key order. |
| `attributes-legacy-alias` | Manifest uses the legacy `attributes:` key instead of `inputs:`. | Accepted; resolves via the alias exactly as `inputs:` would. |
| `column0-comment-between-entries` | A `#`-prefixed comment at column 0 between two input entries. | Accepted; both entries resolve and the comment has no effect. |
| `trailing-inline-comment` | An entry's `name:` value carries a trailing inline `# comment`. | Accepted; resolves, and the input name excludes the comment text. |
| `block-scalar-folded-description` | An entry's `description:` uses a folded block scalar (`>`) spanning multiple lines. | Accepted; parses without error and the input still resolves by name. |
| `block-scalar-literal-description` | An entry's `description:` uses a literal block scalar (`\|`) spanning multiple lines. | Accepted; parses without error and the input still resolves by name. |
| `nested-extra-keys-ignored` | An entry carries extra nested keys (`sensitive:`, a nested `value:` mapping) beyond `name`/`type`/`required`. | Accepted; resolves name/type/required, and extra keys are ignored rather than errors. |
| `empty-inputs-sequence` | `inputs: []`. | Accepted; resolves to zero inputs, not an error. |
| `missing-inputs-key` | No `inputs:` or `attributes:` key present at all. | Accepted; resolves to zero inputs, not an error. |
| `crlf-line-endings` | The whole document (indented-dash-sequence shape) uses CRLF line endings throughout. | Accepted; resolves identically to its LF counterpart -- CRLF is a known untested gap (PR #1084 finding: `WriteProfileFixtureRaw` fixtures inherit LF). |
| `document-start-end-markers` | The document opens with a `---` marker and closes with a `...` marker. | Accepted; resolves the input exactly as the unmarked document does. |
| `multi-document-stream` | A `---`-separated multi-document YAML stream; the manifest is the first document, an unrelated second document follows. | Accepted; resolves from the first document only -- the parser reads `stream.Documents[0]` and never looks past it. |
| `tab-block-indentation` | A raw tab character used for the `inputs:` sequence's block indentation. | Rejected as not valid YAML -- a raw tab cannot start a token in block context outside a quoted scalar or comment (YAML core schema), so this is genuinely invalid input, not a parser gap. |
| `tab-in-trailing-comment` | A raw tab character inside an entry's trailing `# comment`, not used for indentation. | Accepted; resolves the input, and the tab inside the comment has no effect. |
| `nested-name-under-value-mapping` | An entry carries a nested `value:` **mapping** that itself has a `name:` key (issue #1103: the shape a "first `name:` wins" scan would trip on). | Accepted; resolves the entry's own top-level `name:`, not the nested one. |
| `nested-name-under-value-sequence` | An entry carries a nested `value:` **sequence** whose first item is a mapping with a `name:` key. | Accepted; resolves the entry's own top-level `name:`, not the nested one. |
| `inputs-depends-adjacency` | An `inputs:` block is immediately followed by a `depends:` block at the same indent, with no blank line between them -- the block-scoping boundary case that produced the original defect in the sibling helper (issue #1071). | Accepted; the input entry resolves and is unaffected by the adjacent `depends:` block. |
| `quoted-scalar-name-double` | An entry's `name:` value is a double-quoted scalar (`name: "nsx_manager_address"`). | Accepted; resolves the input by its unquoted name -- the shape PR #1098's round-1 review used to defeat this guard before this row existed. |
| `quoted-scalar-name-single` | An entry's `name:` value is a single-quoted scalar (`name: 'nsx_manager_address'`). | Accepted; resolves the input by its unquoted name. |

## `StigZipReader` (`backend/Waypoint.Core/ComplianceContent/Xccdf/StigZipReader.cs`)

Shapes of an uploaded/synchronized DISA STIG `.zip` package (issue #1073).

| Shape ID | Description | Expected |
|---|---|---|
| `single-benchmark` | One entry ending `-xccdf.xml`. | Accepted; exactly one benchmark. |
| `flat-multi-xccdf` | Multiple sibling `-xccdf.xml` entries alongside a non-XCCDF file. | Accepted; one benchmark per XCCDF entry. |
| `nested-directory-multi-xccdf` | One XCCDF per component subdirectory under a `*_Supplemental/` tree. | Accepted; one benchmark per component directory. |
| `zip-of-zips` | Top-level entries are themselves component STIG zips, each containing their own XCCDF. | Accepted; one benchmark per nested XCCDF, recursing one level. |
| `zip-of-zips-depth-boundary` | Zip-of-zips nesting exactly at `MaxRecursionDepth`. | Accepted. |
| `zip-of-zips-beyond-depth-boundary` | Zip-of-zips nesting one level past `MaxRecursionDepth`. | Rejected with a recursion-bound error. |
| `case-insensitive-xccdf-suffix` | An entry name ends `-XCCDF.XML` (uppercase). | Accepted; matched case-insensitively. |
| `non-xccdf-siblings-ignored` | A mix of PDF/readme/audit-rules files alongside one XCCDF entry. | Accepted; only the XCCDF entry is counted. |
| `zip-slip-entry-name` | An entry name contains a `..` traversal segment. | Rejected with an unsafe-path error. |
| `no-xccdf-entry` | Archive has entries, but none end `-xccdf.xml`. | Rejected as containing no XCCDF entry. |

## Deferred parsers (tracked as issue [#1099](https://github.com/blac9216/waypoint/issues/1099))

The remainder of issue #1077's enumerated scope is filed as issue
[#1099](https://github.com/blac9216/waypoint/issues/1099). The following parsers are
in issue #1077's scope but not yet covered by this inventory: `Get-WaypointNsxProfileAuthInputKeySet` (`WaypointScan.psm1`),
`XccdfParser`, and `VendorHierarchyInterpreter` (beyond the layout-table parity guard
`docs/compliance-parity.md` already provides for its directory-literal dimension --
this inventory would add the leaf-manifest/encoding dimensions on top of that).
