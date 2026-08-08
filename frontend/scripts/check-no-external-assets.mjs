#!/usr/bin/env node
/**
 * Fails the build if the production bundle (dist/) references anything
 * external — the air-gap requirement from CLAUDE.md ("no CDN assets, no
 * phone-home ... at runtime") and ADR-0007 ("CI should fail on any external
 * URL in the build output"), made an enforced check rather than a
 * convention (issue #11's acceptance criterion).
 *
 * Scope: explicit `http://`/`https://` URLs. Protocol-relative `//host/...`
 * references are deliberately NOT flagged — that pattern collides with
 * ordinary `//` sequences (line comments, path fragments) too often to be a
 * useful signal, and every realistic air-gap violation this guards against
 * (CDN <script>/<link> tags, remote fonts/images, analytics beacons) uses an
 * explicit scheme in every toolchain in this stack.
 *
 * ═══════════════════════════════════════════════════════════════════════════
 * FILE SELECTION IS AN ALLOWLIST OF WHAT IS *SCANNABLE*, NOT A DENYLIST OF
 * WHAT IS OPAQUE. This is the single most important thing about this file.
 * ═══════════════════════════════════════════════════════════════════════════
 *
 * This guard has failed open five times, and every previous fix corrected the
 * *list* or the *table*:
 *
 *   1. issue #65 round 2 — an extension *allowlist* of "text" types silently
 *      skipped `.mjs` and extensionless files.
 *   2. issue #77 — compressed artifacts sat on the known-binary skip list and
 *      were silently ignored.
 *   3. issue #81 — after #77 replaced that with a `.br`/`.gz`/`.zip`
 *      denylist, formats nobody had added to *that* list yet (`.zst`, `.xz`,
 *      `.7z`, `.lz4`, …) fell through to the scan branch, decoded as UTF-8,
 *      matched nothing, and were reported as *scanned*.
 *   4. PR #88 round 1 — the fix for #81 *replaced* the extension list with
 *      magic-byte sniffing instead of adding to it, so `.gz`/`.zip` lost
 *      their fail-closed signal and three real shapes started printing `OK`.
 *   5. PR #88 round 2 — with a magic-byte table *and* an extension list in
 *      union, the reviewer walked straight past both with `.Z` (Unix
 *      compress), lzop, lzip, RAR, zstd skippable frames and a `.tar` holding
 *      compressed members.
 *
 * Five is not five unlucky omissions. It is a structural fact:
 *
 *   **The set of opaque formats is unbounded. Enumerating it can never
 *   converge.**
 *
 * So this file no longer tries. The question it asks is inverted:
 *
 *   OLD (lost five times): "did we remember this compression format?"
 *   NEW (finite, closed):  "is this a text format we can actually scan, or a
 *                           binary asset type we explicitly recognise?"
 *
 * THE THREE DISPOSITIONS. `classifyDistFile()` is the ONE predicate that
 * decides, and `scanDist()` — the code path the CLI actually runs — calls it
 * for every file. There is deliberately no second implementation anywhere;
 * `scripts/check-no-external-assets.test.mjs` has a test that fails if
 * `scanDist` stops routing through it, because "the property test guards a
 * function the CLI never runs" is how escape #4 shipped.
 *
 *   • SCAN — the file is *confidently text*: it decodes as valid UTF-8 and
 *     contains no NUL bytes. Only these are scanned for external references.
 *     (Plus one narrow name-based veto, `COMPRESSED_EXTENSIONS` — see below.)
 *
 *   • SKIP — the file's **content** matches `KNOWN_BINARY_SIGNATURES`, a
 *     short, explicitly justified allowlist of binary asset types a Vite
 *     `dist/` legitimately contains, each verified by magic bytes and never
 *     by filename. Skipped files are still byte-scanned for a *cleartext*
 *     URL (see `scanDist`) — the skip is only a licence to not fail for being
 *     opaque, not a licence to ignore the bytes.
 *
 *   • FAIL — **everything else.** Unrecognised, undecodable, compressed,
 *     archived, encrypted, UTF-16, truncated or merely ambiguous: all of it
 *     fails the build. There is no fallthrough bucket, so there is nothing
 *     for a novel format to fall through *into*.
 *
 * Under this model `.Z`, lzop, lzip, RAR, a `.tar` of compressed members, a
 * zstd skippable frame, a file named exactly `.gz`, and every format invented
 * after this comment was written all fail by default, without anyone having
 * heard of them. That is the whole point: the failure mode of a format nobody
 * enumerated is now "the build stops", not "OK — 1 file(s) scanned".
 *
 * THE OPERATOR'S ESCAPE HATCH is to add a justified entry to
 * `KNOWN_BINARY_SIGNATURES` with its magic bytes and a reason. That is a
 * deliberate, reviewable act in a diff — which is exactly what an air-gap
 * exemption should be, and the opposite of the silent pass a denylist gave.
 *
 * COMPRESSED-FORMAT DETECTION IS NOW DIAGNOSTICS, NOT A DECISION.
 * `detectCompressedFormat()` still recognises gzip/zstd/xz/zip/7z/lz4/bzip2/
 * `.Z`/lzop/lzip/RAR/zstd-skippable/tar, and `COMPRESSED_EXTENSIONS` still
 * lists the usual suffixes — but neither decides pass/fail any more. Their
 * only jobs are (a) to make the error message say "this looks like gzip"
 * instead of a bare "unscannable", and (b) `COMPRESSED_EXTENSIONS` acts as a
 * narrow one-way veto on the SCAN branch: a file *named* `.br`/`.gz`/`.zip`/…
 * is never scanned even if its bytes happen to decode cleanly. That veto can
 * only ever ADD failures, never remove one, and it exists for the one
 * residual the text test cannot cover on its own — a compressed stream whose
 * bytes coincidentally form valid UTF-8 with no NULs. (Vanishingly unlikely
 * at bundle size: compressed output is high-entropy, and the probability that
 * ~40 KB of it is valid UTF-8 is astronomically small. But it costs nothing
 * to keep, and REMOVING a fail-closed signal is literally how escape #4
 * happened. Entries are only ever added.)
 *
 * WHY "VALID UTF-8 AND NO NUL BYTES" IS THE RIGHT TEXT TEST. Every format
 * this guard has ever lost to fails it, for reasons intrinsic to the format
 * rather than to anyone's memory:
 *   • gzip (`1f 8b`), zstd (`28 b5 2f fd`), xz (`fd 37 …`), 7z (`37 7a bc af`),
 *     `.Z` (`1f 9d`) and lzop (`89 4c 5a 4f`) all place a UTF-8 continuation
 *     or otherwise-illegal byte in the first two bytes — invalid UTF-8 at
 *     offset 0 or 1, before any list is consulted.
 *   • zip, RAR, tar, ICO, WOFF and every length-prefixed container pad their
 *     headers with NUL bytes.
 *   • Brotli, raw DEFLATE and zlib have no magic number at all — the reason
 *     the old model needed an extension list — but their high-entropy output
 *     is not valid UTF-8 either, so the *content* test catches what no
 *     *signature* table could.
 *   • UTF-16/UTF-32 text is full of NULs, so a UTF-16LE bundle carrying a CDN
 *     import — which the old model happily "scanned" and found nothing in —
 *     now fails closed.
 * NUL is the discriminator because it is the one byte that never appears in
 * real UTF-8 text and always appears in binary containers and wide-encoded
 * text.
 *
 * PATTERN MATCHING IS CANONICALIZATION, NOT ENUMERATION (issues #110, #105).
 * The same lesson the file-selection inversion taught applies one level
 * down: `URL_PATTERN` used to recognise exactly one surface spelling of a
 * scheme, so every *other* spelling — uppercase (#105), a JSON/JS `\/`
 * escape, a `\x`/`\u` string escape, an HTML numeric entity — passed
 * unseen. Rather than adding one regex alternative per obfuscation shape
 * found (the list-patching move that took five rounds to abandon on the
 * file-selection side), `canonicalizeForScan()` decodes text through the
 * closed set of transforms a scheme survives in this toolchain's outputs
 * BEFORE the pattern runs, so the pattern only ever needs to recognise the
 * canonical spelling. See `canonicalizeForScan`'s own doc comment for the
 * transform order and for the two shapes that remain genuinely out of
 * reach — concatenation and runtime-computed strings (`fromCharCode`,
 * `atob`) — which are not encodings of a literal at all, and so are not a
 * pattern-matching problem no matter how the pattern is built.
 *
 * KNOWN-BINARY SIGNATURES ARE STRUCTURALLY VALIDATED WHEN THEY ARE SHORT
 * (issue #121). A signature match alone licenses a `skip`, and `skip` files
 * are only cleartext-scanned — so a signature short enough to appear by
 * chance (ICO/CUR/TTF's 4-byte magics) could be worn over an arbitrary,
 * e.g. compressed, payload with nothing to catch it. The fix is not one
 * more byte of magic (there is no more real-format magic to add — that is
 * the whole reason these are 4 bytes) but a `validate` step alongside the
 * short signatures that checks the bytes are shaped like the *rest* of the
 * format (an ICONDIR with a real directory entry, an sfnt table directory
 * that fits the buffer), not just its first four bytes. Strong signatures
 * (8 bytes, or bytes no valid UTF-8 could ever produce) have no `validate`
 * and need none — see `matchesSignature`.
 *
 * THE RESIDUAL, STATED HONESTLY. What this model still cannot see, now the
 * *only* blind spots rather than an open-ended list:
 *   1. A reference assembled at runtime rather than written as a literal —
 *      string concatenation (`"htt"+"ps://…"`), `String.fromCharCode(...)`,
 *      a base64 `data:`/`atob()` payload. No static text transform resolves
 *      these without executing the code; see `canonicalizeForScan`.
 *   2. A URL inside a file on `KNOWN_BINARY_SIGNATURES` that is not present
 *      in cleartext (e.g. a URL inside a compressed PNG `zTXt` chunk). The
 *      cleartext byte-scan in `scanDist` covers the uncompressed case; the
 *      compressed-metadata case is accepted, because an image cannot execute
 *      a fetch and the allowlist is short enough to reason about.
 *
 * SKIPPED FILES MAY CARRY INERT, SPEC-MANDATED METADATA URIS (issue #120).
 * The cleartext backstop on `skip`-dispositioned files exists to catch a
 * URL smuggled into a binary asset — but standard image metadata (XMP's RDF
 * namespace, ICC profile fields, PNG `tEXt`/`iTXt`) is REQUIRED by its
 * spec to contain URI-shaped strings that are never fetched. Conflating
 * "looks like a URL" with "is a fetch target" false-positives on any
 * ordinary designer-exported asset. `METADATA_NAMESPACE_ALLOWLIST` (distinct
 * from the URL `ALLOWLIST` above it) exempts exactly that: it is consulted
 * ONLY for `skip`-dispositioned files, never for `scan`-dispositioned ones,
 * so it cannot weaken the strict JS/text scan the way widening the URL
 * `ALLOWLIST` would (see that allowlist's own warning below).
 *
 * ALLOWLIST (URL): three narrow, exact exceptions for inert strings baked
 * into audited third-party dependencies (React, workbox-window) that this
 * project does not author and cannot edit. Each is a literal used for
 * developer-facing diagnostic text, an XML namespace identifier, or a
 * console warning — never a network fetch target, never rendered as a link,
 * never attacker-influenced. Read the comment on each entry before adding
 * a new one; this list is meant to stay short and justified, not grow into
 * a rubber stamp.
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { basename, join } from "node:path";
import { fileURLToPath } from "node:url";

// The terminator class excludes whitespace, quoting/bracket punctuation, and
// C0 control bytes (`\x00`-`\x1f`, `\x7f`) — the latter matters for the
// cleartext byte-scan of `skip`-dispositioned binaries (issue #120): a URL
// sitting in a PNG tEXt/XMP chunk is immediately followed by the chunk's CRC
// and the next chunk's own binary header, which is exactly where control
// bytes show up in an otherwise-cleartext region. Without excluding them the
// match run-on swallows that trailing binary noise into the "URL", which
// then fails an exact-match allowlist entry for a reason that has nothing to
// do with the namespace URI itself.
// oxlint(eslint/no-control-regex): intentional — see the comment above.
const URL_PATTERN = /https?:\/\/[^\s"'`)<>\\\x00-\x1f\x7f]+/gi; // eslint-disable-line no-control-regex

/**
 * NORMALIZATION, NOT ENUMERATION — the fix for issues #110 and #105.
 *
 * #105 and #110 found the same disease this module's header already names for
 * file selection: `URL_PATTERN` recognised exactly one surface spelling of a
 * scheme (`https://` and `http://`, lowercase, contiguous, unescaped), so
 * every OTHER spelling of the same bytes — uppercase, JSON's `\/` escape, a
 * JS `\x`/`\u` string escape, an HTML numeric character reference — passed
 * unseen. Adding one more regex alternative per shape found is the list-
 * patching move this guard has already lost five times on the file-selection
 * side; it does not converge, because the set of encodings is unbounded in
 * exactly the way the set of opaque formats was.
 *
 * So this does not enumerate encodings. It CANONICALIZES: every text file is
 * decoded through the small, closed set of transforms a URL scheme survives
 * in this toolchain's outputs (case folding, HTML entity decoding, JS string
 * escape decoding, JSON solidus-escape decoding) BEFORE `URL_PATTERN` ever
 * runs, so the pattern only ever has to recognise one spelling: the
 * canonical one. A new obfuscation shape built from these same primitives
 * (nest an entity inside a hex escape, mix case with an escaped slash, ...)
 * is caught for free, because canonicalization composes; a genuinely new
 * *primitive* (a shape none of these transforms touches) is the same kind of
 * residual the module header already documents for #110 — recorded honestly
 * below, not silently claimed away.
 *
 * Two shapes are explicitly NOT handled here, and are not regressions to
 * "fix" — they are not encodings of a literal at all, they are values
 * assembled at *runtime*, which no static text scan (canonicalizing or not)
 * can resolve without a JS interpreter:
 *   - string concatenation (`"htt" + "ps://…"`) — the literal `https://`
 *     never appears in the source text at all, encoded or otherwise.
 *   - `String.fromCharCode(...)` / `atob()` / base64 `data:` payloads — the
 *     scheme is computed, not written.
 * These remain documented residuals (see the module header's "THE RESIDUAL,
 * STATED HONESTLY"), not silent gaps: the difference between "we decode what
 * you wrote" and "we execute what you wrote" is the line this guard draws.
 *
 * CANONICALIZATION ORDER — HTML entities first, then JS escapes, then
 * solidus-unescaping, then case folding — matters because entities and JS
 * escapes can each produce characters (`\`, `&`, digits) the other stage
 * consumes; running HTML-entity decoding last would leave an entity-encoded
 * backslash unavailable to the JS-escape stage, for instance. The order here
 * is the one that lets each stage feed the next, which is what makes nested/
 * combined obfuscation (e.g. an entity-encoded hex escape) fall out for free
 * rather than needing its own case.
 */
function canonicalizeForScan(text) {
	let out = text;

	// HTML numeric character references: &#104; (decimal) / &#x68; (hex).
	// Bounded to a small numeric run so this cannot runaway-match arbitrary
	// text; every legitimate use (entity-encoded ASCII) fits comfortably.
	out = out.replace(/&#x([0-9a-f]{1,6});/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)));
	out = out.replace(/&#([0-9]{1,7});/g, (_, dec) => String.fromCodePoint(parseInt(dec, 10)));

	// JS string escapes: \xHH (hex byte) and \uHHHH / \u{H...} (unicode).
	// These are what a minifier or an adversary writes to keep a literal out
	// of a naive string scan while still producing it at parse time.
	out = out.replace(/\\x([0-9a-f]{2})/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)));
	out = out.replace(/\\u\{([0-9a-f]{1,6})\}/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)));
	out = out.replace(/\\u([0-9a-f]{4})/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)));

	// JSON/JS-escaped solidus: `\/` is a legal (and common — several
	// serializers/minifiers emit it by default) encoding of a literal `/`.
	out = out.replace(/\\\//g, "/");

	return out;
}

/**
 * THE SKIP ALLOWLIST — binary asset types this build legitimately emits,
 * each recognised by its magic bytes and NEVER by its filename.
 *
 * Keep this list short and every entry justified. Adding one is the
 * operator's escape hatch when a new binary asset type genuinely belongs in
 * `dist/`, and it is meant to be a visible, reviewable line in a diff.
 * Anything not here fails the build, which is the correct direction to be
 * wrong in: a false failure costs one commit, a false pass ships a CDN
 * reference into an air-gapped enclave.
 *
 * `parts` is a list of `{ offset, bytes }` fragments that must all match, so
 * container formats whose identifying bytes are not at offset 0 (RIFF/WebP,
 * ISO-BMFF/AVIF) can be recognised properly instead of by a weak prefix.
 * `null` inside `bytes` is a wildcard for a byte whose value is variable.
 *
 * DELIBERATELY ABSENT, and why — each of these fails the build today:
 *   • BMP — its entire signature is the two ASCII bytes `BM`. That is too
 *     weak to license a skip; anything starting "BM…" would inherit it.
 *     Convert a BMP to PNG or WebP (a Vite `dist/` has no reason to carry
 *     one), or add it here with that trade-off written down.
 *   • EOT — the Embedded OpenType header is a length/version record with no
 *     signature at all, so there is nothing to verify. It is an IE8-era
 *     format Vite does not emit; ship WOFF2.
 *   • Audio/video (MP4, WebM, MP3, OGG) — not emitted by this build today.
 *     Add the one you actually ship, with its magic, when you ship it.
 */
const KNOWN_BINARY_SIGNATURES = [
	// ---- Images ----
	{
		format: "png",
		// PNG signature, RFC 2083 §3.1 — the 0x89 lead byte exists precisely
		// to make a PNG fail a text test, which is also why it can never be
		// confused with a scannable file.
		parts: [{ offset: 0, bytes: [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a] }],
	},
	{
		format: "jpeg",
		// SOI marker + the first marker byte (ITU-T T.81). 0xff is never a
		// legal UTF-8 byte, so this too is unambiguously binary.
		parts: [{ offset: 0, bytes: [0xff, 0xd8, 0xff] }],
	},
	{
		format: "gif",
		// "GIF8" + ("7a" | "9a"). Note this header IS ASCII, which is exactly
		// why the text test runs FIRST: a hand-crafted "GIF89a…https://evil…"
		// file that is valid UTF-8 gets scanned, not skipped. See
		// `classifyDistFile`.
		parts: [{ offset: 0, bytes: [0x47, 0x49, 0x46, 0x38, null, 0x61] }],
	},
	{
		format: "webp",
		// RIFF container: "RIFF" <uint32 size> "WEBP" (WebP container spec).
		parts: [
			{ offset: 0, bytes: [0x52, 0x49, 0x46, 0x46] },
			{ offset: 8, bytes: [0x57, 0x45, 0x42, 0x50] },
		],
	},
	{
		// ISO-BMFF box: <uint32 size> "ftyp" then the brand. AVIF images use
		// the `avif` brand, image sequences `avis` (ISO/IEC 23000-22 §10.1).
		// Spelled out rather than wildcarded so the allowlist stays exact.
		format: "avif",
		parts: [{ offset: 4, bytes: [0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66] }],
	},
	{
		format: "avif (sequence)",
		parts: [{ offset: 4, bytes: [0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x73] }],
	},
	{
		// ICONDIR: reserved(0x0000) + type, little-endian — 1 = icon, 2 =
		// cursor. Both types spelled out; a wildcard here would skip any file
		// opening `00 00 ?? 00`, which is far too much of the binary space to
		// license from a four-byte match. Issue #121: the 4-byte magic alone
		// still licenses a skip over an arbitrary (e.g. compressed) payload
		// appended after it, so `validate` additionally requires a structurally
		// plausible ICONDIR — at least one image directory entry, sized to fit.
		format: "ico",
		parts: [{ offset: 0, bytes: [0x00, 0x00, 0x01, 0x00] }],
		validate: (buffer) => validateIconDir(buffer),
	},
	{
		format: "cur",
		parts: [{ offset: 0, bytes: [0x00, 0x00, 0x02, 0x00] }],
		validate: (buffer) => validateIconDir(buffer),
	},
	// ---- Fonts ----
	{ format: "woff", parts: [{ offset: 0, bytes: [0x77, 0x4f, 0x46, 0x46] }] }, // "wOFF"
	{ format: "woff2", parts: [{ offset: 0, bytes: [0x77, 0x4f, 0x46, 0x32] }] }, // "wOF2"
	{ format: "otf", parts: [{ offset: 0, bytes: [0x4f, 0x54, 0x54, 0x4f] }] }, // "OTTO"
	{
		// sfnt version 1.0. Issue #121: like ICO/CUR, this 4-byte magic alone
		// licenses a skip over anything that follows. `validate` requires a
		// plausible sfnt table directory — `numTables` in a sane range and
		// enough bytes for the directory it claims to have.
		format: "ttf",
		parts: [{ offset: 0, bytes: [0x00, 0x01, 0x00, 0x00] }],
		validate: (buffer) => validateSfntTableDirectory(buffer),
	},
	{ format: "ttc", parts: [{ offset: 0, bytes: [0x74, 0x74, 0x63, 0x66] }] }, // "ttcf"
	// ---- Other ----
	{
		format: "wasm",
		// "\0asm" + version 1 (WebAssembly core spec §5.5.16).
		parts: [{ offset: 0, bytes: [0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00] }],
	},
];

/**
 * DIAGNOSTICS ONLY — this table no longer decides anything. Its sole job is
 * to turn "unscannable" into "this looks like gzip", so the operator reading
 * a failed build knows what to do about it.
 *
 * That is a deliberate demotion. Under the old model this table WAS the
 * decision, which meant a format missing from it was a silent pass; the
 * reviewer of PR #88 round 2 duly found five (`.Z`, lzop, lzip, RAR, zstd
 * skippable frames) plus `.tar`, all of which are now listed here — and all
 * of which would fail the build anyway if they were not, because failing is
 * the default. Adding a format here improves an error message. It cannot
 * change a verdict, in either direction.
 *
 * `parts` has the same shape as `KNOWN_BINARY_SIGNATURES` (offset + bytes,
 * `null` = wildcard).
 */
const COMPRESSED_SIGNATURES = [
	{ format: "gzip", parts: [{ offset: 0, bytes: [0x1f, 0x8b] }] },
	{ format: "Unix compress (.Z)", parts: [{ offset: 0, bytes: [0x1f, 0x9d] }] },
	{ format: "zstd", parts: [{ offset: 0, bytes: [0x28, 0xb5, 0x2f, 0xfd] }] },
	{
		// RFC 8878 §3.1.2: a skippable frame's magic is 0x184D2A5? — the low
		// nibble of the first byte is free, so 0x50..0x5f all start one. Real
		// tools emit these to carry side data alongside compressed frames.
		format: "zstd skippable frame",
		parts: [{ offset: 1, bytes: [0x2a, 0x4d, 0x18] }],
		firstByteRange: [0x50, 0x5f],
	},
	{ format: "xz", parts: [{ offset: 0, bytes: [0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00] }] },
	{ format: "7z", parts: [{ offset: 0, bytes: [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c] }] },
	{ format: "lz4", parts: [{ offset: 0, bytes: [0x04, 0x22, 0x4d, 0x18] }] },
	{ format: "lzop", parts: [{ offset: 0, bytes: [0x89, 0x4c, 0x5a, 0x4f] }] },
	{ format: "lzip", parts: [{ offset: 0, bytes: [0x4c, 0x5a, 0x49, 0x50] }] },
	{ format: "bzip2", parts: [{ offset: 0, bytes: [0x42, 0x5a, 0x68] }] },
	{ format: "rar", parts: [{ offset: 0, bytes: [0x52, 0x61, 0x72, 0x21, 0x1a, 0x07] }] },
	{ format: "zip", parts: [{ offset: 0, bytes: [0x50, 0x4b, 0x03, 0x04] }] },
	{ format: "zip (empty archive)", parts: [{ offset: 0, bytes: [0x50, 0x4b, 0x05, 0x06] }] },
	{ format: "zip (spanned)", parts: [{ offset: 0, bytes: [0x50, 0x4b, 0x07, 0x08] }] },
	// POSIX/GNU tar writes "ustar" at offset 257 of the first header block. A
	// tar has no offset-0 magic at all, which is why it needed the fallthrough
	// bucket to be removed rather than one more entry added.
	{ format: "tar", parts: [{ offset: 257, bytes: [0x75, 0x73, 0x74, 0x61, 0x72] }] },
];

/**
 * Suffixes that veto the SCAN branch by name alone (see the module header,
 * "COMPRESSED-FORMAT DETECTION IS NOW DIAGNOSTICS"). A file whose name ends
 * with one of these is never scanned, whatever its bytes decode to.
 *
 * This is a ONE-WAY signal: it can only ever move a file from `scan` to
 * `fail`, never the reverse. It is not the mechanism any more — the text test
 * is — it is a backstop for a compressed stream that coincidentally decodes
 * as clean UTF-8. Entries are only ever added; removing one is how PR #88
 * round 1 reopened the hole.
 *
 * Matched against the lowercased BASENAME with `endsWith`, not `extname()`.
 * `extname(".gz")` is the empty string in Node, so an `extname`-based check
 * misses a file named exactly `.gz` or `.br` (PR #88 round-2 review, finding
 * 2). Such a file fails on its content here regardless, but the name signal
 * should not have a hole in it either.
 */
const COMPRESSED_EXTENSIONS = [
	".br",
	".gz",
	".tgz",
	".zip",
	".zst",
	".zstd",
	".xz",
	".bz2",
	".7z",
	".lz4",
	".lz",
	".lzo",
	".lzma",
	".zz",
	".deflate",
	".rar",
	".tar",
	".z",
];

/**
 * ICO/CUR structural check (issue #121). Beyond the 4-byte ICONDIR magic,
 * requires: at least one directory entry (`idCount >= 1`), and that entry's
 * declared image offset/size stay inside the buffer. This does not fully
 * parse the image data — that would just move the enumeration problem inside
 * the format — it only asks whether the bytes are shaped like a real ICONDIR
 * at all, which an arbitrary payload glued on after 4 magic bytes is not.
 */
function validateIconDir(buffer) {
	const HEADER = 6; // reserved(2) + type(2) + count(2)
	const ENTRY = 16;
	if (buffer.length < HEADER) {
		return false;
	}
	const count = buffer.readUInt16LE(4);
	if (count < 1 || buffer.length < HEADER + ENTRY) {
		return false;
	}
	const entryOffset = buffer.readUInt32LE(HEADER + 12);
	const entrySize = buffer.readUInt32LE(HEADER + 8);
	if (entrySize === 0) {
		return false;
	}
	// Guard the addition itself against overflow-style wraparound before
	// comparing against buffer.length.
	if (entryOffset > buffer.length || entrySize > buffer.length) {
		return false;
	}
	return entryOffset + entrySize <= buffer.length;
}

/**
 * TTF/sfnt structural check (issue #121). Beyond the 4-byte version magic,
 * requires a plausible sfnt table directory: `numTables` in a sane range,
 * and the buffer long enough to actually hold that many 16-byte table
 * records. Real fonts satisfy this trivially; a payload wearing the sfnt
 * magic as a 4-byte prefix over unrelated (e.g. compressed) bytes does not,
 * because those bytes are not shaped like a table directory at all.
 */
function validateSfntTableDirectory(buffer) {
	const HEADER = 12; // version(4) + numTables(2) + searchRange(2) + entrySelector(2) + rangeShift(2)
	const RECORD = 16;
	if (buffer.length < HEADER) {
		return false;
	}
	const numTables = buffer.readUInt16BE(4);
	// A real font has at least a handful of tables (cmap/glyf/head/hmtx/...)
	// and, per the sfnt spec, not an unbounded number. 1..64 comfortably
	// covers every real font while still requiring real structure.
	if (numTables < 1 || numTables > 64) {
		return false;
	}
	return buffer.length >= HEADER + numTables * RECORD;
}

function matchesSignature(buffer, signature) {
	if (signature.firstByteRange) {
		const [low, high] = signature.firstByteRange;
		if (buffer.length === 0 || buffer[0] < low || buffer[0] > high) {
			return false;
		}
	}
	const magicMatches = signature.parts.every(({ offset, bytes }) => {
		if (buffer.length < offset + bytes.length) {
			return false;
		}
		return bytes.every((byte, i) => byte === null || buffer[offset + i] === byte);
	});
	if (!magicMatches) {
		return false;
	}
	// Issue #121: a magic-byte match alone is not always enough to license a
	// skip. Weak (short) signatures carry an additional structural `validate`
	// — see KNOWN_BINARY_SIGNATURES — that must also pass; strong signatures
	// (8 bytes, or bytes no valid UTF-8 could produce) have none and rely on
	// the magic match alone, which is already unambiguous.
	return signature.validate ? signature.validate(buffer) : true;
}

/**
 * Returns the name of the known-binary asset format `buffer` opens with
 * (`"png"`, `"woff2"`, …), or `null`. Pure byte inspection — no filename is
 * involved, so renaming a file can neither hide it from nor sneak it into the
 * skip allowlist.
 */
export function detectKnownBinaryFormat(buffer) {
	for (const signature of KNOWN_BINARY_SIGNATURES) {
		if (matchesSignature(buffer, signature)) {
			return signature.format;
		}
	}
	return null;
}

/**
 * DIAGNOSTIC. Returns a human-readable compressed/archive format name if
 * `buffer` looks like one, else `null`. Used only to explain a failure — it
 * has no vote in `classifyDistFile`, by design (see the module header).
 */
export function detectCompressedFormat(buffer) {
	for (const signature of COMPRESSED_SIGNATURES) {
		if (matchesSignature(buffer, signature)) {
			return signature.format;
		}
	}
	return null;
}

/**
 * DIAGNOSTIC + one-way scan veto. Returns the matched suffix when `file`'s
 * lowercased basename ends with a `COMPRESSED_EXTENSIONS` entry, else `null`.
 * Deliberately NOT `extname()`-based, so a file named exactly `.gz` is caught.
 */
export function matchCompressedExtension(file) {
	const name = basename(file).toLowerCase();
	for (const ext of COMPRESSED_EXTENSIONS) {
		if (name.endsWith(ext)) {
			return ext;
		}
	}
	return null;
}

const UTF8_DECODER = new TextDecoder("utf-8", { fatal: true });

/**
 * True when `buffer` is *confidently text*: it decodes as valid UTF-8 and
 * contains no NUL bytes. This is the whole scannability criterion — see the
 * module header for why these two conditions are the right ones and what
 * every format the guard has lost to does about them.
 */
export function isDecodableText(buffer) {
	if (buffer.includes(0)) {
		return false;
	}
	try {
		UTF8_DECODER.decode(buffer);
		return true;
	} catch {
		return false;
	}
}

/**
 * THE ONE PREDICATE. Every file under `dist/` gets its disposition from this
 * function and from nowhere else — `scanDist()`, which is what the CLI runs,
 * calls it per file and does not re-derive any part of the answer.
 * `check-no-external-assets.test.mjs` asserts that structurally and
 * behaviourally, because a property test pointed at a function the CLI does
 * not run is what let PR #88 round 1's regression ship.
 *
 * Returns `{ disposition, format, reason }` where `disposition` is:
 *   - `"scan"` — confidently text; inspect it for external references.
 *   - `"skip"` — content matches the known-binary allowlist; do not treat as
 *     opaque. (`scanDist` still byte-scans it for a cleartext URL.)
 *   - `"fail"` — anything else. `reason` explains why in operator terms.
 *
 * ORDER MATTERS, and text comes first on purpose. Several allowlisted
 * signatures are ASCII (`GIF89a`, `wOFF`, `OTTO`, `ttcf`), so a hand-crafted
 * file that opens with one and continues as valid UTF-8 carrying
 * `https://cdn.evil.example/x.js` would be SKIPPED if the binary check ran
 * first. Testing text first means such a file is scanned and the URL is
 * caught; a genuine PNG/JPEG/WOFF/ICO/WASM is never valid UTF-8 (each puts an
 * illegal byte or a NUL in its first four bytes), so nothing that should be
 * skipped is scanned instead.
 */
export function classifyDistFile(file, buffer) {
	const compressedName = matchCompressedExtension(file);

	if (isDecodableText(buffer)) {
		if (compressedName) {
			return {
				disposition: "fail",
				format: null,
				reason:
					`named "*${compressedName}", which claims a compressed/opaque artifact — not scanned even though its ` +
					`bytes happen to decode as text. Rename the file if it is genuinely plain text.`,
			};
		}
		return { disposition: "scan", format: "text", reason: "valid UTF-8, no NUL bytes" };
	}

	const binaryFormat = detectKnownBinaryFormat(buffer);
	if (binaryFormat) {
		return { disposition: "skip", format: binaryFormat, reason: `recognised ${binaryFormat} asset (magic bytes)` };
	}

	const compressedFormat = detectCompressedFormat(buffer);
	if (compressedFormat) {
		return {
			disposition: "fail",
			format: compressedFormat,
			reason: `looks like ${compressedFormat} — compressed/archived content this guard cannot read`,
		};
	}
	if (compressedName) {
		return {
			disposition: "fail",
			format: null,
			reason: `not decodable as text, and named "*${compressedName}" — presumed compressed/opaque`,
		};
	}
	return {
		disposition: "fail",
		format: null,
		reason: buffer.includes(0)
			? "not scannable: contains NUL bytes (binary container, or UTF-16/UTF-32 text) and matches no known-binary signature"
			: "not scannable: not valid UTF-8 and matches no known-binary signature",
	};
}

export const ALLOWLIST = [
	{
		pattern: /^https:\/\/react\.dev\/errors\//,
		reason:
			"React production build's own minified-invariant error-decoder link — interpolated into a thrown Error's " +
			"message for a developer reading a stack trace/console. Never fetched by the app; baked into react-dom " +
			"itself; cannot be removed without patching a vendored dependency.",
	},
	{
		// Exactly the four namespace URIs actually present in the build, matched
		// whole (`$`-anchored) rather than by origin prefix — allowing all of
		// `http://www.w3.org/` would exempt any future w3.org URL, including a
		// real fetchable one.
		pattern: /^http:\/\/www\.w3\.org\/(1999\/xlink|2000\/svg|1998\/Math\/MathML|XML\/1998\/namespace)$/,
		reason:
			"The four XML/SVG/MathML namespace URIs used by react-dom's attribute/property tables to set namespaced " +
			"DOM attributes (xlink:href, xml:lang, SVG/MathML elements). These are XML namespace *names*, a syntactic " +
			"identifier the XML/DOM spec requires to look like a URI — never dereferenced over the network.",
	},
	{
		// `$`-anchored, not a prefix: for a URL shortener the path IS the
		// resource identity, so `bit.ly/wb-precache-evil-shortlink` is a
		// *different* shortlink that can resolve anywhere — unlike the w3.org
		// entry above (authority pinned, only the path varies), a prefix match
		// here would allowlist arbitrary destinations. The workbox string is a
		// fixed literal, so anchoring costs nothing.
		pattern: /^https:\/\/bit\.ly\/wb-precache$/,
		reason:
			"workbox-window's console.warn() message, shown only if the precache manifest fails to install. " +
			"Printed diagnostic text, never fetched automatically.",
	},
];

/**
 * METADATA-NAMESPACE ALLOWLIST (issue #120) — distinct from `ALLOWLIST` above,
 * and consulted ONLY inside `scanDist`'s cleartext backstop for `skip`-
 * dispositioned files (see `classifyDistFile`'s "skip" case). NEVER consulted
 * for `scan`-dispositioned (JS/CSS/HTML/...) text, which is why this is its
 * own list rather than an addition to `ALLOWLIST`: an entry here exempts a
 * namespace URI only when it turns up inside a binary asset's metadata, not
 * everywhere in the bundle. That scoping is the point — the #120 failure mode
 * this replaces was the error message telling operators to add the XMP/RDF
 * namespace URI to the GLOBAL url `ALLOWLIST`, which would have exempted it
 * inside application JavaScript too, a genuine weakening of the guard.
 *
 * Seeded with the handful of inert namespace URIs standard image encoders
 * write by specification, never dereferenced over the network — the same
 * "syntactic identifier that must look like a URI" justification as the
 * w3.org entries in `ALLOWLIST`. Exact literal match ($-anchored), same
 * reasoning as that list: a prefix match would allowlist more than the
 * specific namespace name.
 */
export const METADATA_NAMESPACE_ALLOWLIST = [
	{
		pattern: /^http:\/\/www\.w3\.org\/1999\/02\/22-rdf-syntax-ns#$/,
		reason: "RDF/XML namespace URI (W3C RDF Syntax spec) — mandatory root of every XMP packet.",
	},
	{
		pattern: /^http:\/\/purl\.org\/dc\/elements\/1\.1\/$/,
		reason: "Dublin Core namespace URI — the metadata vocabulary XMP uses for title/creator/description fields.",
	},
	{
		pattern: /^http:\/\/ns\.adobe\.com\/xap\/1\.0\/$/,
		reason: "Adobe XMP namespace URI — written by any encoder (not just Adobe's) that embeds an XMP packet.",
	},
	{
		pattern: /^http:\/\/ns\.adobe\.com\/tiff\/1\.0\/$/,
		reason: "Adobe XMP TIFF namespace URI — camera/scanner metadata fields (make, model, orientation) in XMP.",
	},
	{
		pattern: /^http:\/\/ns\.adobe\.com\/exif\/1\.0\/$/,
		reason: "Adobe XMP EXIF namespace URI — photographic metadata fields (exposure, ISO, ...) in XMP.",
	},
	{
		pattern: /^http:\/\/ns\.adobe\.com\/photoshop\/1\.0\/$/,
		reason: "Adobe XMP Photoshop namespace URI — written by any encoder using Adobe's Photoshop metadata schema.",
	},
];

function isAllowlistedMetadataNamespace(url) {
	return METADATA_NAMESPACE_ALLOWLIST.find((entry) => entry.pattern.test(normalizeAuthorityCase(url)));
}

/**
 * Lowercases only the scheme and host of a matched URL, leaving the path
 * untouched — RFC 3986 §3.1/§3.2.2 make scheme and host case-insensitive
 * (`HTTPS://REACT.DEV/errors/` and `https://react.dev/errors/` are the same
 * resource), but the path is not (`/errors/` and `/ERRORS/` need not be).
 * `ALLOWLIST` patterns are deliberately exact on the path, so folding the
 * whole URL to lowercase before testing them would let a case-varied path
 * smuggle past an anchored lowercase pattern — the exact risk #105 flags.
 * `URL_PATTERN` is itself `i`-flagged so `HTTPS://` is matched at all; this
 * is what keeps the allowlist's path exactness meaningful once it is.
 */
function normalizeAuthorityCase(url) {
	return url.replace(/^([a-z]+):\/\/([^/\\]*)/i, (_, scheme, host) => `${scheme.toLowerCase()}://${host.toLowerCase()}`);
}

function isAllowlisted(url) {
	const normalized = normalizeAuthorityCase(url);
	return ALLOWLIST.find((entry) => entry.pattern.test(normalized));
}

function* walk(dir) {
	for (const entry of readdirSync(dir)) {
		const full = join(dir, entry);
		const stat = statSync(full);
		if (stat.isDirectory()) {
			yield* walk(full);
		} else {
			yield full;
		}
	}
}

/**
 * Scans `distDir` and returns
 * `{ violations, allowlisted, scanned, skipped, unscannable }`.
 *
 * - `violations`/`allowlisted` are `{ file, url }`.
 * - `scanned` is the file paths that were confidently text.
 * - `skipped` is `{ file, format }` for known-binary assets.
 * - `unscannable` is `{ file, reason }` — a NON-EMPTY `unscannable` fails the
 *   build. `main()` is the one that decides that, so this stays a pure,
 *   unit-testable function with no `process.exit`.
 *
 * EVERY disposition comes from `classifyDistFile()`. This function must not
 * re-derive any of it — the CLI path and the tested path being different code
 * is the drift vector that shipped PR #88 round 1's regression, and
 * `check-no-external-assets.test.mjs` asserts this routing directly.
 *
 * Known-binary files are byte-scanned for a *cleartext* URL even though they
 * are skipped. The skip is a licence not to fail for being opaque, not a
 * licence to ignore the bytes: a URL sitting uncompressed in a PNG `tEXt`
 * chunk or a font's name table is a real external reference, and pre-#77
 * `main` caught that shape only by accident (whenever the file happened not
 * to be on its extension skip list). False positives are not a practical
 * concern — the literal ASCII `http://` appearing by chance in compressed
 * image data is a ~1e-14 event per bundle.
 */
export function scanDist(distDir) {
	const violations = [];
	const allowlisted = [];
	const scanned = [];
	const skipped = [];
	const unscannable = [];

	// `isMetadataContext`: whether `text` came from a `skip`-dispositioned
	// binary (issue #120). Only then is METADATA_NAMESPACE_ALLOWLIST
	// consulted — a `scan`-dispositioned (JS/CSS/HTML/...) text file gets the
	// strict URL ALLOWLIST only, exactly as before, so this cannot widen what
	// application code is permitted to reference.
	const record = (file, text, isMetadataContext) => {
		for (const url of canonicalizeForScan(text).match(URL_PATTERN) ?? []) {
			if (isAllowlisted(url) || (isMetadataContext && isAllowlistedMetadataNamespace(url))) {
				allowlisted.push({ file, url });
			} else {
				violations.push({ file, url });
			}
		}
	};

	for (const file of walk(distDir)) {
		const buffer = readFileSync(file);
		const verdict = classifyDistFile(file, buffer);

		if (verdict.disposition === "fail") {
			unscannable.push({ file, reason: verdict.reason });
			continue;
		}
		if (verdict.disposition === "skip") {
			skipped.push({ file, format: verdict.format });
			// Cleartext-only backstop; see the doc comment above.
			record(file, buffer.toString("latin1"), true);
			continue;
		}
		scanned.push(file);
		record(file, buffer.toString("utf-8"), false);
	}

	return { violations, allowlisted, scanned, skipped, unscannable };
}

function main() {
	const distDir = process.argv[2] ?? "dist";
	let result;
	try {
		result = scanDist(distDir);
	} catch (err) {
		console.error(`check-no-external-assets: could not scan "${distDir}": ${err.message}`);
		console.error(`Did you run "vite build" first?`);
		process.exit(1);
	}

	if (result.unscannable.length > 0) {
		console.error(
			`\ncheck-no-external-assets: FAILED — ${result.unscannable.length} unscannable file(s) found in ${distDir}:`,
		);
		for (const { file, reason } of result.unscannable) {
			console.error(`  ${file}: ${reason}`);
		}
		console.error("\nThis guard is an ALLOWLIST of what it can inspect, not a denylist of formats it has heard of.");
		console.error("A file passes only if it is confidently text (valid UTF-8, no NUL bytes) and is then scanned, or");
		console.error("if its magic bytes match the short known-binary asset allowlist (PNG/JPEG/GIF/WebP/AVIF/ICO/WOFF/");
		console.error("WOFF2/OTF/TTF/TTC/WASM). Everything else fails, including every compression and archive format —");
		console.error("enumerating those can never converge, so they are not enumerated. Fix one of these ways:");
		console.error("  - compressed output (gzip/brotli/zstd/... from a precompression step): remove it, or teach this");
		console.error("    script to decompress-and-scan (node:zlib has brotli, gzip, deflate and zstd built in);");
		console.error("  - a genuine binary asset type this build now emits: add its magic bytes to");
		console.error("    KNOWN_BINARY_SIGNATURES in scripts/check-no-external-assets.mjs, with a written reason;");
		console.error("  - a text file with a misleading name (*.gz, *.br, ...): rename it;");
		console.error("  - UTF-16/UTF-32 text: re-encode it as UTF-8.");
		process.exit(1);
	}

	if (result.allowlisted.length > 0) {
		console.log(`check-no-external-assets: ${result.allowlisted.length} allowlisted vendor URL(s) skipped:`);
		const seen = new Set();
		for (const { url } of result.allowlisted) {
			// Issue #120: an allowlisted match can come from either list — the
			// strict URL ALLOWLIST (scan context) or METADATA_NAMESPACE_ALLOWLIST
			// (skip context). Check both rather than assuming the first.
			const entry = isAllowlisted(url) ?? isAllowlistedMetadataNamespace(url);
			if (entry && !seen.has(entry.pattern.source)) {
				seen.add(entry.pattern.source);
				console.log(`  - ${entry.pattern} — ${entry.reason}`);
			}
		}
	}

	if (result.violations.length > 0) {
		console.error(`\ncheck-no-external-assets: FAILED — ${result.violations.length} external URL(s) found in ${distDir}:`);
		for (const { file, url } of result.violations) {
			console.error(`  ${file}: ${url}`);
		}
		console.error("\nAir-gapped appliances cannot ship a build with external references (CLAUDE.md, ADR-0007).");
		console.error("Vendor the asset locally. If this URL was found inside a binary asset (PNG/JPEG/font/...) rather");
		console.error("than in application code, it is most likely embedded XMP/ICC/tEXt metadata from the tool that");
		console.error("exported it — strip metadata at export time (e.g. `oxipng --strip all`, `exiftool -all=`) rather");
		console.error("than editing an allowlist. If it is a genuinely inert vendored-library string in application code,");
		console.error("add a narrowly-scoped, justified entry to ALLOWLIST in scripts/check-no-external-assets.mjs; if it");
		console.error("is a genuinely inert metadata namespace URI, add it to METADATA_NAMESPACE_ALLOWLIST instead — never");
		console.error("to ALLOWLIST, which also exempts the URL inside application JavaScript.");
		process.exit(1);
	}

	console.log(
		`check-no-external-assets: OK — no external references found in ${distDir} ` +
			`(${result.scanned.length} file(s) scanned, ${result.skipped.length} known-binary file(s) skipped).`,
	);
}

// Only run as a CLI when invoked directly (`node scripts/check-no-external-assets.mjs`),
// not when imported by the self-test.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
	main();
}
