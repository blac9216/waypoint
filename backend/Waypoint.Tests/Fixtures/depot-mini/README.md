# depot-mini: shared sanitized mini-depot fixture (issue #1696)

Every value under this directory is **fabricated** for this fixture. Structural facts
(paths, field names, tag shapes) are real and traced below to the research finding or
documented layout fact each one encodes. Zero vendor bytes, zero real URLs, zero
identifiers from the real depot -- see AGENTS.md's sanitization mandate.

## Why a "placeholder" suffix on binary-shaped files

The sanitize scanner (`.github/sanitize/scan_repo_specific.py`) refuses to pass ANY
tracked file whose extension is `.zip`, `.gz`, `.tgz`, or `.pdf` -- unconditionally,
regardless of content (`UNINSPECTABLE_EXTENSIONS`). A real depot's binaries use
exactly those shapes (`*-updaterepo.zip`, `*.tar.gz`), so every "binary" placeholder in
this tree is checked in with an extra `.placeholder` suffix appended to its real
depot-relative name (e.g. `vcsa-full-a-updaterepo.zip.placeholder`) and is plain UTF-8
text -- a few lines naming what it stands in for, nothing else. `DepotMiniFixture.cs`
(C#) and `New-DepotMiniFixture.ps1` (PowerShell) both strip that suffix while
materializing the tree into a throwaway temp directory, so the code under test always
sees the real, depot-shaped filename -- the `.placeholder` suffix never reaches disk
outside git. `.iso`/`.ova` placeholders keep the same convention for consistency even
though those two extensions are not in the forbidden list (they decode as plain text
and would pass either way).

## Provenance map

| Structural fact | Source |
|---|---|
| Catalog document at `PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json` | #1027 depot-consumption finding (`lcm.depot.adapter.remote.v2.rootDir`); mirrors `ManagedToolOptions.ProductVersionCatalogPath`'s default and `WaypointCatalogIndex.psm1`'s `$Script:DefaultCatalogRelativePath` |
| `patches` object keyed by component, each an array of `{ productVersion, artifacts.bundles[].binaries[] }` | `VendorProductVersionCatalogParser.cs` / `ConvertFrom-WaypointCatalogJson` -- the real Broadcom catalog shape both consumers already parse |
| Ordinary binary resolves to `PROD/COMP/<Product>/<fileName>` | #1027 comment https://github.com/blac9216/waypoint/issues/1027#issuecomment-5457289016 (`…vcfBinariesDir`); round-2 PR #1629 review finding 1 (manifest keys are DepotPath-relative, not bare filenames) |
| `metadata[]` entry with `tag: "zip-expand"`, `configuration: { key: "relative", value: "vmw/<uuid>/<version>" }` (VCENTER's two zip binaries) | #1027 comment (same URL) ratified zip-expand scope addition; PR #1629 round-1 finding 1/5 (two same-version zips each carry their own distinct `relative` value and must not collide) |
| `configuration` as a single object (zip a) AND as an array of key/value pairs (zip b) | `Get-BinaryZipExpandRelativePath`'s own doc comment: "read defensively as either... the catalog document's own shape is not ours to assume beyond what #1027 documented" |
| Zip binary staged alongside its own expanded tree (round-3 steady state) | PR #1629 round-3 review finding 1 -- the fully-staged case no prior fixture round staged |
| `vcsa-fixture-9.1.0.6543-patch.iso`: a second bundle (`b2b`) in the SAME catalog entry as `b2` | issue #687's flattening contract (`VendorProductVersionCatalogParser`'s own doc comment: "flattens every binary across every component/entry") -- a distinct binary contributed by a second bundle of one entry, not only by a separate entry/component |
| `nsx-missing.ova` listed in two bundles (`b3`, `b3b`) of the same entry with different checksums | `VendorProductVersionCatalogParser.Parse`'s dedup-by-filename rule (issue #687, `byFileName[upsert.RelativePath] = upsert`) / `ConvertFrom-WaypointCatalogJson`'s identical `$ByFileName[$Binary.fileName] = ...` overwrite -- both consumers keep the LAST bundle in document order, proven here against a real document instead of only a hand-typed JSON string |
| `vcsa-corrupt.iso`: on-disk bytes present but size/hash disagree with the catalog | `Test-CatalogEntryPresent` -- a mismatch is "missing", not merely path-present |
| `esxi-image.iso`: size-only catalog row (no `checksum` field) | issue #1696 dispatch requirement; exercises the null-safe hash comparison in `Test-CatalogEntryPresent` / `TryParseBinary` |
| `TKG` component: 12 versions, only 2 staged on disk | issue #1696 dispatch requirement ("K8s-dominant product with many versions"); mirrors #1027's empirical finding that the Kubernetes-release product key is ~0% staged in a real depot |
| `productVersion: "9.1.0.6543"` (four dot-separated numeric segments, one > 255) | docs issue #1694 / the sanitize scanner's IP-literal check treats a dotted quad as a candidate IP only when every octet is <= 255; this version is a regression probe that the scanner's IP heuristic does not misfire on a legitimate build-suffixed version |
| ESX patch store, Depot91 layout: `PROD/COMP/ESX_HOST/patch-store/hostupdate/` | #1028 comment https://github.com/blac9216/waypoint/issues/1028#issuecomment-5457273036 -- table row "VCF/VVF 9.1 ... inside the depot tree: `PROD/COMP/ESX_HOST/patch-store/hostupdate/…`"; `EsxPatchStoreMetadataParser.Depot91RelativeSegments` |
| ESX patch store, Legacy layout: `<storeRoot>/hostupdate/` | #1028 comment (same URL) -- table row "VCF/VVF 8.x ... sidecar patch store: `<store>/hostupdate/<VENDORCODE>/…`"; `EsxPatchStoreMetadataParser.HostupdateDirName` |
| `hostupdate/__hostupdate20-consolidated-index__.xml`, `<vendor>/__hostupdate20-consolidated-metadata-index__.xml`, `vendor-index.xml`, per-VIB `vibs/*.xml` with `relative-path`/`checksum` | `EsxPatchStoreMetadataParserTests.cs`'s own fixture-construction helpers (`WriteConsolidatedIndex`, `WriteVendorMetadataIndex`, `WriteMetadataZip`), which this fixture's `umds-parts/*.xml` and loader-side zip assembly single-source |
| `hostupdate/hardlink-hostupdate/` staging-tree directory | `EsxPatchStoreMetadataParser.StagingTreeDirName` (issue #1164) -- "the tool's download-time staging tree ... skipped and warned about rather than treated as an empty/unknown vendor" |
| `PROD/metadata/upgrade_info.xml` | #1027 comment (same URL as above), finding 7: "a vendor-signed `upgrade_info.xml` ... enumerates upgrade entries"; `WaypointCatalogIndex.psm1`'s own upgrade_info.xml exception (never unknown) |
| `stray/unexpected-file.bin` (deliberate unknown file, outside `PROD/`) | issue #1696 dispatch requirement; a real depot share can carry operator junk beside the depot root itself |
| `vmw/1111aaaa/9.1.0.5210/manifest/` and `.../package-pool/` (zip a's expanded tree) | issue #1640: a real expanded updaterepo tree carries its own `manifest/` and `package-pool/` subdirectories; `Test-ZipExpandTreeComplete` requires both non-empty before reporting a zip-expand entry `present`, so zip a's tree needed this shape to keep reporting `present` under the new standard |

## Hash/size strategy: template rewrite, computed from the fixture's own bytes

The catalog document is checked in with two token forms in place of a real
size/checksum on the subset of entries whose files are actually staged in this tree:

- `"{{sha256:<fixture-relative-path>}}"` (a full quoted string token) -- replaced with
  the uppercase hex SHA-256 of the **materialized** file's bytes.
- `"{{size:<fixture-relative-path>}}"` (a full quoted string token, including the
  quotes) -- replaced with the file's byte length as a bare JSON integer.

Both `DepotMiniFixture.cs` and `New-DepotMiniFixture.ps1` perform this rewrite
identically, immediately after copying the tree (and stripping `.placeholder`
suffixes) into a throwaway temp directory -- so hash verification against these rows
is real, not asserted-then-ignored. Every other entry (the corrupt/missing/TKG-filler
rows) carries a literal, invented, and deliberately never-matching hash string, because
those rows exist specifically to prove a mismatch or an absence, not a match. Every
literal, invented hash is still exactly 64 lowercase hex characters -- the real SHA-256
shape -- so a hash-length/format regression in either parser is not silently uncatchable
just because the fixture's own hash never matches; `Parity/DepotMiniFixtureLintTests.cs`
guards this over every checked-in `checksum`/`checksum-type="sha-256"` value.

## Extending this fixture

1. Add the new binary under the correct `PROD/COMP/<Product>/` (or ESX store) path,
   named `<real-name>.placeholder`, containing a one-line invented description.
2. Add its catalog entry to `productVersionCatalog.json`, using a `{{sha256:...}}` /
   `{{size:...}}` token pair if you want real hash verification, or a literal fabricated
   hash if the row exists to prove a mismatch/absence -- a literal hash MUST be exactly
   64 lowercase hex characters (`DepotMiniFixtureLintTests` enforces this).
3. Add a row to the table above naming the research finding or documented layout fact
   the new structural detail encodes. An addition with no such row is not
   fixture-worthy -- it belongs in an ad hoc per-test fixture instead.
4. Never add a `.zip`/`.gz`/`.tgz`/`.pdf` file to this tree, checked in or not, without
   the `.placeholder` suffix -- the sanitize scanner refuses those extensions
   unconditionally.
