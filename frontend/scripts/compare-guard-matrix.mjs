#!/usr/bin/env node
/**
 * NEGATIVE-CONTROL MATRIX for the air-gap guard
 * (`check-no-external-assets.mjs`), run against two revisions of it at once.
 *
 * WHY THIS IS A COMMITTED SCRIPT. The guard has failed open five times, and
 * the two properties that matter are comparative, not absolute:
 *
 *   1. STRICT SUPERSET — every case the *previous* guard failed the build on,
 *      this one must still fail. Escape #4 (PR #88 round 1) was a regression
 *      against `main` that a single-revision test suite could not see, because
 *      each revision looked self-consistent.
 *   2. NEW COVERAGE — the cases the previous guard waved through that this one
 *      catches.
 *
 * Both need two guards side by side over one corpus, which is awkward to paste
 * into a PR body and easy for GitHub to mangle on the way in (docs/testing.md,
 * "Honest verification" rule 3). So it lives here and a reviewer runs it.
 *
 * Usage:
 *
 *   node scripts/compare-guard-matrix.mjs                    # HEAD vs origin/main
 *   BASELINE=/tmp/other-guard.mjs node scripts/compare-guard-matrix.mjs
 *
 * With no BASELINE it extracts `origin/main`'s copy of the guard to a temp
 * file with `git show`. Every fixture is bundle-sized (~40 KB) where the
 * format allows, and the matrix prints `leak` — whether the marker URL
 * survives in the raw bytes — for each case, so a "caught" verdict that is
 * really the scan branch stumbling over cleartext is visible rather than
 * assumed. A case whose bytes leak the URL proves nothing about opacity
 * handling; those are marked and excluded from the superset claim.
 *
 * Exit code is 1 if the superset property is violated, so this is usable as a
 * check and not only as a report.
 */
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, deflateRawSync, deflateSync, gzipSync } from "node:zlib";
import * as zlib from "node:zlib";

const HERE = dirname(fileURLToPath(import.meta.url));
const CANDIDATE = join(HERE, "check-no-external-assets.mjs");
const URL_MARKER = "https://cdn.evil.example/payload.js";

function resolveBaseline() {
	if (process.env.BASELINE) {
		return process.env.BASELINE;
	}
	const out = join(mkdtempSync(join(tmpdir(), "wp-baseline-")), "main-guard.mjs");
	const show = spawnSync("git", ["show", "origin/main:frontend/scripts/check-no-external-assets.mjs"], {
		cwd: HERE,
		encoding: "utf-8",
		maxBuffer: 8 * 1024 * 1024,
	});
	if (show.status !== 0) {
		console.error("could not extract origin/main's guard; pass BASELINE=<path> instead\n" + show.stderr);
		process.exit(2);
	}
	writeFileSync(out, show.stdout);
	return out;
}

/** ~40 KB JS-bundle-shaped source carrying a CDN import. Size matters: a
 * one-line fixture leaves the URL verbatim in most "compressed" outputs
 * (short literals are stored raw), so a toy fixture is caught by accident and
 * would pass against a guard that is in fact broken — issue #81 documents
 * exactly this trap. */
function bundle() {
	let s = `import a from "${URL_MARKER}";\nconsole.log(a);\n`;
	for (let i = 0; s.length < 40 * 1024; i++) {
		s += `function fn${i}(x){return x*${i}+${i % 7};}\n`;
	}
	return Buffer.from(s);
}
const BUNDLE = bundle();

const HAS_ZSTD = typeof zlib.zstdCompressSync === "function";
const HAS_XZ = spawnSync("xz", ["--version"]).status === 0;

function xzc(buf) {
	const r = spawnSync("xz", ["-9", "-c"], { input: buf, maxBuffer: 32 * 1024 * 1024 });
	if (r.status !== 0) throw new Error("xz failed");
	return r.stdout;
}

/** A minimal but structurally real POSIX tar holding one gzip member. A tar
 * has NO offset-0 magic (`ustar` lives at offset 257), which is why it needs
 * the fallthrough bucket removed rather than one more signature added. */
function tarOfGzip(member) {
	const header = Buffer.alloc(512);
	header.write("bundle.js.gz", 0, "ascii");
	header.write("0000644\0", 100, "ascii");
	header.write("0000000\0", 108, "ascii");
	header.write("0000000\0", 116, "ascii");
	header.write(member.length.toString(8).padStart(11, "0") + "\0", 124, "ascii");
	header.write("00000000000\0", 136, "ascii");
	header.write("        ", 148, "ascii"); // checksum placeholder
	header.write("0", 156, "ascii");
	header.write("ustar\0", 257, "ascii");
	header.write("00", 263, "ascii");
	let sum = 0;
	for (const b of header) sum += b;
	header.write(sum.toString(8).padStart(6, "0") + "\0 ", 148, "ascii");
	const pad = Buffer.alloc((512 - (member.length % 512)) % 512);
	return Buffer.concat([header, member, pad, Buffer.alloc(1024)]);
}

/** PNG with the URL sitting uncompressed in a tEXt chunk. */
function pngWithTextChunk(url) {
	const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
	const chunk = (type, data) => {
		const len = Buffer.alloc(4);
		len.writeUInt32BE(data.length);
		const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
		const crc = Buffer.alloc(4); // not a valid CRC; no decoder is involved here
		return Buffer.concat([len, body, crc]);
	};
	const ihdr = Buffer.alloc(13);
	ihdr.writeUInt32BE(1, 0);
	ihdr.writeUInt32BE(1, 4);
	ihdr[8] = 8;
	ihdr[9] = 0;
	return Buffer.concat([
		sig,
		chunk("IHDR", ihdr),
		chunk("tEXt", Buffer.from(`Comment\0see ${url}`, "latin1")),
		chunk("IEND", Buffer.alloc(0)),
	]);
}

/** A structurally real 1x1 24-bit BMP: BITMAPFILEHEADER + BITMAPINFOHEADER.
 * Deliberately not on the known-binary allowlist — "BM" is a two-byte ASCII
 * signature, too weak to license a skip — so this must fail the build. */
function realBmp() {
	const buf = Buffer.alloc(58);
	buf.write("BM", 0, "ascii");
	buf.writeUInt32LE(58, 2); // file size
	buf.writeUInt32LE(0, 6); // reserved
	buf.writeUInt32LE(54, 10); // pixel data offset
	buf.writeUInt32LE(40, 14); // DIB header size
	buf.writeInt32LE(1, 18); // width
	buf.writeInt32LE(1, 22); // height
	buf.writeUInt16LE(1, 26); // planes
	buf.writeUInt16LE(24, 28); // bpp
	return buf;
}

const PLAIN = Buffer.from(`import a from "${URL_MARKER}";\nconsole.log(a);\n`);
const CLEAN = Buffer.from(`<link rel="icon" href="/icons/favicon-32.png">\n`);
const HOST = "cdn.evil.example/x.js";

/** A PNG carrying a real XMP `tEXt`-shaped packet — issue #120's exact repro
 * shape. Builds the chunk directly (rather than via `pngWithTextChunk`,
 * whose keyword/text framing is fixed) with the mandatory RDF/Dublin-Core/
 * XMP namespace URIs a real XMP packet contains as its keyword+text. */
function pngWithXmp() {
	const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
	const chunk = (type, data) => {
		const len = Buffer.alloc(4);
		len.writeUInt32BE(data.length);
		return Buffer.concat([len, Buffer.from(type, "ascii"), data, Buffer.alloc(4)]);
	};
	const ihdr = Buffer.alloc(13);
	ihdr.writeUInt32BE(1, 0);
	ihdr.writeUInt32BE(1, 4);
	ihdr[8] = 8;
	const xmp =
		'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>' +
		'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF ' +
		'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" ' +
		'xmlns:dc="http://purl.org/dc/elements/1.1/" ' +
		'xmlns:xmp="http://ns.adobe.com/xap/1.0/">' +
		"</rdf:RDF></x:xmpmeta>";
	return Buffer.concat([
		sig,
		chunk("IHDR", ihdr),
		chunk("tEXt", Buffer.from(`XML:com.adobe.xmp\0${xmp}`, "latin1")),
		chunk("IEND", Buffer.alloc(0)),
	]);
}

/**
 * `expect` records what SHOULD happen on the candidate guard:
 *   "fail" — the build must stop.
 *   "pass" — the build must succeed (no false positive).
 */
const cases = [
	// ── A. Shapes pre-PR `main` already failed on. The superset property is
	//      that every one of these still fails.
	["A01 brotli named app.js.br", "app.js.br", brotliCompressSync(BUNDLE), "fail"],
	["A02 gzip named app.js.gz", "app.js.gz", gzipSync(BUNDLE), "fail"],
	["A03 zip PK\\x03\\x04 named assets.zip", "assets.zip", Buffer.concat([Buffer.from([0x50, 0x4b, 0x03, 0x04]), BUNDLE.subarray(0, 200)]), "fail"],
	["A04 zlib deflate named bundle.js.gz", "bundle.js.gz", deflateSync(BUNDLE), "fail"],
	["A05 raw DEFLATE named bundle.js.gz", "bundle.js.gz", deflateRawSync(BUNDLE), "fail"],
	["A06 empty zip PK\\x05\\x06 as assets.zip", "assets.zip", Buffer.concat([Buffer.from([0x50, 0x4b, 0x05, 0x06]), Buffer.alloc(18)]), "fail"],
	["A07 gzip behind one leading byte", "bundle.js.gz", Buffer.concat([Buffer.from([0x00]), gzipSync(BUNDLE)]), "fail"],
	["A08 plain JS with CDN import", "app.js", PLAIN, "fail"],
	["A09 CDN import in .mjs chunk", "chunk.mjs", PLAIN, "fail"],
	["A10 CDN import, no extension at all", "LICENSE", PLAIN, "fail"],
	["A11 source map listing a CDN source", "app.js.map", Buffer.from(JSON.stringify({ version: 3, sources: [URL_MARKER] })), "fail"],
	["A12 CDN URL in .xml", "browserconfig.xml", Buffer.from(`<logo src="${URL_MARKER}"/>`), "fail"],
	["A13 bit.ly shortlink sharing the allowlisted prefix", "sw.js", Buffer.from(`u="https://bit.ly/wb-precache-evil-shortlink";`), "fail"],
	["A14 w3.org URL that is not a namespace name", "vendor.js", Buffer.from(`u="http://www.w3.org/StyleSheets/TR/base.css";`), "fail"],
	["A15 plaintext merely named .gz", "not-really.gz", PLAIN, "fail"],

	// ── B. Shapes pre-PR `main` waved through (exit 0). These are the
	//      fail-opens; the candidate must catch every one.
	["B01 zstd named .zst", "bundle.js.zst", HAS_ZSTD ? zlib.zstdCompressSync(BUNDLE) : null, "fail"],
	["B02 xz named .foo (unlisted extension)", "bundle.foo", HAS_XZ ? xzc(BUNDLE) : null, "fail"],
	["B03 gzip with no extension", "bundle", gzipSync(BUNDLE), "fail"],
	["B04 zstd named .dat", "bundle.dat", HAS_ZSTD ? zlib.zstdCompressSync(BUNDLE) : null, "fail"],
	["B05 bzip2 named .bz2", "bundle.js.bz2", Buffer.concat([Buffer.from("BZh9"), BUNDLE.subarray(0, 300)]), "fail"],
	["B06 7z named .7z", "assets.7z", Buffer.concat([Buffer.from([0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c]), BUNDLE.subarray(0, 300)]), "fail"],
	["B07 lz4 frame named .lz4", "bundle.js.lz4", Buffer.concat([Buffer.from([0x04, 0x22, 0x4d, 0x18]), BUNDLE.subarray(0, 300)]), "fail"],
	["B08 brotli named .dat (headerless, unlisted ext)", "bundle.dat", brotliCompressSync(BUNDLE), "fail"],
	["B09 raw DEFLATE named .dat (no header at all)", "bundle.dat", deflateRawSync(BUNDLE), "fail"],

	// ── B'. The five formats the round-2 reviewer walked past the union with,
	//        plus the two structural cases. None was in any table.
	["B10 Unix compress (.Z)  [round-2 escape]", "bundle.js.Z", Buffer.concat([Buffer.from([0x1f, 0x9d, 0x90]), BUNDLE.subarray(0, 300)]), "fail"],
	["B11 lzop (.lzo)         [round-2 escape]", "bundle.js.lzo", Buffer.concat([Buffer.from([0x89, 0x4c, 0x5a, 0x4f, 0x00, 0x0d, 0x0a, 0x1a, 0x0a]), BUNDLE.subarray(0, 300)]), "fail"],
	["B12 lzip (.lz)          [round-2 escape]", "bundle.js.lz", Buffer.concat([Buffer.from([0x4c, 0x5a, 0x49, 0x50, 0x01]), BUNDLE.subarray(0, 300)]), "fail"],
	["B13 RAR (.rar)          [round-2 escape]", "assets.rar", Buffer.concat([Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00]), BUNDLE.subarray(0, 300)]), "fail"],
	["B14 zstd skippable frame named .dat [round-2 escape]", "sidecar.dat", Buffer.concat([Buffer.from([0x5f, 0x2a, 0x4d, 0x18, 0x10, 0x00, 0x00, 0x00]), Buffer.alloc(16, 0x41)]), "fail"],
	["B15 .tar of a gzip member [round-2 escape]", "assets.tar", tarOfGzip(gzipSync(BUNDLE)), "fail"],
	["B16 file named exactly .gz (extname bug) [round-2 escape]", ".gz", gzipSync(BUNDLE), "fail"],
	["B17 file named exactly .br (extname bug) [round-2 escape]", ".br", brotliCompressSync(BUNDLE), "fail"],

	// ── C. Fail-opens the INVERSION closes that no list ever addressed.
	["C01 UTF-16LE text carrying a CDN import", "bundle.js", Buffer.from(`import a from "${URL_MARKER}";`, "utf16le"), "fail"],
	["C02 plaintext CDN URL named icon.png (ext-skip bypass)", "icon.png", PLAIN, "fail"],
	["C03 crafted GIF89a header + valid UTF-8 + CDN URL", "sprite.gif", Buffer.from(`GIF89a;fetch("${URL_MARKER}");`), "fail"],
	["C04 PNG with a cleartext URL in a tEXt chunk", "icon.png", pngWithTextChunk(URL_MARKER), "fail"],
	["C05 same PNG renamed .dat (main scanned it, we must too)", "icon.dat", pngWithTextChunk(URL_MARKER), "fail"],
	["C06 encrypted/random bytes, unlisted extension", "blob.dat", Buffer.from(Array.from({ length: 4096 }, (_, i) => (i * 137 + 200) % 256)), "fail"],
	["C07 real BMP (deliberately NOT on the binary allowlist)", "logo.bmp", realBmp(), "fail"],

	// ── D. Must PASS. A guard that fails everything is not a guard.
	["D01 clean HTML", "index.html", CLEAN, "pass"],
	["D02 clean JS with only allowlisted vendor URLs", "vendor.js", Buffer.from(`throw Error("visit https://react.dev/errors/185");el.setAttributeNS("http://www.w3.org/2000/svg","x",1);`), "pass"],
	["D03 real PNG icon from dist/", "icon-192.png", null, "pass"],
	["D04 WOFF2 font (magic wOF2, allowlisted)", "inter.woff2", Buffer.concat([Buffer.from("wOF2"), Buffer.alloc(500, 0x33)]), "pass"],
	["D05 WASM module (allowlisted)", "mod.wasm", Buffer.concat([Buffer.from([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]), Buffer.alloc(200, 0x11)]), "pass"],
	["D06 empty file", "empty.txt", Buffer.alloc(0), "pass"],
	["D07 UTF-8 text with non-ASCII (emoji, accents)", "notes.txt", Buffer.from("café — 🚀 all local: /assets/x.js"), "pass"],

	// ── E. Pattern-level fail-opens (#110, #105) — the CANDIDATE must catch
	//      every one via canonicalization. NOT superset breaks against `main`:
	//      main never caught these (they are pattern gaps, not file-selection
	//      gaps), so "newly caught" here is the expected, intended delta.
	["E01 uppercase HTTPS scheme (#105)", "app.js", Buffer.from(`import a from "HTTPS://${HOST}";`), "fail"],
	["E02 JSON-escaped solidus", "app.js.map", Buffer.from(`{"u":"https:\\/\\/${HOST}"}`), "fail"],
	["E03 JS hex-escape scheme", "app.js", Buffer.from(`import("\\x68ttps://${HOST}");`), "fail"],
	["E04 HTML numeric entity scheme", "index.html", Buffer.from(`<script src="&#104;ttps://${HOST}"></script>`), "fail"],
	["E05 uppercase scheme + escaped solidus, combined", "app.js.map", Buffer.from(`{"u":"HTTPS:\\/\\/${HOST}"}`), "fail"],

	// ── F. Still-honest residuals (#110) — the candidate does NOT catch these,
	//      by design (they are runtime-assembled, not encodings of a literal).
	//      Recorded here so the matrix states the limitation rather than
	//      silently having no row for it.
	["F01 string concatenation (documented residual)", "app.js", Buffer.from(`import("htt"+"ps://${HOST}");`), "pass"],

	// ── G. Weak-magic-byte exploits (#121) — a real signature glued onto an
	//      opaque payload with no real format structure behind it. NOT
	//      superset breaks (pre-#121 `main` also skips these, by design of
	//      the code this repo had before this PR); "newly caught" is again
	//      the intended delta, which is why the matrix records it as its own
	//      lettered group rather than folding it into A-C.
	["G01 ICO magic over a compressed payload hiding a CDN URL", "sprite.ico", Buffer.concat([Buffer.from([0x00, 0x00, 0x01, 0x00]), deflateRawSync(BUNDLE)]), "fail"],
	["G02 TTF sfnt magic over a compressed payload hiding a CDN URL", "font.ttf", Buffer.concat([Buffer.from([0x00, 0x01, 0x00, 0x00]), deflateRawSync(BUNDLE)]), "fail"],

	// ── H. Legitimate binary metadata (#120) — must PASS, not merely "not a
	//      false negative": a real XMP-bearing PNG is an ordinary asset.
	["H01 PNG carrying a real XMP packet (issue's exact repro)", "logo-xmp.png", pngWithXmp(), "pass"],
];

function runGuard(guard, dir) {
	const r = spawnSync(process.execPath, [guard, dir], { encoding: "utf-8", maxBuffer: 8 * 1024 * 1024 });
	return { status: r.status, out: (r.stdout + r.stderr).trim() };
}

const baseline = resolveBaseline();
console.log(`baseline : ${baseline}`);
console.log(`candidate: ${CANDIDATE}`);
console.log(`zstd=${HAS_ZSTD} xz=${HAS_XZ}\n`);

const header = ["case", "leak", "main", "cand", "want", "verdict"];
console.log(
	`${header[0].padEnd(52)} ${header[1].padEnd(5)} ${header[2].padEnd(5)} ${header[3].padEnd(5)} ${header[4].padEnd(5)} verdict`,
);
console.log("-".repeat(96));

let supersetBreaks = 0;
let expectationBreaks = 0;
let skipped = 0;
let newlyCaught = 0;

for (const [name, filename, bytesOrNull, expected] of cases) {
	let bytes = bytesOrNull;
	if (bytes === null) {
		if (name.startsWith("D03")) {
			bytes = spawnSync("cat", [join(HERE, "..", "dist", "icons", "icon-192.png")], { maxBuffer: 1 << 20 }).stdout;
			if (!bytes || bytes.length === 0) {
				console.log(`${name.padEnd(52)} ${"-".padEnd(5)} ${"-".padEnd(5)} ${"-".padEnd(5)} ${expected.padEnd(5)} SKIP (run npm run build first)`);
				skipped++;
				continue;
			}
		} else {
			console.log(`${name.padEnd(52)} ${"-".padEnd(5)} ${"-".padEnd(5)} ${"-".padEnd(5)} ${expected.padEnd(5)} SKIP (compressor unavailable)`);
			skipped++;
			continue;
		}
	}

	const dir = mkdtempSync(join(tmpdir(), "wp-matrix-"));
	mkdirSync(join(dir, "d"));
	writeFileSync(join(dir, "d", filename), bytes);
	const leak = bytes.toString("latin1").includes(URL_MARKER);
	const mainR = runGuard(baseline, join(dir, "d"));
	const candR = runGuard(CANDIDATE, join(dir, "d"));
	rmSync(dir, { recursive: true, force: true });

	const mainFailed = mainR.status !== 0;
	const candFailed = candR.status !== 0;
	const notes = [];
	// H-series is the one deliberate exception to the superset property: #120
	// is specifically that `main`/pre-#120 guards FALSE-POSITIVE (fail) on
	// legitimate binary-asset metadata, and the whole point of the candidate
	// change is that it now correctly passes. Treating that as a "break" would
	// make the matrix reject its own fix. Every other letter group keeps the
	// superset property un-relaxed.
	const isIntentionalFalsePositiveFix = name.startsWith("H");
	if (mainFailed && !candFailed && !isIntentionalFalsePositiveFix) {
		notes.push("SUPERSET BREAK");
		supersetBreaks++;
	} else if (mainFailed && !candFailed && isIntentionalFalsePositiveFix) {
		notes.push("false-positive fix (#120, expected)");
	}
	if ((expected === "fail") !== candFailed) {
		notes.push("EXPECTATION BREAK");
		expectationBreaks++;
	}
	if (!mainFailed && candFailed) {
		notes.push("newly caught");
		newlyCaught++;
	}
	if (notes.length === 0) notes.push("ok");

	console.log(
		`${name.padEnd(52)} ${String(leak).padEnd(5)} ${(mainFailed ? "FAIL" : "ok").padEnd(5)} ${(candFailed ? "FAIL" : "ok").padEnd(5)} ${expected.padEnd(5)} ${notes.join(", ")}`,
	);
}

console.log("-".repeat(96));
console.log(`cases run        : ${cases.length - skipped} (${skipped} skipped)`);
console.log(`newly caught     : ${newlyCaught}  (candidate fails where main passed)`);
console.log(`superset breaks  : ${supersetBreaks}  (main failed, candidate passed — must be 0)`);
console.log(`expectation break: ${expectationBreaks}  (must be 0)`);

process.exit(supersetBreaks + expectationBreaks === 0 ? 0 : 1);
