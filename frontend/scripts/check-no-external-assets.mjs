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
 * ALLOWLIST: three narrow, exact-prefix exceptions for inert strings baked
 * into audited third-party dependencies (React, workbox-window) that this
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

// Extensions that are meaningfully "text" for this check. Binary assets
// (PNG icons, etc.) are skipped — this project's own binary assets carry no
// such strings, and treating them as text buys nothing.
const SCANNED_EXTENSIONS = new Set([".html", ".js", ".css", ".json", ".webmanifest", ".svg", ".txt", ".map"]);

export const ALLOWLIST = [
	{
		pattern: /^https:\/\/react\.dev\/errors\//,
		reason:
			"React production build's own minified-invariant error-decoder link — interpolated into a thrown Error's " +
			"message for a developer reading a stack trace/console. Never fetched by the app; baked into react-dom " +
			"itself; cannot be removed without patching a vendored dependency.",
	},
	{
		pattern: /^http:\/\/www\.w3\.org\//,
		reason:
			"XML/SVG/MathML namespace URIs used by react-dom's attribute/property tables to set namespaced DOM " +
			"attributes (xlink:href, xml:lang, SVG/MathML elements). These are XML namespace *names*, a syntactic " +
			"identifier the XML/DOM spec requires to look like a URI — never dereferenced over the network.",
	},
	{
		pattern: /^https:\/\/bit\.ly\/wb-precache/,
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
 * Scans `distDir` and returns `{ violations, allowlisted }`, each an array
 * of `{ file, url }`. Pure function (no process.exit) so it's unit-testable.
 */
export function scanDist(distDir) {
	const violations = [];
	const allowlisted = [];

	for (const file of walk(distDir)) {
		if (!SCANNED_EXTENSIONS.has(extname(file))) {
			continue;
		}
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

	return { violations, allowlisted };
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

	console.log(`check-no-external-assets: OK — no external references found in ${distDir}.`);
}

// Only run as a CLI when invoked directly (`node scripts/check-no-external-assets.mjs`),
// not when imported by the self-test.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
	main();
}
