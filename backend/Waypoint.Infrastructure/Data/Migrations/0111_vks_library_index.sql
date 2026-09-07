-- Issue #1480 (epic #1184, split from #1054 (closed as design record 2026-08-30));
-- research: lane #1031 (VKS library lane, ratified). Slot 0111 pre-assigned
-- 2026-08-30; verified free against both the migrations directory and open PRs at
-- authoring time (main's ledger was at 91, 0129 last merged; #1509/#1389 hold
-- 0108/0113 respectively and are still in flight; #1765's 0109 has since
-- landed on main, reconciled by this branch's round-1 rebase -- per this repo's
-- standing merge-verification convention for concurrent migration slots).
--
-- This is the shared dual-backend identity/dimension model for children B/C/D of
-- #1184's VKS rescope (model-only: no sync logic lands here). #1031 Layer B found a
-- single name grammar that parses 138/138 live public library items:
--   ob-<buildId>-[tkgs-ova-]<distro>-<distroVersion>[-<arch>][-vmi-k8s|-k8s]
--     -v<k8sVersion>---vmware.<n>[-fips][.<n>]-(vkr|tkg).<n>[.<suffix>]
-- across four coexisting naming eras (oldest to newest): legacy `*-k8s-*` (arch
-- absent), `tkgs-ova-*` (arch absent), `*-vmi-k8s-*` (arch present), and the current
-- era (no k8s token at all, arch present). Arch is absent on 46/138 live items
-- (nullable here, to be backfilled later from the OVF's
-- `vmware-system.tkr.os-arch` ProductSection key by whichever backend fetches the
-- OVF -- not this issue). FIPS is a real, non-monotonic dimension (#1031: the
-- newest vkr releases dropped the `-fips` token entirely) so it is never inferred
-- from release recency. `---` in the raw name encodes `+` in the upstream version
-- string (`<k8s>+vmware.<n>-[fips-]vkr.<n>`) -- #1031's ordering finding, since
-- every VKR catalog entry shares one releaseDate and version-string ordering is
-- the only reliable one; ordering itself is a later child's concern, using the
-- shared Waypoint.Core.Versions.ProductVersionComparer (issue #1039, PR #1767,
-- landed on main as 16fec7bf) rather than a local comparator.
--
-- vks_library_items ------------------------------------------------------------------
-- One row per library item, from either backend (#1031 Layer D: the signed VCF
-- product catalog's VKR productVersions, depot-fed; or Layer B: the public
-- wp-content.broadcom.com/v2/latest/lib.json + items.json, public-mirror). Unique
-- per (source, name) -- the same name never appears twice for one backend, but the
-- two backends are allowed to have independently discovered the same name (e.g.
-- before a cross-reconciliation step a later child may add). item_uuid is Layer B's
-- items.json `id` (urn:uuid) and is nullable because Layer D (depot-fed) has no
-- per-item UUID, only file paths and checksums.
--
-- parse_status/naming_era: an item whose name the grammar cannot parse is STILL
-- STORED here (parse_status='unparsed', naming_era='unparsed', every dimension
-- column NULL) rather than dropped -- #1031's Risk "a future upstream naming
-- change could break parsing" and this issue's AC: quarantine for visibility, never
-- silently discard (the #1446 lesson this repo already applies elsewhere).
CREATE TABLE IF NOT EXISTS vks_library_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL,
    source TEXT NOT NULL CHECK (source IN ('depot', 'public')),
    item_uuid TEXT NULL,
    distro TEXT NULL,
    distro_version TEXT NULL,
    arch TEXT NULL,
    k8s_version TEXT NULL,
    vmware_build TEXT NULL,
    fips BOOLEAN NOT NULL DEFAULT false,
    release_line TEXT NULL CHECK (release_line IN ('vkr', 'tkg')),
    line_build TEXT NULL,
    ob_build_id TEXT NULL,
    naming_era TEXT NOT NULL CHECK (naming_era IN ('legacy_k8s', 'tkgs_ova', 'vmi_k8s', 'current', 'unparsed')),
    parse_status TEXT NOT NULL CHECK (parse_status IN ('parsed', 'unparsed')),
    etag TEXT NULL,
    sha256 TEXT NULL,
    size_bytes BIGINT NULL,
    created_upstream TIMESTAMPTZ NULL,
    discovered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT vks_library_items_source_name UNIQUE (source, name)
);

CREATE INDEX IF NOT EXISTS idx_vks_library_items_distro_k8s
    ON vks_library_items (distro, k8s_version);

COMMENT ON TABLE vks_library_items IS
    'Issue #1480: dual-backend VKS/VKR library item identity/dimension model (depot-fed and public-mirror). Unparseable names are stored with parse_status=''unparsed'' and every dimension NULL, never dropped.';
COMMENT ON COLUMN vks_library_items.item_uuid IS
    'The public items.json entry''s id (urn:uuid). NULL for depot-fed rows (#1031 Layer D has no per-item UUID, only catalog file paths and checksums).';
COMMENT ON COLUMN vks_library_items.etag IS
    'An item/batch-scoped CHANGE TOKEN from the public items.json file entry -- NEVER a checksum. #1031 Layer B measured 93/138 items publishing an identical etag across all four of an item''s files (a 3.7 GB vmdk and a 249-byte .mf alike), and one item where the served .mf''s real MD5 differed from its published etag. Treat strictly as opaque: refetch when etag OR size changes, never compare it to sha256 for integrity.';
COMMENT ON COLUMN vks_library_items.sha256 IS
    'Per-item content SHA-256, populated by whichever backend can produce one without extra work: depot-fed reads it directly from the signed catalog''s binaries[] entries (#1031 Layer D); public-mirror computes it after fetch (a later child, not this issue). NULL until a backend has supplied it.';
COMMENT ON COLUMN vks_library_items.arch IS
    'Parsed from the name where the era carries an arch token. NULL on the 46 legacy-named items research measured (tkgs_ova and legacy_k8s eras) -- backfill from the OVF ProductSection''s vmware-system.tkr.os-arch key is a later child''s job, never guessed here.';
COMMENT ON COLUMN vks_library_items.fips IS
    'A real dimension that is NOT monotonic with release recency -- #1031: the newest vkr releases (1.34.1+) dropped the -fips token entirely. Never infer "newer = fips" from this column.';
COMMENT ON COLUMN vks_library_items.naming_era IS
    'Which of the four coexisting name-grammar eras produced this row''s parse (#1031 Layer B), or ''unparsed'' when the grammar could not match the raw name at all.';

-- Runner grants: waypoint_download_runner is the only writer (the depot-fed and
-- public-mirror sync/index jobs both run runner-side, ADR-0013/0014, same
-- "runner owns download-domain writes" precedent as 0025/0091/0100/0107/0117/0127).
-- SELECT+INSERT+UPDATE (upsert semantics on (source, name)); deliberately no DELETE
-- (this repo's #556 grant-hygiene convention -- an item no longer seen upstream is a
-- later reconciliation concern, not a deletion, matching 0091's own precedent).
-- No separate grant is needed for the API/control-plane read path this issue's UI
-- consumer will use: Waypoint.Api connects as the table owner (deploy/compose.yaml's
-- ConnectionStrings__Waypoint uses ${POSTGRES_USER}, ungranted here on purpose), so
-- "API SELECT" is already satisfied by ownership -- following 0090's own precedent of
-- documenting a no-grant posture with an accurate rationale rather than a guessed one.
GRANT SELECT, INSERT, UPDATE ON vks_library_items TO waypoint_download_runner;
