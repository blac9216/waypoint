-- Issue #1392 (epic #1184, split from #1053; slot 0109 pre-assigned 2026-08-30).
-- Findings source: research lane #1030 ("VMware Tools lane"). Metadata-only: no bytes
-- are fetched here, no self-hash is computed beyond what a HEAD response already
-- carries (etag). Self-hash and vendor-signature verification are a later child's job
-- (#1030 section 2) -- self_hash_sha256/signature_available exist now as nullable
-- placeholder columns so that child does not need its own migration.
--
-- vmtools_artifact_index ---------------------------------------------------------------
-- One row per real artifact found by a recursive crawl of the public VMware Tools
-- mirror (https://packages-prod.broadcom.com/tools/). #1030 finding 1: the tree is
-- non-uniform per version and `releases/latest/` / `esx/latest/` / `esx/<major>latest/`
-- are REAL directories of mixed vintages, never a pointer to "current" -- so their
-- contents are indexed like any other path (is_latest_alias=true marks them) and
-- version identity always comes from the FILENAME (research D13/R2-5), never from the
-- directory name. Upsert-keyed on (relative_path, etag): a re-crawl of an unchanged
-- tree touches last_seen_at on the same row rather than inserting a duplicate (issue
-- AC3). etag is the upstream object's MD5 (#1030 finding 1, HTTP facts) and is nullable
-- because a HEAD response is not guaranteed to carry one.
CREATE TABLE IF NOT EXISTS vmtools_artifact_index (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    lane TEXT NOT NULL DEFAULT 'vmtools' CHECK (lane = 'vmtools'),
    relative_path TEXT NOT NULL,
    tools_version_raw TEXT NULL,
    tools_version_major INTEGER NULL,
    tools_version_minor INTEGER NULL,
    tools_version_patch INTEGER NULL,
    tools_build TEXT NULL,
    platform TEXT NOT NULL CONSTRAINT vmtools_artifact_index_platform_check CHECK (platform IN ('windows', 'linux', 'arm', 'unknown')),
    file_type TEXT NOT NULL CONSTRAINT vmtools_artifact_index_file_type_check CHECK (file_type IN ('iso', 'exe', 'sha', 'sig', 'rpm', 'deb', 'other')),
    size_bytes BIGINT NULL,
    etag TEXT NULL,
    self_hash_sha256 TEXT NULL,
    signature_available BOOLEAN NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    is_latest_alias BOOLEAN NOT NULL DEFAULT false,
    CONSTRAINT vmtools_artifact_index_relative_path_etag UNIQUE (relative_path, etag)
);

CREATE INDEX IF NOT EXISTS idx_vmtools_artifact_index_tools_version
    ON vmtools_artifact_index (tools_version_major, tools_version_minor, tools_version_patch);

COMMENT ON TABLE vmtools_artifact_index IS
    'Issue #1392: one row per real artifact found by a recursive crawl of the public VMware Tools mirror. Upsert-keyed on (relative_path, etag) so an unchanged re-crawl never duplicates a row. Never treat is_latest_alias rows as "current" -- research #1030 finding 1: latest/ is a real directory of mixed vintages.';
COMMENT ON COLUMN vmtools_artifact_index.relative_path IS
    'Path relative to the mirror root, e.g. releases/13.1.0/windows/VMware-tools-windows-13.1.0-25218885.iso.';
COMMENT ON COLUMN vmtools_artifact_index.tools_version_raw IS
    'The Tools version string as it appears in the artifact filename (e.g. "13.1.0"), never the containing directory name (#1030 D13/R2-5).';
COMMENT ON COLUMN vmtools_artifact_index.etag IS
    'The upstream HEAD response ETag, which #1030 finding 1 confirmed equals the object MD5. Identity/dedupe key; nullable because not every response is guaranteed to carry one.';
COMMENT ON COLUMN vmtools_artifact_index.self_hash_sha256 IS
    'Placeholder for a later child''s (#1030 section 2) locally computed SHA-256. NULL until that slice ships -- this issue fetches metadata only.';
COMMENT ON COLUMN vmtools_artifact_index.signature_available IS
    'Placeholder for a later child''s vendor-signature verification (osslsigncode / PKCS7 hashdata, #1030 section 2). NULL until that slice ships.';
COMMENT ON COLUMN vmtools_artifact_index.is_latest_alias IS
    'True when relative_path falls under a releases/latest/, esx/latest/, or esx/<major>latest/ subtree. These are indexed like any other path and MUST NOT be treated as "the current version" (#1030 finding 1).';

-- vmtools_esx_version_mapping ------------------------------------------------------------
-- One row per parsed data row of the upstream `versions` file -- the join key
-- correlating an esx/<version> directory name to a Tools version/build (#1030 finding
-- 1). Malformed rows (wrong column count) are never persisted here; the parser returns
-- them separately as warnings so they are surfaced, never silently dropped (the #1446
-- lesson this repo already applies to the ESX patch-store parser). sequence_in_file
-- preserves the upstream file's own newest-first-by-ESXi-build ordering, which is what
-- lets a consumer fall back to file order when tools_version_raw does not parse as a
-- version (issue AC2's "releaseDate-fallback ordering" case) instead of crashing on a
-- numeric comparison.
CREATE TABLE IF NOT EXISTS vmtools_esx_version_mapping (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_in_file INTEGER NOT NULL,
    esxi_version_dir TEXT NOT NULL,
    esxi_build TEXT NULL,
    tools_version_code TEXT NOT NULL,
    tools_version_raw TEXT NULL,
    tools_version_major INTEGER NULL,
    tools_version_minor INTEGER NULL,
    tools_version_patch INTEGER NULL,
    tools_build TEXT NULL,
    raw_row TEXT NOT NULL,
    indexed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT vmtools_esx_version_mapping_dir_code UNIQUE (esxi_version_dir, tools_version_code)
);

CREATE INDEX IF NOT EXISTS idx_vmtools_esx_version_mapping_sequence
    ON vmtools_esx_version_mapping (sequence_in_file);

COMMENT ON TABLE vmtools_esx_version_mapping IS
    'Issue #1392: the upstream `versions` file''s join key, parsed one row per data line (comment header stripped). esxi_version_dir is column 2 verbatim (e.g. esx/9.1, or esx/0.0 meaning "not bundled with any ESXi"), which is literally the esx/ subdirectory name -- never resolved as a semantic version. Malformed rows are skipped here and surfaced as parser warnings, never persisted or swallowed.';
COMMENT ON COLUMN vmtools_esx_version_mapping.sequence_in_file IS
    'Zero-based position of this data row in the upstream versions file, which is ordered newest-first by ESXi build. Lets a consumer order by release recency even when tools_version_raw does not parse (issue AC2).';
COMMENT ON COLUMN vmtools_esx_version_mapping.esxi_build IS
    'Column 3: the ESXi build number. NULL when esxi_version_dir is esx/0.0 (this Tools build is not bundled with any ESXi).';
COMMENT ON COLUMN vmtools_esx_version_mapping.tools_version_code IS
    'Column 1: the Tools version code shown on the VI/NGC client (e.g. "13344").';
COMMENT ON COLUMN vmtools_esx_version_mapping.tools_version_raw IS
    'Column 4: the Tools version as shown in guest Setup/About (e.g. "13.1.0"). NULL only when the row itself is malformed -- in that case the row is not persisted at all.';
COMMENT ON COLUMN vmtools_esx_version_mapping.raw_row IS
    'The full whitespace-joined source line, kept for audit/debugging of the parse.';

-- Runner grants: waypoint_download_runner is the only writer -- the crawler and the
-- versions-file parser both run runner-side against the runner's own outbound HTTP
-- access (same "runner owns download-domain writes" precedent as 0025/0091/0100/0107/
-- 0127). SELECT+INSERT+UPDATE on both tables (upsert semantics on both); deliberately no
-- DELETE (this repo's #556 grant-hygiene convention -- neither repository method nor
-- crawler ever removes a row; a bundle no longer seen is a later reconciliation
-- concern, not a deletion, matching 0091's own precedent).
GRANT SELECT, INSERT, UPDATE ON vmtools_artifact_index TO waypoint_download_runner;
GRANT SELECT, INSERT, UPDATE ON vmtools_esx_version_mapping TO waypoint_download_runner;
