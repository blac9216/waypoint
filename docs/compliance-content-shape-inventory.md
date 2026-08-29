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
  code, and flags any shape that resolved under the old ref but no longer does under
  the new one. This is the check that catches a fix silently losing a shape no current
  real content happens to exercise -- conformance against today's content cannot see
  that class of regression at all.

## `InspecManifestParser` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/InspecManifest.cs`)

Shapes of the `inputs:` (and legacy `attributes:`) block of an `inspec.yml` manifest.
Every row here exists because a sibling parser (`Get-WaypointNsxProfileAuthInputKeySet`
in `WaypointScan.psm1`, issue #1071) missed one of these shapes in real content, or
because fixing it (PR #1084) silently broke another one.

| Shape ID | Description | Expected |
|---|---|---|
| `indented-dash-sequence` | `inputs:` entries as an indented `  - name: ...` sequence (the shape every existing test used before this inventory). | Resolves the input by name. |
| `column0-dash-sequence` | `inputs:` entries as a column-0 `- name: ...` sequence (no indentation before the dash) -- the shape every shipped NSX manifest actually used per issue #1071. | Resolves the input by name. |
| `name-not-first-key` | An entry mapping lists `description:` before `name:` -- the shape PR #1084's first fix commit silently stopped resolving (issue #1077's motivating regression). | Resolves the input by name regardless of key order. |
| `attributes-legacy-alias` | Manifest uses the legacy `attributes:` key instead of `inputs:`. | Resolves via the alias exactly as `inputs:` would. |
| `column0-comment-between-entries` | A `#`-prefixed comment at column 0 between two input entries. | Both entries resolve; the comment has no effect. |
| `trailing-inline-comment` | An entry's `name:` value carries a trailing inline `# comment`. | Resolves; the input name excludes the comment text. |
| `block-scalar-folded-description` | An entry's `description:` uses a folded block scalar (`>`) spanning multiple lines. | Parses without error; the input still resolves by name. |
| `block-scalar-literal-description` | An entry's `description:` uses a literal block scalar (`\|`) spanning multiple lines. | Parses without error; the input still resolves by name. |
| `nested-extra-keys-ignored` | An entry carries extra nested keys (`sensitive:`, a nested `value:` mapping) beyond `name`/`type`/`required`. | Resolves name/type/required; extra keys are ignored, not errors. |
| `empty-inputs-sequence` | `inputs: []`. | Resolves to zero inputs; not an error. |
| `missing-inputs-key` | No `inputs:` or `attributes:` key present at all. | Resolves to zero inputs; not an error. |
| `crlf-line-endings` | The whole document (indented-dash-sequence shape) uses CRLF line endings throughout. | Resolves identically to its LF counterpart -- CRLF is a known untested gap (PR #1084 finding: `WriteProfileFixtureRaw` fixtures inherit LF). |

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

## Deferred parsers (tracked as issue #1077's enumerated remainder)

The following parsers are in issue #1077's scope but not yet covered by this
inventory: `Get-WaypointNsxProfileAuthInputKeySet` (`WaypointScan.psm1`),
`XccdfParser`, and `VendorHierarchyInterpreter` (beyond the layout-table parity guard
`docs/compliance-parity.md` already provides for its directory-literal dimension --
this inventory would add the leaf-manifest/encoding dimensions on top of that).
