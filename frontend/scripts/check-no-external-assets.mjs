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
 * FILE SELECTION — fail closed, not open. Every file under dist/ is scanned
 * by default; only the extensions in SKIPPED_BINARY_EXTENSIONS below are
 * skipped. This is deliberately a denylist and not an allowlist: an
 * allowlist of "text" extensions silently exempts every file type nobody
 * thought of (`.mjs`, `.cjs`, `.xml`, and anything with no extension at
 * all), and `frontend/public/` is copied verbatim into `dist/` with
 * whatever names it contains — so an allowlist means a new emitted file
 * type ships unchecked until someone notices. A guard that fails open is
 * worse than no guard. The cost of the inversion is that a binary file not
 * on the denylist may decode to a byte sequence that looks like a URL and
 * fail the build; that is the safe direction to be wrong in, and the fix is
 * to add the extension here with a reason.
 *
 * COMPRESSED ARTIFACTS (.br/.gz/.zip) — fail closed, not skip. These are
 * NOT on SKIPPED_BINARY_EXTENSIONS, because they are not binary in the
 * sense that word is used above: Brotli/gzip/zip encode compressed *text*,
 * and a URL living in that text is exactly what this guard exists to find.
 * Scanning the compressed bytes as UTF-8 finds nothing (the match target is
 * gone once compressed) while reporting the same "OK" as a clean file —
 * which is worse than not scanning at all, because it *looks* thorough. So
 * a compressed artifact is neither scanned nor silently skipped: its mere
 * presence in dist/ fails the build (COMPRESSED_EXTENSIONS below), on the
 * same "never silently pass content it cannot inspect" principle as the
 * denylist above. The current Vite config emits none of these (no
 * compression plugin), so this costs nothing today; it exists so that
 * enabling `vite-plugin-compression` or an nginx `brotli_static`/
 * `gzip_static` precompression step later is a conscious decision — someone
 * has to come here and either wire up decompress-and-scan (Node's `zlib`
 * has both Brotli and gzip built in) or explicitly widen this list, not
 * discover months later that the guard had a blind spot.
 *
 * ALLOWLIST: three narrow, exact exceptions for inert strings baked into
 * audited third-party dependencies (React, workbox-window) that this
 * project does not author and cannot edit. Each is a literal used for
 * developer-facing diagnostic text, an XML namespace identifier, or a
 * console warning — never a network fetch target, never rendered as a link,
 * never attacker-influenced. Read the comment on each entry before adding
 * a new one; this list is meant to stay short and justified, not grow into
 * a rubber stamp. See the PR description's "Verification" section for the
 * self-test that proves this guard still fails on a real violation.
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { extname, join } from "node:path";
import { fileURLToPath } from "node:url";

const URL_PATTERN = /https?:\/\/[^\s"'`)<>\\]+/g;

/**
 * Known-binary extensions, skipped because decoding them as UTF-8 produces
 * noise rather than signal. EVERYTHING ELSE IS SCANNED — including
 * extensionless files, `.mjs`/`.cjs`, `.xml`, and any format added to
 * `public/` later. Only add an entry here for a format that genuinely
 * cannot carry a meaningful URL reference in its decoded text.
 */
const SKIPPED_BINARY_EXTENSIONS = new Set([
	// Images
	".png",
	".jpg",
	".jpeg",
	".gif",
	".webp",
	".avif",
	".ico",
	".bmp",
	// Fonts
	".woff",
	".woff2",
	".ttf",
	".otf",
	".eot",
	// Other binary payloads
	".wasm",
]);

/**
 * Compressed-text formats: NOT skipped, NOT scanned — their presence in
 * dist/ fails the build outright (see the COMPRESSED ARTIFACTS header
 * comment). Kept as a separate set from SKIPPED_BINARY_EXTENSIONS on
 * purpose, so the two can never be conflated again.
 */
const COMPRESSED_EXTENSIONS = new Set([".br", ".gz", ".zip"]);

/** True when `file` is a known-binary artifact this check deliberately skips.
 * Extension matching is case-insensitive; a file with no extension is never
 * skipped (that was the hole this replaced). */
export function isSkippedBinary(file) {
	return SKIPPED_BINARY_EXTENSIONS.has(extname(file).toLowerCase());
}

/** True when `file` is a compressed-text artifact (.br/.gz/.zip) this check
 * cannot inspect. Extension matching is case-insensitive. */
export function isCompressedArtifact(file) {
	return COMPRESSED_EXTENSIONS.has(extname(file).toLowerCase());
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

function isAllowlisted(url) {
	return ALLOWLIST.find((entry) => entry.pattern.test(url));
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
 * `{ violations, allowlisted, scanned, skipped, compressed }`.
 * `violations`/`allowlisted` are arrays of `{ file, url }`; `scanned`/
 * `skipped`/`compressed` are file paths, reported so the CLI can show what
 * the guard actually looked at rather than asking the reader to trust it.
 * `compressed` is never scanned and never counted as a violation of its own
 * — the caller (`main`) is the one that decides a non-empty `compressed`
 * fails the build, so this stays a pure function (no process.exit) and
 * unit-testable.
 */
export function scanDist(distDir) {
	const violations = [];
	const allowlisted = [];
	const scanned = [];
	const skipped = [];
	const compressed = [];

	for (const file of walk(distDir)) {
		if (isCompressedArtifact(file)) {
			compressed.push(file);
			continue;
		}
		if (isSkippedBinary(file)) {
			skipped.push(file);
			continue;
		}
		scanned.push(file);
		const content = readFileSync(file, "utf-8");
		const matches = content.match(URL_PATTERN) ?? [];
		for (const url of matches) {
			const entry = isAllowlisted(url);
			if (entry) {
				allowlisted.push({ file, url });
			} else {
				violations.push({ file, url });
			}
		}
	}

	return { violations, allowlisted, scanned, skipped, compressed };
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

	if (result.compressed.length > 0) {
		console.error(
			`\ncheck-no-external-assets: FAILED — ${result.compressed.length} compressed artifact(s) found in ${distDir}:`,
		);
		for (const file of result.compressed) {
			console.error(`  ${file}`);
		}
		console.error("\nBrotli/gzip/zip encode compressed text, and this guard cannot read inside them — scanning the");
		console.error("compressed bytes would report a false OK instead of the URL(s) they may carry. The build currently");
		console.error("emits none of these; if a precompression step was just added, either teach this script to");
		console.error("decompress-and-scan (node:zlib has both Brotli and gzip built in) or remove the compressed output.");
		process.exit(1);
	}

	if (result.allowlisted.length > 0) {
		console.log(`check-no-external-assets: ${result.allowlisted.length} allowlisted vendor URL(s) skipped:`);
		const seen = new Set();
		for (const { url } of result.allowlisted) {
			const entry = isAllowlisted(url);
			if (!seen.has(entry.pattern.source)) {
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
		console.error("Vendor the asset locally, or if this is a genuinely inert vendored-library string, add a");
		console.error("narrowly-scoped, justified entry to ALLOWLIST in scripts/check-no-external-assets.mjs.");
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
