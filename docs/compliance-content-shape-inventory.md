# Vendor-content parser shape inventory

Status: **guard for issue [#1077](https://github.com/blac9216/waypoint/issues/1077)**'s
fixture-shape-blindness defect class -- three instances landed in one session (issues
#1073, #1071, and #1071's own fix) because a parser was validated only against the
input shapes its author already had in mind. This document is the **authority** for
the structural shapes each vendor-content parser is known to need to handle. It is not
descriptive of test code; test code is checked against it.

Each C# parser's section below is parsed directly out of this file by a matching
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

The `Get-WaypointProfileDeclaredInputNameSet` section is a different mechanism, because
its parser is PowerShell, not C#: it is guarded by
`backend/Waypoint.Tests/Core/ComplianceContent/ShapeInventory/WaypointScan.ShapeInventory.Tests.ps1`
(Pester), whose per-shape fixtures and expectations come from the single shared table in
`WaypointScanShapeCorpus.psm1` in the same directory -- the PowerShell analogue of a
`*ShapeInventoryTests` class's `ShapeExpectations` table. That Pester suite is not part of
`dotnet test`; run it locally with `pwsh -NoProfile -Command "Invoke-Pester -Path backend/Waypoint.Tests/Core/ComplianceContent/ShapeInventory/WaypointScan.ShapeInventory.Tests.ps1 -CI"`.
In CI it is a required gate: `.github/workflows/backend.yml`'s
`pester: powershell shape inventory` job runs both `*.Tests.ps1` suites in that directory
and fails the build on any failure, so breaking a shape in `WaypointScan.psm1`, deleting a
corpus fixture, or deleting or editing a row of the section below turns CI red. That
workflow's path filter therefore includes this document as well as `backend/**`.
Its completeness check (doc row <-> corpus entry, both directions), its Expected-column
vocabulary check, and its Expected-verdict-vs-fixture reconciliation (the analogue of
`ShapeInventoryDoc.AssertExpectedVocabulary` / `AssertVerdictMatchesFixtures`, so a
documentation-only edit cannot disarm a shape) are all asserted inside that same Pester
run, not by `ShapeInventoryDoc` -- `ShapeInventoryDoc` only reads C# fixture classes. `scripts/parser-shape-diff.sh` still diffs this parser's corpus old-ref-vs-new-ref
alongside the two C# parsers: see "Real-content conformance and differential checks" below
for how the PowerShell side of that harness works.

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

- **The PowerShell section gets the same three properties, in CI.**
  `Get-WaypointProfileDeclaredInputNameSet`'s section is enforced by Pester rather than
  by `ShapeInventoryDoc`, but with the same guarantees and in the same place: the
  `pester: powershell shape inventory` job in `.github/workflows/backend.yml` runs
  `WaypointScan.ShapeInventory.Tests.ps1` on every PR touching `backend/**` or this
  document, and fails the build on doc<->corpus drift in either direction, on an
  Expected cell that does not open with `Accepted`/`Rejected`, on an Expected verdict
  that disagrees with what the corpus row's expectation `Kind` actually asserts, and on
  any shape whose fixture stops resolving. `WaypointScanShapeCorpus.psm1` is the single
  table those checks and `scripts/dump-waypoint-scan-shape-verdicts.ps1` all read, so
  the Pester suite and the differential harness cannot drift apart either. (Before PR
  #1151's round-1 review this suite existed but ran nowhere: a guard that only fires
  when someone remembers to type the command is the same rot that produced issue
  #1071.)

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

- **Opt-in real-content conformance** (`RealContentConformanceTests` for the two C#
  parsers; `WaypointScan.RealContentConformance.Tests.ps1` (Pester) for
  `Get-WaypointProfileDeclaredInputNameSet`): walks a locally cloned vendor content
  repository at `/workspaces/git/dod-compliance-and-automation` (read-only) and
  reports, per parser, how many real artifacts it accepts versus rejects. Skips
  cleanly (no assertion, no failure -- `Set-ItResult -Skipped` on the Pester side) when
  that path does not exist, so CI never depends on vendor content.
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
Every row here exists because a sibling parser (`Get-WaypointProfileDeclaredInputNameSet`
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

## `Get-WaypointProfileDeclaredInputNameSet` (`WaypointScan.psm1`)

Shapes of the `inputs:` block of an `inspec.yml` manifest, as seen by the shared
line-oriented PowerShell manifest scanner (PR #1135 extracted this function so both
the NSX and vSphere scan paths use it). Unlike `InspecManifestParser` this is a
hand-rolled indentation-tracking scan, not a real YAML parser -- its history is the
worst in this file: issue #1071 found it silently missed several shapes, PR #1084's
first fix commit introduced a NEW silent miss (an entry whose `name:` key is not
first), and PR #1084's round-1 review caught that regression before merge. This
section exists to keep the corpus that already caught that regression from eroding.
Every row returns the set of declared input NAMES (a `List[string]`, no per-input
type/required detail) -- accept rows assert `nsx_manager_address` is a member of
that set (or, for the two `depends:`-adjacency rows, that a differently-named
`depends:` entry is NOT a member).

| Shape ID | Description | Expected |
|---|---|---|
| `indented-dash-sequence` | `inputs:` entries as an indented `  - name: ...` sequence. | Accepted; the name is a member of the declared set. |
| `column0-dash-sequence` | `inputs:` entries as a column-0 `- name: ...` sequence (no indentation before the dash) -- the shape every shipped NSX 4.x/3.x manifest actually used per issue #1071. | Accepted; the name is a member of the declared set. |
| `name-not-first-key` | An entry mapping lists `description:` before `name:` -- the shape PR #1084's first fix commit silently stopped resolving (its own round-1 review finding, fixed before merge). | Accepted; the name is a member of the declared set regardless of key order. |
| `column0-comment-between-entries` | A `#`-prefixed comment at column 0 between two input entries. | Accepted; both entries' names are members and the comment has no effect. |
| `trailing-inline-comment` | An entry's `name:` value carries a trailing ` #`-prefixed inline comment (a literal space before the `#`). | Accepted; the declared name excludes the comment text. |
| `trailing-comment-tab-separator` | An entry's `name:` value carries a trailing comment separated by a TAB instead of a space before the `#` (no literal `' #'` substring on the line). | Accepted; the name is a member of the declared set with the comment stripped, exactly as the space-separated `trailing-inline-comment` row is. Issue #1099's first extension of this guard to this parser found this row genuinely RED: the comment-stripping match required a literal space immediately before `#`, so the tab and comment text leaked into the captured name and it never matched a known auth-key name -- the parser's fourth defect of this class (issue #1071, PR #1084's own fix, PR #1084's block-scalar false positives, and this one). Filed and fixed as issue #1152 in the same change that added this row: the match now looks for `#` preceded by ANY run of whitespace, and a quoted name's closing quote is located explicitly first so a `#` inside quotes is never treated as a comment introducer (issue #1152's third acceptance criterion, pinned by the `quoted-name-containing-hash` row below). |
| `trailing-comment-multi-space-separator` | An entry's `name:` value carries a trailing comment separated by MORE THAN ONE space before the `#`. | Accepted; the name is a member of the declared set with the comment stripped -- the same fix (issue #1152) that made `trailing-comment-tab-separator` accepted covers this shape too, since both are "not exactly one literal space before `#`". |
| `block-scalar-folded-description` | An entry's `description:` uses a folded block scalar (`>`) spanning multiple lines -- PR #1084 found this shape produced real false positives (a subsequent block-scalar content line briefly misread as an entry key) before its indentation-column tracking was tightened. | Accepted; the name is a member of the declared set. |
| `block-scalar-literal-description` | An entry's `description:` uses a literal block scalar (`\|`) spanning multiple lines -- same PR #1084 false-positive class as the folded form. | Accepted; the name is a member of the declared set. |
| `nested-extra-keys-ignored` | An entry carries extra nested keys (`sensitive:`, a nested `value:` mapping) beyond `name`/`type`/`required`. | Accepted; the name is a member of the declared set. |
| `empty-inputs-sequence` | `inputs: []`. | Accepted; the declared set is empty, not an error. |
| `missing-inputs-key` | No `inputs:` key present at all (this scanner has no `attributes:` legacy alias -- unlike `InspecManifestParser`, it only recognizes `inputs:`). | Accepted; the declared set is empty, not an error. |
| `document-start-end-markers` | The document opens with a `---` marker and closes with a `...` marker. | Accepted; the name is a member of the declared set exactly as the unmarked document is -- PR #1084 round-1 review's finding 3 (a `---` marker read as a sequence entry, leaving the block open) is closed: the `-` marker only opens a sequence entry when followed by whitespace or end-of-line. |
| `quoted-scalar-name-double` | An entry's `name:` value is a double-quoted scalar (`name: "nsx_manager_address"`). | Accepted; the declared name excludes the quote characters. |
| `quoted-scalar-name-single` | An entry's `name:` value is a single-quoted scalar (`name: 'nsx_manager_address'`). | Accepted; the declared name excludes the quote characters. |
| `quoted-name-containing-hash` | An entry's `name:` value is a quoted scalar whose content CONTAINS a `#` (`name: "nsx#manager_address"`) -- issue #1152's third acceptance criterion. A `#` inside quotes is not a YAML comment introducer, so stripping from it would truncate a legitimate name and produce the same silent-miss class as the tab-separator defect. | Accepted; the declared name is the full quoted content including the `#`, not truncated at it -- the parser locates the closing quote first and only then discards anything after it. |
| `nested-name-under-value-mapping` | An entry carries a nested `value:` **mapping** that itself has a `name:` key -- PR #1084 round-1 review's finding 1/2 false-positive class. | Accepted; only the entry's own top-level `name:` is a member of the declared set, not the nested one. |
| `nested-name-under-value-sequence` | An entry carries a nested `value:` **sequence** whose first item is a mapping with a `name:` key -- PR #1084 round-1 review's finding 2 (a false positive present on `origin/main` even after the first fix commit). | Accepted; only the entry's own top-level `name:` is a member of the declared set, not the nested one. |
| `inputs-depends-adjacency` | An `inputs:` block is immediately followed by a `depends:` block at the same indent, with a DIFFERENTLY-named entry in `depends:` -- the block-scoping boundary case that produced the original defect in this helper (issue #1071). | Accepted; the `inputs:` entry's name is a member of the declared set and the `depends:` entry's name is NOT. |
| `tab-block-indentation` | A raw tab character used for the `inputs:` sequence's block indentation. | Accepted; the name is a member of the declared set -- this scanner treats a raw tab as ordinary block whitespace (`\s` in its indentation regex), unlike `InspecManifestParser`'s underlying real YAML parser, which rejects the same byte sequence as invalid YAML (see that parser's `tab-block-indentation` row). The two parsers reading the SAME shape ID differently is a real, pre-existing divergence between a hand-rolled scanner and a spec-compliant parser, not a defect introduced by this guard. |
| `crlf-line-endings` | The whole document (indented-dash-sequence shape) uses CRLF line endings throughout. | Accepted; the name is a member of the declared set -- `[System.IO.File]::ReadAllLines` normalizes CRLF, so this parser (unlike `InspecManifestParser` before PR #1084) has no CRLF gap. |

## `XccdfParser` (`backend/Waypoint.Core/ComplianceContent/Xccdf/XccdfParser.cs`)

Shapes of a single XCCDF `Benchmark` XML document (issue #1099, extending #1077 to the
last two of three parsers PR #1098's first slice did not cover; `XccdfParserTests`
issue #730 already covers malformed/oversized/XXE/missing-required-field rejection --
this section is the namespace/prefix/encoding structural-shape dimension that guard
did not enumerate as a documented, differential-harness-tracked corpus). Every document
here is an INVENTED miniature only shaped like public DISA XCCDF structure -- no real
vendor/DISA content appears anywhere in this file.

| Shape ID | Description | Expected |
|---|---|---|
| `default-namespace-declared` | The `Benchmark` root and its children declare the XCCDF 1.2 namespace as the default namespace (`xmlns="..."`, no prefix). | Accepted; one rule resolves. |
| `prefixed-namespace-elements` | The document uses an explicit namespace prefix on every element (`<xccdf:Benchmark xmlns:xccdf="...">`, `<xccdf:title>`, `<xccdf:Rule>`, ...). | Accepted; one rule resolves -- the parser matches every element by local name only (`LocalName(element)`), never by namespace-qualified identity, so a prefix never hides a shape from it. |
| `no-namespace-declared` | The document declares no XML namespace at all. | Accepted; one rule resolves -- the parser never requires a namespace to be present. |
| `nested-group-within-group-rule` | A `Rule` sits two `Group` levels deep (`Group > Group > Rule`), one level deeper than the `Group > Rule` shape `XccdfParserTests`' `ValidDocument` fixture already exercises. | Accepted; one rule resolves -- `EnumerateDescendants` recurses through every intermediate element regardless of depth. |
| `non-utf8-encoding-declaration-ignored-for-char-stream` | The XML declaration states `encoding="ISO-8859-1"` even though the document is parsed from an already-decoded .NET `string` (`XmlReader.Create` over a `StringReader`), which cannot re-decode bytes. | Accepted; one rule resolves -- a declared encoding that disagrees with the (already-Unicode) character stream is not a parse error for this entry point, only for a byte-stream reader this parser never uses. |
| `mixed-case-title-and-version-child-elements-still-match` | The `title`/`version` child elements are spelled `TITLE`/`Version` (mixed case). | Accepted; parses without error -- `FindChildText`/`FindChildAttribute` compare local names with `OrdinalIgnoreCase`, unlike the root-element check below. |
| `lowercase-benchmark-root-element` | The root element is spelled `benchmark` (all lowercase) instead of `Benchmark`. | Rejected as missing the top-level `Benchmark` element -- the root-element local-name check is deliberately `StringComparison.Ordinal` (case-SENSITIVE), the one place this parser's otherwise case-insensitive element matching does not apply; discovered by this guard's first extension to this parser (issue #1099), not a pre-existing documented behaviour. |
| `byte-order-mark-before-declaration` | A literal UTF-8 BOM character (`U+FEFF`) precedes the `<?xml ...?>` declaration in the `string` handed to `TryParse`. | Rejected as not valid/safe XML ("Data at the root level is invalid") -- `XmlReader.Create` over a `StringReader` does not strip a BOM character embedded in the character stream itself (only a byte-stream reader strips a BOM from the raw bytes before decoding); discovered by this guard's first extension to this parser (issue #1099). Any caller reading XCCDF content from bytes must strip a BOM before decoding to `string`, or this shape is a hard rejection, not a silent pass-through. |

## `VendorHierarchyInterpreter` leaf-manifest dimension (`backend/Waypoint.Core/ComplianceContent/SemanticImport/VendorHierarchyInterpreter.cs`)

`LayoutTableParityTests` (issue #959) already guards this interpreter's PATH/layout
dimension -- which family/component a directory shape resolves to -- against
`docs/compliance-parity.md`'s provenance matrix. This section (issue #1099) guards the
orthogonal dimension: how the interpreter turns an already-classified path's PARSED
`inspec.yml` manifest and entry metadata into a `SemanticCandidate`'s fields --
display-name fallback, aggregate-vs-leaf disposition, and pass-through/derived fields.
No real vendor content, path, or manifest appears anywhere in this file.

| Shape ID | Description | Expected |
|---|---|---|
| `title-present-leaf-uses-manifest-title-as-display-name` | A non-aggregate object-kind-split leaf (`vsphere/.../vcenter`) whose `inspec.yml` declares a `title:`. | Accepted; the candidate's `DisplayName` equals the manifest's `Title` verbatim. |
| `title-missing-split-leaf-falls-back-to-tail-segment-literal` | The same object-kind-split shape, but the manifest declares no `title:`. | Accepted; `DisplayName` falls back to the leaf's own path-segment literal (`tail[0]`, e.g. `esxi`), never a synthesized string. |
| `title-missing-whole-appliance-falls-back-to-family-name` | A whole-appliance family (`photon`) leaf whose manifest declares no `title:`. | Accepted; `DisplayName` falls back to the vendor family name (`photon`) -- the whole-appliance branch's fallback source differs from a split family's (family name vs. tail segment), and both must be exercised, not just one. |
| `empty-tail-with-controls-directory-is-an-executable-leaf` | A whole-appliance profile found directly AT its baseline directory (empty tail) that DOES have a `controls/` directory. | Accepted; `IsAggregate` is `false` and `IsExecutableLeaf` is `true` -- a bare baseline directory with real controls is a directly executable profile, not a grouping node. |
| `empty-tail-without-controls-directory-is-an-aggregate` | The same empty-tail shape, but with NO `controls/` directory. | Accepted; `IsAggregate` is `true` -- an empty tail alone does not make a leaf executable; the absence of a `controls/` directory is what marks it a pure grouping node. |
| `non-empty-tail-forces-aggregate-even-with-controls-directory` | A whole-appliance profile found one segment BELOW its baseline directory (non-empty tail) that DOES have a `controls/` directory. | Accepted; `IsAggregate` is `true` regardless of the `controls/` directory's presence -- `isAggregate = tail.Length > 0 \|\| !HasControlsDirectory` is an OR, so a non-empty tail alone is sufficient and the `controls/`-directory signal is never consulted once the tail is non-empty. |
| `inputs-supports-depends-carried-through-unchanged` | A leaf manifest declaring one `inputs:` entry, one `supports:` platform string, and one `depends:` profile string. | Accepted; the candidate's `Inputs`/`Supports`/`Depends` collections carry the manifest's values through unchanged -- the interpreter never filters, renames, or drops them on the way into `SemanticCandidate`. |
| `content-digest-differs-when-release-key-differs-same-manifest-and-controls` | Two leaves under the SAME family/product-version, with byte-identical manifest content and control file names, but DIFFERENT release directories (`v2r3-stig` vs. `v2r4-stig`). | Accepted; the two candidates' `ContentDigest` values differ -- `ComputeDigest` folds `releaseKey` into the hash, so an otherwise-identical manifest re-published under a new release is never digest-collision-indistinguishable from the old one. |
