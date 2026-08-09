import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, deflateRawSync, deflateSync, gzipSync } from "node:zlib";
import * as zlib from "node:zlib";
import { afterEach, describe, expect, it } from "vitest";
import {
	classifyDistFile,
	detectCompressedFormat,
	detectKnownBinaryFormat,
	isDecodableText,
	matchCompressedExtension,
	scanDist,
} from "./check-no-external-assets.mjs";

const SCRIPT_PATH = join(dirname(fileURLToPath(import.meta.url)), "check-no-external-assets.mjs");

/**
 * Realistic-sized (~40 KB) synthetic JS bundle carrying a CDN import. Issue
 * #81's own investigation is explicit about why size matters: a ONE-LINE
 * `.zst`/`.xz` fixture is caught by accident, because these compressors store
 * short literals raw and the URL survives verbatim in the compressed bytes —
 * so a toy fixture "passes" even against a guard that is in fact broken,
 * giving a false "the guard is fine" reading. Padding this out well past the
 * point where these formats switch from raw literal storage to real entropy
 * coding is what makes the negative controls meaningful. Asserted directly by
 * `expectCliFailsClosed` below, before any fixture is used to prove anything.
 */
function buildRealisticBundleFixture(url) {
	const header = `import a from "${url}";\nconsole.log(a);\n`;
	let filler = "";
	let i = 0;
	while (filler.length < 40 * 1024) {
		filler += `function fn${i}(x){return x*${i}+${i % 7};}\n`;
		i++;
	}
	return header + filler;
}

const REALISTIC_BUNDLE_URL = "https://cdn.evil.example/payload.js";
const REALISTIC_BUNDLE = buildRealisticBundleFixture(REALISTIC_BUNDLE_URL);
const BUNDLE_BUF = Buffer.from(REALISTIC_BUNDLE);

// node:zlib gained zstd support in Node 22.15; the `.xz` fixture shells out to
// the system `xz` binary (universal on Linux) because Node has no built-in xz
// encoder. Both are probed once and dependent tests are skipped — not failed —
// with a clear reason when absent, per docs/testing.md's "never claim a check
// you did not execute": an honest skip beats a flaky failure. Neither
// capability is load-bearing for the model itself, only for two of the
// fixtures that illustrate it.
const HAS_NATIVE_ZSTD = typeof zlib.zstdCompressSync === "function";
const HAS_XZ_BINARY = spawnSync("xz", ["--version"]).status === 0;

/** Runs the real CLI (not scanDist) against `dir` and returns its exit code
 * plus stdout/stderr, so tests can prove the whole guard — including the
 * process.exit paths in main() — behaves as claimed, not just scanDist's pure
 * return value. */
function runCli(dir) {
	const result = spawnSync(process.execPath, [SCRIPT_PATH, dir], { encoding: "utf-8" });
	return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Proves the external-asset guard actually guards (issue #11 acceptance
 * criterion: "demonstrated it fails when fed a deliberate external URL").
 * Also a regression test for the vendor allowlist, and for the FILE-SELECTION
 * model, which is the part of this guard that has failed open five times and
 * is now inverted — see the module header of check-no-external-assets.mjs.
 */
describe("check-no-external-assets", () => {
	let dir;

	afterEach(() => {
		if (dir) {
			rmSync(dir, { recursive: true, force: true });
		}
	});

	function fixtureDir(files) {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		for (const [name, content] of Object.entries(files)) {
			writeFileSync(join(dir, name), content);
		}
		return dir;
	}

	it("flags a deliberate external URL as a violation", () => {
		fixtureDir({ "index.html": `<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">` });

		const { violations } = scanDist(dir);

		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://fonts.googleapis.com/css2?family=Inter");
	});

	it("flags multiple violation types (CDN script + remote image) in one pass", () => {
		fixtureDir({
			"app.js": `import x from "https://cdn.jsdelivr.net/npm/some-lib@1/lib.js"; const img = "http://example.com/logo.png";`,
		});

		const { violations } = scanDist(dir);
		expect(violations.map((v) => v.url).sort()).toEqual(
			["http://example.com/logo.png", "https://cdn.jsdelivr.net/npm/some-lib@1/lib.js"].sort(),
		);
	});

	it("passes a clean fixture with no external URLs", () => {
		fixtureDir({ "index.html": `<link rel="icon" href="/icons/favicon-32.png">` });

		expect(scanDist(dir).violations).toHaveLength(0);
	});

	it("exempts exactly the documented vendor-inert allowlist, nothing broader", () => {
		fixtureDir({
			"vendor.js": [
				`throw Error("Minified React error #185; visit https://react.dev/errors/185 for the full message");`,
				`el.setAttributeNS("http://www.w3.org/1999/xlink", "xlink:href", href);`,
				`console.warn("bad-precache-response: see https://bit.ly/wb-precache");`,
				// A URL that merely starts similarly must NOT be swept in by accident.
				`const evil = "https://react.dev.evil-mirror.example/errors/";`,
			].join("\n"),
		});

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(3);
		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://react.dev.evil-mirror.example/errors/");
	});

	/**
	 * Regression test for issue #77 finding (b). The bit.ly allowlist entry
	 * used to be a prefix match, so a different shortlink sharing the prefix
	 * was silently allowlisted. For a URL shortener the path IS the resource
	 * identity (unlike react.dev/w3.org, where the authority is pinned and only
	 * the path varies), so a prefix match here allows arbitrary destinations.
	 */
	it("flags a bit.ly shortlink that merely shares the allowlisted prefix", () => {
		fixtureDir({ "sw.js": `const u = "https://bit.ly/wb-precache-evil-shortlink";` });

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(0);
		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://bit.ly/wb-precache-evil-shortlink");
	});

	it("allowlists only the four exact w3.org namespace URIs, not the whole origin", () => {
		fixtureDir({
			"vendor.js": [
				`a = "http://www.w3.org/1999/xlink";`,
				`b = "http://www.w3.org/2000/svg";`,
				`c = "http://www.w3.org/1998/Math/MathML";`,
				`d = "http://www.w3.org/XML/1998/namespace";`,
				// Same origin, not a namespace name — must still fail.
				`e = "http://www.w3.org/StyleSheets/TR/base.css";`,
			].join("\n"),
		});

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(4);
		expect(violations.map((v) => v.url)).toEqual(["http://www.w3.org/StyleSheets/TR/base.css"]);
	});

	/**
	 * Regression tests for PR #65 review finding #1. The guard used to select
	 * files with an extension ALLOWLIST, so these shapes were skipped in
	 * silence and a dist/ shipping a CDN import passed clean. Selection is now
	 * content-based, which subsumes the fix: an extension has no say in whether
	 * a file is scanned at all.
	 */
	describe("scans by content, so no extension can exempt a text file", () => {
		it("flags an external import inside a .mjs chunk", () => {
			fixtureDir({ "chunk.mjs": `import x from "https://cdn.evil.example/lib.mjs";` });

			const { violations, scanned } = scanDist(dir);

			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/lib.mjs");
			expect(scanned).toHaveLength(1);
		});

		it("flags an external URL inside a file with no extension at all", () => {
			fixtureDir({ LICENSE: `@font-face { src: url(https://fonts.evil.example/x.woff2); }` });

			const { violations, scanned } = scanDist(dir);

			expect(violations).toHaveLength(1);
			expect(scanned).toHaveLength(1);
		});

		it("flags external URLs in .cjs and .xml alongside the .css sibling that was always caught", () => {
			fixtureDir({
				"shim.cjs": `require("https://cdn.evil.example/a.cjs");`,
				"browserconfig.xml": `<square150x150logo src="https://cdn.evil.example/b.png"/>`,
				"styles.css": `body { background: url(https://cdn.evil.example/c.png); }`,
			});

			expect(scanDist(dir).violations.map((v) => v.url).sort()).toEqual([
				"https://cdn.evil.example/a.cjs",
				"https://cdn.evil.example/b.png",
				"https://cdn.evil.example/c.png",
			]);
		});

		it("recurses into subdirectories rather than only scanning the dist root", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			mkdirSync(join(dir, "assets", "nested"), { recursive: true });
			writeFileSync(join(dir, "assets", "nested", "worker"), `fetch("https://beacon.evil.example/ping");`);

			const { violations } = scanDist(dir);

			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://beacon.evil.example/ping");
		});

		it("flags an external URL inside a source map (.map has no special status)", () => {
			fixtureDir({
				"app.js.map": JSON.stringify({
					version: 3,
					sources: ["https://cdn.evil.example/original/app.ts"],
					sourcesContent: [null],
					mappings: "",
				}),
			});

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/original/app.ts");
		});

		it("scans an uppercase-extension file exactly like any other text file", () => {
			fixtureDir({ "CHUNK.MJS": `import x from "https://cdn.evil.example/upper.mjs";` });

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/upper.mjs");
		});

		it("flags an external URL inside a dotfile, where extname() returns \"\"", () => {
			fixtureDir({ ".well-known-thing": `see https://cdn.evil.example/dotfile for details` });

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/dotfile");
		});

		it("scans UTF-8 text containing non-ASCII rather than treating it as binary", () => {
			fixtureDir({ "notes.txt": Buffer.from("café — 🚀 fetch(\"https://cdn.evil.example/emoji.js\")") });

			const { violations, scanned, unscannable } = scanDist(dir);

			expect(unscannable).toHaveLength(0);
			expect(scanned).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/emoji.js");
		});
	});

	/**
	 * ═══════════════════════════════════════════════════════════════════════
	 * THE INVERTED MODEL — the round-3 change, and the reason this guard is no
	 * longer an enumeration.
	 * ═══════════════════════════════════════════════════════════════════════
	 *
	 * Five fail-opens (#65, #77, #81, PR #88 round 1, PR #88 round 2) were all
	 * the same shape: a format nobody had listed fell through to a default
	 * "scan it and report OK" branch. Correcting the list five times did not
	 * converge, because the set of opaque formats is unbounded.
	 *
	 * So there is no default-pass branch any more. A file passes only by being
	 * confidently text (scanned) or by matching the short magic-byte allowlist
	 * of known-binary assets (skipped). Everything else fails the build.
	 *
	 * These tests are written to fail if that inversion is ever undone — in
	 * particular the "fails closed by default" cases, which use formats that
	 * are deliberately NOT in any table in the script.
	 */
	describe("fails closed by default: only text and allowlisted binaries pass", () => {
		/** Asserts the whole CLI rejects a single-file dist/, and returns
		 * stderr. Also asserts the fixture is a meaningful negative control:
		 * the marker URL must not survive in the raw bytes, or the scan branch
		 * would catch it by accident and prove nothing about opacity. */
		function expectCliFailsClosed(fixtureName, bytes) {
			expect(bytes.toString("latin1")).not.toContain(REALISTIC_BUNDLE_URL);
			fixtureDir({ [fixtureName]: bytes });

			const { status, stdout, stderr } = runCli(dir);

			expect(status).toBe(1);
			expect(stdout).not.toMatch(/OK — no external references found/);
			expect(stderr).toMatch(/1 unscannable file\(s\) found/);
			return stderr;
		}

		describe("the text test is the whole scannability criterion", () => {
			it("accepts valid UTF-8 with no NUL bytes, and nothing else", () => {
				expect(isDecodableText(Buffer.from("plain ascii"))).toBe(true);
				expect(isDecodableText(Buffer.from("caf\u00e9 \u2014 \ud83d\ude80"))).toBe(true);
				expect(isDecodableText(Buffer.alloc(0))).toBe(true);
				// A NUL byte: binary container, or UTF-16/UTF-32 text.
				expect(isDecodableText(Buffer.from([0x61, 0x00, 0x62]))).toBe(false);
				// Invalid UTF-8: a continuation byte with no lead byte. This is
				// what gzip (1f 8b), zstd (28 b5 ..), 7z (37 7a bc ..), .Z
				// (1f 9d) and lzop (89 4c ..) all present in their first two
				// bytes — which is why no signature table is needed to reject
				// them.
				expect(isDecodableText(Buffer.from([0x1f, 0x8b, 0x08]))).toBe(false);
				expect(isDecodableText(Buffer.from([0xff, 0xfe, 0x41]))).toBe(false);
			});

			it("rejects every headerless compressed format, which no signature table could reach", () => {
				// Brotli, zlib and raw DEFLATE are the three `vite-plugin-
				// compression2` algorithms with no fixed magic number
				// (`CoreAlgorithm = 'gzip' | 'brotliCompress' | 'deflate' |
				// 'deflateRaw' | 'zstandard'`), and its default filename is
				// `[path][base].gz` for all but brotli/zstandard. Under the old
				// model these needed an extension list. Under this one their
				// CONTENT disqualifies them, whatever they are called.
				for (const bytes of [
					brotliCompressSync(BUNDLE_BUF),
					deflateSync(BUNDLE_BUF),
					deflateRawSync(BUNDLE_BUF),
				]) {
					expect(detectCompressedFormat(bytes)).toBeNull(); // nothing to sniff
					expect(isDecodableText(bytes)).toBe(false); // caught anyway
					expect(classifyDistFile("chunk.unlisted-ext", bytes).disposition).toBe("fail");
				}
			});
		});

		describe("formats nobody enumerated fail without being enumerated", () => {
			/**
			 * The five formats the PR #88 round-2 reviewer walked straight past
			 * the magic-bytes-OR-extension union with, plus the two structural
			 * cases. Each is asserted to fail with its diagnostic signature
			 * REMOVED from the table, so the test proves the *default* is
			 * failure rather than proving one more table entry works.
			 */
			const roundTwoEscapes = [
				["Unix compress (.Z)", "bundle.js.Z", Buffer.from([0x1f, 0x9d, 0x90])],
				["lzop", "bundle.js.lzo", Buffer.from([0x89, 0x4c, 0x5a, 0x4f, 0x00, 0x0d, 0x0a, 0x1a, 0x0a])],
				["lzip", "bundle.js.lz", Buffer.from([0x4c, 0x5a, 0x49, 0x50, 0x01, 0x0c])],
				["RAR", "assets.rar", Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00])],
			];

			for (const [label, name, header] of roundTwoEscapes) {
				it(`${label} fails the build (round-2 escape)`, () => {
					// Header + high-entropy payload, so the URL cannot survive.
					const bytes = Buffer.concat([header, deflateRawSync(BUNDLE_BUF)]);
					expectCliFailsClosed(name, bytes);
				});
			}

			it("a zstd skippable frame fails the build (round-2 escape)", () => {
				// RFC 8878 §3.1.2: magic 0x184D2A5?, so 0x50..0x5f all open one.
				// Real tools emit these to carry side data. The payload here is
				// deliberately NUL-padded, exactly as a length-prefixed frame is.
				const bytes = Buffer.concat([
					Buffer.from([0x5f, 0x2a, 0x4d, 0x18, 0x10, 0x00, 0x00, 0x00]),
					Buffer.alloc(16),
				]);
				expectCliFailsClosed("sidecar.dat", bytes);
			});

			it("a .tar whose member is compressed fails the build, despite having no offset-0 magic", () => {
				// A tar's `ustar` marker lives at offset 257, so offset-anchored
				// sniffing cannot see it at all. What fails it here is that a tar
				// header is NUL-padded — a content property, not a table entry.
				const member = gzipSync(BUNDLE_BUF);
				const header = Buffer.alloc(512);
				header.write("bundle.js.gz", 0, "ascii");
				header.write(member.length.toString(8).padStart(11, "0") + "\0", 124, "ascii");
				header.write("ustar\0", 257, "ascii");
				const pad = Buffer.alloc((512 - (member.length % 512)) % 512);
				expectCliFailsClosed("assets.tar", Buffer.concat([header, member, pad, Buffer.alloc(1024)]));
			});

			it("a file named exactly .gz fails the build (extname() returns \"\" for it)", () => {
				// PR #88 round-2 review, finding 2: `extname(".gz") === ""`, so
				// the old extension half was blind to a leading-dot filename —
				// and for `.br`, which has no magic number, that meant blind to
				// BOTH halves. Under the inverted model the content decides, so
				// the name cannot create a bypass at all.
				expect(matchCompressedExtension(".gz")).toBe(".gz");
				expectCliFailsClosed(".gz", gzipSync(BUNDLE_BUF));
			});

			it("a file named exactly .br fails the build (both halves were blind to it)", () => {
				expect(matchCompressedExtension(".br")).toBe(".br");
				const bytes = brotliCompressSync(BUNDLE_BUF);
				expect(detectCompressedFormat(bytes)).toBeNull(); // no magic — nothing to sniff
				expectCliFailsClosed(".br", bytes);
			});

			it("UTF-16LE text carrying a CDN import fails closed instead of being 'scanned'", () => {
				// The old model decoded this as UTF-8, matched nothing (every
				// ASCII character is separated by a NUL), and reported it as
				// SCANNED — a false all-clear on a file whose URL is right
				// there. NUL bytes now disqualify it.
				const bytes = Buffer.from(`import a from "${REALISTIC_BUNDLE_URL}";`, "utf16le");
				fixtureDir({ "bundle.js": bytes });

				const { scanned, unscannable } = scanDist(dir);

				expect(scanned).toHaveLength(0);
				expect(unscannable).toHaveLength(1);
				expect(unscannable[0].reason).toMatch(/NUL bytes/);
				expect(runCli(dir).status).toBe(1);
			});

			it("random high-entropy bytes under an unremarkable name fail closed", () => {
				// The general case: no format, no signature, no extension. There
				// is no bucket for this to fall into any more.
				const bytes = Buffer.from(Array.from({ length: 4096 }, (_, i) => (i * 137 + 200) % 256));
				expectCliFailsClosed("blob.dat", bytes);
			});
		});

		describe("compressed-format detection is diagnostics, not a decision", () => {
			it("names the format in the failure message when it recognises one", () => {
				const stderr = expectCliFailsClosed("bundle.js.zst-ish", Buffer.concat([gzipSync(BUNDLE_BUF)]));
				expect(stderr).toMatch(/looks like gzip/);
			});

			it("still fails, with a generic reason, when it recognises nothing", () => {
				const bytes = Buffer.from(Array.from({ length: 2048 }, (_, i) => 0x80 + (i % 64)));
				expect(detectCompressedFormat(bytes)).toBeNull();
				expect(detectKnownBinaryFormat(bytes)).toBeNull();
				const stderr = expectCliFailsClosed("mystery.bin", bytes);
				expect(stderr).toMatch(/not valid UTF-8 and matches no known-binary signature/);
			});

			it("recognises the formats it knows about purely for the message, filename-independently", () => {
				expect(detectCompressedFormat(gzipSync(Buffer.from("x")))).toBe("gzip");
				expect(detectCompressedFormat(Buffer.from([0x28, 0xb5, 0x2f, 0xfd, 0x00]))).toBe("zstd");
				expect(detectCompressedFormat(Buffer.from([0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00]))).toBe("xz");
				expect(detectCompressedFormat(Buffer.from([0x50, 0x4b, 0x05, 0x06, 0x00]))).toBe("zip (empty archive)");
				expect(detectCompressedFormat(Buffer.from([0x1f, 0x9d, 0x90]))).toBe("Unix compress (.Z)");
				expect(detectCompressedFormat(Buffer.from([0x89, 0x4c, 0x5a, 0x4f, 0x00]))).toBe("lzop");
				expect(detectCompressedFormat(Buffer.from([0x4c, 0x5a, 0x49, 0x50, 0x01]))).toBe("lzip");
				expect(detectCompressedFormat(Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07]))).toBe("rar");
				expect(detectCompressedFormat(Buffer.from([0x5f, 0x2a, 0x4d, 0x18, 0x00]))).toBe("zstd skippable frame");
				expect(detectCompressedFormat(Buffer.from("import a from \"/local.js\";"))).toBeNull();
			});

			it("cannot change a verdict: a file it does not recognise fails exactly the same way", () => {
				// The property that makes this table safe to leave incomplete
				// forever. Two buffers that are equally unreadable — one
				// recognised, one not — get the same disposition and differ only
				// in the human-readable reason.
				const known = gzipSync(BUNDLE_BUF);
				const unknown = Buffer.concat([Buffer.from([0xc3, 0x28, 0xa0]), deflateRawSync(BUNDLE_BUF)]);
				expect(detectCompressedFormat(unknown)).toBeNull();
				expect(classifyDistFile("a.dat", known).disposition).toBe("fail");
				expect(classifyDistFile("b.dat", unknown).disposition).toBe("fail");
			});
		});

		describe("the known-binary skip allowlist is verified by magic bytes, never by filename", () => {
			const png = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), Buffer.alloc(64, 0x9f)]);
			// Realistic filler: a WOFF2 header carries length/offset fields full of
			// NULs and its tables are brotli-compressed, so real ones are never
			// valid UTF-8. A fixture padded with printable ASCII would be *text*,
			// and would be scanned rather than skipped — correctly, since the guard
			// could read it.
			const woff2 = Buffer.concat([Buffer.from("wOF2"), Buffer.alloc(64, 0x9f)]);
			const wasm = Buffer.concat([Buffer.from([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]), Buffer.alloc(64, 0x11)]);

			/**
			 * A structurally real minimal ICO: ICONDIR (magic + idCount=1) plus one
			 * ICONDIRENTRY whose declared image offset/size fit inside the buffer,
			 * plus that many bytes of "image data" filler. Issue #121's fix requires
			 * this much of the format to be present before a skip is licensed — see
			 * `validateIconDir`. Used as the POSITIVE control; the exploit shape
			 * (magic bytes over an unrelated payload, with no real directory) is
			 * covered separately below.
			 */
			function validIco() {
				const header = Buffer.alloc(6);
				header.writeUInt16LE(0, 0); // reserved
				header.writeUInt16LE(1, 2); // type: icon
				header.writeUInt16LE(1, 4); // idCount: 1
				const entry = Buffer.alloc(16);
				entry[0] = 16; // width
				entry[1] = 16; // height
				const imageData = Buffer.alloc(32, 0x11);
				entry.writeUInt32LE(imageData.length, 8); // bytes in resource
				entry.writeUInt32LE(header.length + entry.length, 12); // offset
				return Buffer.concat([header, entry, imageData]);
			}
			const ico = validIco();

			it("recognises the allowlisted asset types by content", () => {
				expect(detectKnownBinaryFormat(png)).toBe("png");
				expect(detectKnownBinaryFormat(woff2)).toBe("woff2");
				expect(detectKnownBinaryFormat(wasm)).toBe("wasm");
				expect(detectKnownBinaryFormat(Buffer.from([0xff, 0xd8, 0xff, 0xe0]))).toBe("jpeg");
				expect(detectKnownBinaryFormat(ico)).toBe("ico");
			});

			it("skips them regardless of what they are named, and passes the build", () => {
				fixtureDir({ "icon.png": png, "font.dat": woff2, mystery: wasm });

				const { skipped, scanned, unscannable } = scanDist(dir);

				expect(unscannable).toHaveLength(0);
				expect(scanned).toHaveLength(0);
				expect(skipped.map((s) => s.format).sort()).toEqual(["png", "wasm", "woff2"]);
				expect(runCli(dir).status).toBe(0);
			});

			it("does NOT skip a file merely NAMED like an asset — content decides", () => {
				// This is the inversion of the old `SKIPPED_BINARY_EXTENSIONS`
				// behaviour, and a genuine fail-open it closes: pre-PR `main`
				// skipped anything ending `.png` outright, so a plaintext file
				// with a CDN URL called `icon.png` passed the build clean.
				fixtureDir({ "icon.png": `<script src="https://cdn.evil.example/x.js"></script>` });

				const { violations, scanned, skipped } = scanDist(dir);

				expect(skipped).toHaveLength(0);
				expect(scanned).toHaveLength(1);
				expect(violations[0].url).toBe("https://cdn.evil.example/x.js");
				expect(runCli(dir).status).toBe(1);
			});

			it("a crafted file wearing an ASCII asset header is scanned, not skipped", () => {
				// Several allowlisted signatures ARE ascii (`GIF89a`, `wOFF`,
				// `OTTO`, `ttcf`). If the binary check ran before the text
				// check, this file would be skipped and its URL never seen. The
				// order in `classifyDistFile` is what prevents that, and this is
				// the test that fails if anyone swaps the two branches.
				const crafted = Buffer.from(`GIF89a;fetch("https://cdn.evil.example/x.js");`);
				expect(detectKnownBinaryFormat(crafted)).toBe("gif");
				expect(classifyDistFile("sprite.gif", crafted).disposition).toBe("scan");

				fixtureDir({ "sprite.gif": crafted });
				expect(scanDist(dir).violations[0].url).toBe("https://cdn.evil.example/x.js");
				expect(runCli(dir).status).toBe(1);
			});

			it("still finds a cleartext URL inside a skipped binary asset", () => {
				// The skip is a licence not to fail for being opaque, not a
				// licence to ignore the bytes. A URL sitting uncompressed in a
				// PNG tEXt chunk is a real external reference — and pre-#77
				// `main` only ever caught that shape by accident, whenever the
				// file happened not to be on its extension skip list.
				const withUrl = Buffer.concat([
					Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
					Buffer.from("tEXtComment\0see https://cdn.evil.example/meta.js", "latin1"),
				]);
				fixtureDir({ "icon.png": withUrl });

				const { violations, skipped } = scanDist(dir);

				expect(skipped).toHaveLength(1);
				expect(violations[0].url).toBe("https://cdn.evil.example/meta.js");
				expect(runCli(dir).status).toBe(1);
			});

			it("fails a real binary type that is deliberately NOT on the allowlist (BMP)", () => {
				// BMP's whole signature is the two ASCII bytes "BM" — too weak
				// to license a skip, so it is left off and fails the build. That
				// is the model working as designed: the escape hatch is a
				// justified allowlist entry in a reviewable diff, not a silent
				// pass. EOT is absent for the same reason (no signature at all).
				const bmp = Buffer.alloc(58);
				bmp.write("BM", 0, "ascii");
				bmp.writeUInt32LE(58, 2);
				bmp.writeUInt32LE(54, 10);
				bmp.writeUInt32LE(40, 14);
				expect(detectKnownBinaryFormat(bmp)).toBeNull();
				fixtureDir({ "logo.bmp": bmp });
				expect(runCli(dir).status).toBe(1);
			});
		});

		/**
		 * ═══════════════════════════════════════════════════════════════════
		 * ISSUE #121 — a 4-byte weak signature no longer licenses a skip on its
		 * own; `validate` (see `matchesSignature`) must also see a plausible
		 * rest-of-format. These are the exact exploit shapes from the issue:
		 * a real magic prefix glued onto an opaque/compressed payload that
		 * contains a CDN URL in cleartext nowhere the byte-scan backstop can
		 * see it. Each must now FAIL the build; before this fix all three
		 * printed "OK ... known-binary file(s) skipped" with exit 0.
		 * ═══════════════════════════════════════════════════════════════════
		 */
		describe("weak magic-byte signatures require real structure, not just 4 bytes (#121)", () => {
			/** Deflate-compresses a realistic bundle carrying a CDN import, so the
			 * URL is real but not present in cleartext — proving the cleartext
			 * backstop cannot be what is catching this case. */
			function opaquePayloadWithHiddenUrl() {
				return deflateRawSync(BUNDLE_BUF);
			}

			it("ICO magic over an opaque payload is not skipped, and the hidden URL cannot leak through", () => {
				const payload = opaquePayloadWithHiddenUrl();
				const bytes = Buffer.concat([Buffer.from([0x00, 0x00, 0x01, 0x00]), payload]);
				expect(bytes.toString("latin1")).not.toContain(REALISTIC_BUNDLE_URL);
				expect(detectKnownBinaryFormat(bytes)).toBeNull();
				expect(classifyDistFile("sprite.ico", bytes).disposition).toBe("fail");

				fixtureDir({ "sprite.ico": bytes });
				const { status, stderr, stdout } = runCli(dir);
				expect(status).toBe(1);
				expect(stdout).not.toMatch(/OK — no external references found/);
				expect(stderr).toMatch(/unscannable file\(s\) found/);
			});

			it("CUR magic over an opaque payload is not skipped", () => {
				const bytes = Buffer.concat([Buffer.from([0x00, 0x00, 0x02, 0x00]), opaquePayloadWithHiddenUrl()]);
				expect(detectKnownBinaryFormat(bytes)).toBeNull();
				fixtureDir({ "cursor.cur": bytes });
				expect(runCli(dir).status).toBe(1);
			});

			it("TTF sfnt magic over an opaque payload is not skipped", () => {
				const bytes = Buffer.concat([Buffer.from([0x00, 0x01, 0x00, 0x00]), opaquePayloadWithHiddenUrl()]);
				expect(detectKnownBinaryFormat(bytes)).toBeNull();
				fixtureDir({ "font.ttf": bytes });
				expect(runCli(dir).status).toBe(1);
			});

			it("a genuine minimal ICO (real ICONDIR, in-bounds directory entry) is still skipped", () => {
				// The positive control: the fix must not turn every ICO into a
				// failure, only ones wearing the magic over the wrong shape.
				const header = Buffer.alloc(6);
				header.writeUInt16LE(1, 2);
				header.writeUInt16LE(1, 4);
				const entry = Buffer.alloc(16);
				entry[0] = 16;
				entry[1] = 16;
				const imageData = Buffer.alloc(32, 0x11);
				entry.writeUInt32LE(imageData.length, 8);
				entry.writeUInt32LE(header.length + entry.length, 12);
				const bytes = Buffer.concat([header, entry, imageData]);

				expect(detectKnownBinaryFormat(bytes)).toBe("ico");
				fixtureDir({ "sprite.ico": bytes });
				expect(runCli(dir).status).toBe(0);
			});

			it("a genuine minimal TTF (plausible sfnt table directory) is still skipped", () => {
				const numTables = 4;
				const header = Buffer.alloc(12);
				header.writeUInt32BE(0x00010000, 0);
				header.writeUInt16BE(numTables, 4);
				const records = Buffer.alloc(numTables * 16, 0x20);
				const bytes = Buffer.concat([header, records]);

				expect(detectKnownBinaryFormat(bytes)).toBe("ttf");
				fixtureDir({ "font.ttf": bytes });
				expect(runCli(dir).status).toBe(0);
			});

			it("an ICO claiming a directory entry that overruns the buffer fails, not skips", () => {
				const header = Buffer.alloc(6);
				header.writeUInt16LE(1, 2);
				header.writeUInt16LE(1, 4);
				const entry = Buffer.alloc(16);
				entry.writeUInt32LE(0xffffff, 8); // absurd size
				entry.writeUInt32LE(header.length + entry.length, 12);
				const bytes = Buffer.concat([header, entry]); // no image data at all

				expect(detectKnownBinaryFormat(bytes)).toBeNull();
				fixtureDir({ "sprite.ico": bytes });
				expect(runCli(dir).status).toBe(1);
			});

			it("a TTF claiming an implausible numTables (e.g. 0, or absurdly large) fails, not skips", () => {
				const zero = Buffer.alloc(12);
				zero.writeUInt32BE(0x00010000, 0);
				zero.writeUInt16BE(0, 4);
				expect(detectKnownBinaryFormat(zero)).toBeNull();

				const huge = Buffer.alloc(12);
				huge.writeUInt32BE(0x00010000, 0);
				huge.writeUInt16BE(5000, 4);
				expect(detectKnownBinaryFormat(huge)).toBeNull();
			});
		});

		describe("the compressed-extension name veto is one-way and cannot un-fail anything", () => {
			it("refuses to scan a text file named like a compressed artifact", () => {
				// Retained from the round-2 design as a narrow backstop for the
				// one residual the text test cannot cover alone: a compressed
				// stream whose bytes coincidentally form valid UTF-8. Costs
				// nothing (rename the file) and can only ever ADD a failure.
				fixtureDir({ "not-really.gz": `import a from "https://cdn.evil.example/x.js";` });

				const { scanned, unscannable } = scanDist(dir);

				expect(scanned).toHaveLength(0);
				expect(unscannable).toHaveLength(1);
				expect(unscannable[0].reason).toMatch(/claims a compressed\/opaque artifact/);
				expect(runCli(dir).status).toBe(1);
			});

			it("never turns a fail into a pass, and never overrides a recognised binary asset", () => {
				const png = Buffer.concat([
					Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
					Buffer.alloc(32, 0x9f),
				]);
				// A PNG named `.gz` is still a PNG: the veto only gates the SCAN
				// branch, so it cannot make a skip into a failure either way.
				expect(classifyDistFile("weird.gz", png).disposition).toBe("skip");
				// And an unlisted name never rescues unreadable content.
				expect(matchCompressedExtension("chunk.unlisted-ext")).toBeNull();
				expect(classifyDistFile("chunk.unlisted-ext", gzipSync(BUNDLE_BUF)).disposition).toBe("fail");
			});

			it("matches on the basename suffix, not extname(), so a leading-dot name is covered", () => {
				expect(matchCompressedExtension("app.js.br")).toBe(".br");
				expect(matchCompressedExtension("APP.JS.BR")).toBe(".br");
				expect(matchCompressedExtension(".gz")).toBe(".gz");
				expect(matchCompressedExtension(".br")).toBe(".br");
				expect(matchCompressedExtension("assets.zip")).toBe(".zip");
				expect(matchCompressedExtension("app.js")).toBeNull();
				expect(matchCompressedExtension("targz")).toBeNull();
			});
		});

		/**
		 * Issue #81's own repro shape, kept because it is the case the issue was
		 * filed for: realistic bundles compressed with zstd and xz, both of
		 * which pre-PR `main` reported as *scanned* with exit 0.
		 */
		describe.skipIf(!HAS_NATIVE_ZSTD)("realistic .zst fixture (issue #81 repro)", () => {
			it("does not leak the URL in cleartext once compressed (fixture sanity check)", () => {
				expect(zlib.zstdCompressSync(BUNDLE_BUF).toString("latin1")).not.toContain(REALISTIC_BUNDLE_URL);
			});

			it("CLI fails the build instead of reporting a false OK", () => {
				expect(expectCliFailsClosed("bundle.js.zst", zlib.zstdCompressSync(BUNDLE_BUF))).toMatch(/looks like zstd/);
			});
		});

		describe.skipIf(!HAS_XZ_BINARY)("realistic .xz fixture (issue #81 repro)", () => {
			function xzCompressSync(buffer) {
				const result = spawnSync("xz", ["-9", "-c"], { input: buffer, maxBuffer: 10 * 1024 * 1024 });
				if (result.status !== 0) {
					throw new Error(`xz compression failed: ${result.stderr}`);
				}
				return result.stdout;
			}

			it("does not leak the URL in cleartext once compressed (fixture sanity check)", () => {
				expect(xzCompressSync(BUNDLE_BUF).toString("latin1")).not.toContain(REALISTIC_BUNDLE_URL);
			});

			it("CLI fails the build instead of reporting a false OK", () => {
				expect(expectCliFailsClosed("bundle.js.xz", xzCompressSync(BUNDLE_BUF))).toMatch(/looks like xz/);
			});

			it("CLI: both realistic payloads together (issue #81's exact repro) fail, not '2 file(s) scanned' OK", () => {
				const files = { "bundle.js.xz": xzCompressSync(BUNDLE_BUF) };
				if (HAS_NATIVE_ZSTD) {
					files["bundle.js.zst"] = zlib.zstdCompressSync(BUNDLE_BUF);
				}
				fixtureDir(files);

				const { status, stderr, stdout } = runCli(dir);

				expect(status).toBe(1);
				expect(stderr).toMatch(/unscannable file\(s\) found/);
				expect(stdout).not.toMatch(/OK — no external references found/);
			});
		});

		/**
		 * Issue #77's repro, and PR #88 round 1's regression: `.br`/`.gz` were
		 * first silently skipped as inert binary, then (round 1) lost their
		 * fail-closed signal to a substitution. Both now fail on CONTENT, so
		 * neither a list edit nor a mechanism swap can reopen them.
		 */
		it("CLI: a dist/ containing only app.js.br + app.js.gz (issue #77 repro) fails the build", () => {
			const payload = Buffer.from(REALISTIC_BUNDLE);
			fixtureDir({ "app.js.br": brotliCompressSync(payload), "app.js.gz": gzipSync(payload) });

			const { status, stderr, stdout } = runCli(dir);

			expect(status).toBe(1);
			expect(stdout).not.toMatch(/OK — no external references found/);
			expect(stderr).toMatch(/2 unscannable file\(s\) found/);
			expect(stderr).toMatch(/app\.js\.gz: looks like gzip/);
		});

		it("CLI: exits 0 on a clean, uncompressed fixture", () => {
			fixtureDir({ "index.html": `<link rel="icon" href="/icons/favicon-32.png">` });

			const { status, stdout } = runCli(dir);

			expect(status).toBe(0);
			expect(stdout).toMatch(/OK — no external references found/);
		});
	});

	/**
	 * ═══════════════════════════════════════════════════════════════════════
	 * PR #88 round-2 review, FINDING 3 — the shipped path must be the tested
	 * path.
	 * ═══════════════════════════════════════════════════════════════════════
	 *
	 * Round 1 shipped a regression that the suite could not see, because
	 * `scanDist` inlined its own copy of the classification branch while the
	 * property test asserted about a helper the CLI never called. Editing the
	 * inline copy left the property test green.
	 *
	 * So: exactly one predicate decides, `scanDist` calls it, and there is no
	 * second implementation. This is asserted two ways, because either alone
	 * is escapable — the structural check catches a re-inlining that happens to
	 * behave identically today, and the parity check catches a divergence that
	 * keeps the call but adds a branch around it.
	 */
	describe("one predicate, and the CLI path uses it (round-2 finding 3)", () => {
		it("scanDist routes through classifyDistFile and re-derives nothing itself", () => {
			const source = scanDist.toString();

			expect(source).toMatch(/classifyDistFile\(/);
			// No second implementation: scanDist must not consult any of the
			// underlying signals directly. If a future change needs one, it
			// belongs inside classifyDistFile, where the tests can see it.
			for (const helper of [
				"isDecodableText",
				"detectKnownBinaryFormat",
				"detectCompressedFormat",
				"matchCompressedExtension",
			]) {
				expect(source).not.toContain(`${helper}(`);
			}
		});

		it("every disposition scanDist reports equals classifyDistFile's, across the whole corpus", () => {
			// Behavioural parity over one file of every shape the model
			// distinguishes. A `scanDist` that kept the call but wrapped it in
			// its own extra branch would break here even though the structural
			// check above still passed.
			const corpus = {
				"text.js": Buffer.from(`const u = "https://cdn.evil.example/a.js";`),
				"clean.html": Buffer.from(`<link rel="icon" href="/x.png">`),
				noext: Buffer.from("plain text, no extension"),
				".dotfile": Buffer.from("leading dot"),
				"icon.png": Buffer.concat([
					Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
					Buffer.alloc(32, 0x9f),
				]),
				"font.woff2": Buffer.concat([Buffer.from("wOF2"), Buffer.alloc(32, 0x9f)]),
				"mod.wasm": Buffer.concat([Buffer.from([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]), Buffer.alloc(8)]),
				"bundle.js.gz": gzipSync(BUNDLE_BUF),
				"bundle.js.br": brotliCompressSync(BUNDLE_BUF),
				"chunk.unlisted-ext": deflateRawSync(BUNDLE_BUF),
				"utf16.js": Buffer.from("https://cdn.evil.example/b.js", "utf16le"),
				"plaintext.gz": Buffer.from("plain text with a misleading name"),
				".gz": gzipSync(BUNDLE_BUF),
				"sprite.gif": Buffer.from(`GIF89a;const u="/local.js";`),
				"blob.dat": Buffer.from(Array.from({ length: 512 }, (_, i) => 0x80 + (i % 64))),
			};
			fixtureDir(corpus);

			const { scanned, skipped, unscannable } = scanDist(dir);
			const actual = new Map();
			for (const f of scanned) actual.set(f, "scan");
			for (const { file } of skipped) actual.set(file, "skip");
			for (const { file } of unscannable) actual.set(file, "fail");

			expect(actual.size).toBe(Object.keys(corpus).length);
			for (const [name, bytes] of Object.entries(corpus)) {
				const path = join(dir, name);
				expect(actual.get(path), `disposition for ${name}`).toBe(classifyDistFile(path, bytes).disposition);
			}

			// And the corpus is not degenerate — all three dispositions occur.
			expect(new Set(actual.values())).toEqual(new Set(["scan", "skip", "fail"]));
		});

		/**
		 * ═══════════════════════════════════════════════════════════════════
		 * ISSUE #122 — the fixed 15-fixture corpus above is enumerative, and
		 * this guard just spent five rounds learning that enumeration never
		 * converges. It proves parity on the SHAPES someone thought to write
		 * down; it says nothing about a shape nobody did.
		 *
		 * This is Option A from the issue: a seeded property/fuzz check that
		 * generates several hundred random `(filename, buffer)` pairs — random
		 * lengths spanning the MB boundary, random byte distributions, random
		 * extensions and path depths — and asserts `scanDist`'s reported
		 * disposition for each equals `classifyDistFile`'s. A `scanDist` that
		 * adds ANY new branch that diverges from the predicate on ANY shape
		 * (not just the ones above) fails this, because the corpus is not
		 * fixed content — it is a fixed *procedure* over an unbounded space.
		 *
		 * Concretely, this closes the exact gap #122 demonstrated: a
		 * `buffer.length > 1048576` early-exit inside `scanDist` that "large
		 * assets are slow to scan" its way past `classifyDistFile` entirely.
		 * None of the 15 fixed fixtures cross 1 MB; several generated ones do
		 * by construction (see `randomLength`), so that branch is exercised
		 * here even though nobody wrote a dedicated ">1MB" fixture for it.
		 *
		 * Seeded (mulberry32, not Math.random) so a failure is reproducible —
		 * printed on failure so it can be pinned as a regression fixture.
		 */
		describe("property parity: scanDist matches classifyDistFile over an unbounded generated space (#122)", () => {
			function mulberry32(seed) {
				let a = seed;
				return () => {
					a |= 0;
					a = (a + 0x6d2b79f5) | 0;
					let t = Math.imul(a ^ (a >>> 15), 1 | a);
					t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
					return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
				};
			}

			const SEED = 0xf00dcafe;
			const CASE_COUNT = 300;
			const MB = 1024 * 1024;
			const EXTENSIONS = ["", ".js", ".css", ".html", ".gz", ".br", ".png", ".woff2", ".unlisted-ext", ".GZ", ".Gz"];

			function randomLength(rand) {
				// Deliberately spans the 1 MB boundary the issue's repro exploits,
				// plus small sizes where header-only formats live.
				const buckets = [
					() => Math.floor(rand() * 64), // tiny
					() => Math.floor(rand() * 4096), // small
					() => Math.floor(rand() * 64 * 1024), // medium
					() => MB - 8 + Math.floor(rand() * 16), // straddles 1 MB exactly
					() => MB + Math.floor(rand() * (2 * MB)), // clearly over 1 MB
				];
				return buckets[Math.floor(rand() * buckets.length)]();
			}

			function randomBuffer(rand, length) {
				const buf = Buffer.alloc(length);
				// Mix of byte-distribution regimes so both "confidently text" and
				// "confidently binary" (and the ambiguous space between) all occur:
				// pure printable ASCII, high-entropy, NUL-heavy, and occasionally a
				// real magic-byte prefix glued onto random tail bytes — the exact
				// #121 shape — so this fuzz also exercises that boundary.
				const regime = Math.floor(rand() * 5);
				const magics = [
					[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], // png
					[0x00, 0x00, 0x01, 0x00], // ico (weak)
					[0x00, 0x01, 0x00, 0x00], // ttf (weak)
					[0x1f, 0x8b], // gzip
				];
				let start = 0;
				if (regime === 4 && length > 0) {
					const magic = magics[Math.floor(rand() * magics.length)];
					for (let i = 0; i < magic.length && i < length; i++) buf[i] = magic[i];
					start = Math.min(magic.length, length);
				}
				for (let i = start; i < length; i++) {
					if (regime === 0) buf[i] = 0x20 + Math.floor(rand() * 95); // printable ASCII
					else if (regime === 1) buf[i] = Math.floor(rand() * 256); // high-entropy
					else if (regime === 2) buf[i] = rand() < 0.1 ? 0 : 0x20 + Math.floor(rand() * 95); // NUL-sprinkled text
					else buf[i] = Math.floor(rand() * 256);
				}
				return buf;
			}

			function randomPath(rand, index) {
				const depth = Math.floor(rand() * 3);
				const segments = [];
				for (let d = 0; d < depth; d++) segments.push(`dir${Math.floor(rand() * 5)}`);
				const ext = EXTENSIONS[Math.floor(rand() * EXTENSIONS.length)];
				const name = rand() < 0.1 ? `.${index}` : `file${index}${ext}`;
				segments.push(name);
				return segments;
			}

			it(`scanDist matches classifyDistFile on ${CASE_COUNT} seeded random (filename, buffer) pairs`, () => {
				const rand = mulberry32(SEED);
				dir = mkdtempSync(join(tmpdir(), "waypoint-fuzz-"));

				const cases = [];
				for (let i = 0; i < CASE_COUNT; i++) {
					const segments = randomPath(rand, i);
					const length = randomLength(rand);
					const bytes = randomBuffer(rand, length);
					const fullDir = join(dir, ...segments.slice(0, -1));
					mkdirSync(fullDir, { recursive: true });
					const filePath = join(fullDir, segments[segments.length - 1]);
					writeFileSync(filePath, bytes);
					cases.push({ filePath, bytes, segments });
				}

				const { scanned, skipped, unscannable } = scanDist(dir);
				const actual = new Map();
				for (const f of scanned) actual.set(f, "scan");
				for (const { file } of skipped) actual.set(file, "skip");
				for (const { file } of unscannable) actual.set(file, "fail");

				const mismatches = [];
				for (const { filePath, bytes, segments } of cases) {
					const expected = classifyDistFile(filePath, bytes).disposition;
					const got = actual.get(filePath);
					if (got !== expected) {
						mismatches.push({ path: segments.join("/"), length: bytes.length, expected, got });
					}
				}

				expect(mismatches, `mismatches (seed=${SEED}): ${JSON.stringify(mismatches, null, 2)}`).toHaveLength(0);
				// Sanity: the generator actually produced files past the 1 MB
				// boundary the #122 repro exploited, and all three dispositions.
				expect(cases.some((c) => c.bytes.length > MB)).toBe(true);
				expect(new Set(actual.values()).size).toBeGreaterThan(1);
			// This fuzz case writes CASE_COUNT files to a real temp dir and
			// re-scans them; under v8 coverage instrumentation (the CI
			// `test:coverage` run added in #102) that comfortably exceeds
			// vitest's 5000ms default. Give it explicit headroom rather than
			// raising the global timeout (which would mask genuine hangs).
			}, 30000);

			it("catches the exact #122 regression shape: a size-cap early-exit inside scanDist", () => {
				// This does not test scanDist as shipped — it demonstrates that
				// the fuzz check above WOULD catch the issue's own repro, by
				// running the same comparison the fuzz test runs but against a
				// hand-simulated "scanDist-with-an-early-exit" disposition
				// function instead of the real one. If this test ever fails, the
				// fuzz corpus has stopped generating >1MB files and needs its
				// distribution revisited.
				const rand = mulberry32(SEED);
				let foundOverCap = false;
				for (let i = 0; i < CASE_COUNT && !foundOverCap; i++) {
					randomPath(rand, i);
					const length = randomLength(rand);
					randomBuffer(rand, length);
					if (length > MB) foundOverCap = true;
				}
				expect(foundOverCap).toBe(true);
			});
		});
	});

	/**
	 * ═══════════════════════════════════════════════════════════════════════
	 * ISSUES #110 AND #105 — CLOSED BY CANONICALIZATION, NOT ENUMERATION.
	 * ═══════════════════════════════════════════════════════════════════════
	 *
	 * These shapes used to be a documented, un-caught residual (see git
	 * history for the block this replaces). `canonicalizeForScan()` decodes
	 * case, HTML entities, JS `\x`/`\u` escapes and JSON `\/` escapes before
	 * `URL_PATTERN` runs, so every one of these is now caught — including
	 * combinations the issue didn't enumerate individually, which is the
	 * point of fixing the model rather than the list: canonicalization
	 * composes, so a NEW shape built from the same primitives (nest an
	 * entity inside a hex escape, mix case with an escaped slash) is caught
	 * without needing its own case.
	 */
	describe("obfuscated external references are caught by canonicalization (#110, #105)", () => {
		const caught = {
			"uppercase scheme (#105)": `import x from "HTTPS://cdn.evil.example/x.js";`,
			"mixed-case scheme": `import x from "HtTpS://cdn.evil.example/x.js";`,
			"JSON-escaped solidus": `{"u":"https:\\/\\/cdn.evil.example/x.js"}`,
			"JS hex escape (scheme)": `import("\\x68ttps://cdn.evil.example/x.js");`,
			"JS hex escape (whole scheme+colon)": `import("\\x68\\x74\\x74\\x70\\x73://cdn.evil.example/x.js");`,
			"JS unicode escape (\\uHHHH)": `import("\\u0068ttps://cdn.evil.example/x.js");`,
			"JS unicode escape (\\u{H...})": `import("\\u{68}ttps://cdn.evil.example/x.js");`,
			"HTML numeric entity (decimal)": `<script src="&#104;ttps://cdn.evil.example/x.js"></script>`,
			"HTML numeric entity (hex)": `<script src="&#x68;ttps://cdn.evil.example/x.js"></script>`,
			// Combinations — not individually named by either issue, proving the
			// fix is a property of the model (canonicalization composes) and not
			// one case added per reported shape.
			"uppercase scheme + escaped solidus": `{"u":"HTTPS:\\/\\/cdn.evil.example/x.js"}`,
			// Two entity-decodable characters used together: the scheme's "h" is
			// entity-encoded AND the solidus pair is JS-escaped, in the same
			// literal. Composes only if both transforms run over the same pass.
			"HTML entity for scheme + escaped solidus together": `{"u":"&#104;ttps:\\/\\/cdn.evil.example/x.js"}`,
		};

		for (const [label, body] of Object.entries(caught)) {
			it(`catches: ${label}`, () => {
				fixtureDir({ "app.js": Buffer.from(body) });

				const { violations, scanned } = scanDist(dir);

				expect(scanned).toHaveLength(1);
				expect(violations).toHaveLength(1);
				expect(violations[0].url.toLowerCase()).toContain("cdn.evil.example/x.js");
			});
		}

		it("CLI exits 1 on an uppercase-scheme fixture (the exact #105 repro)", () => {
			fixtureDir({ "app.js": `import x from "HTTPS://cdn.evil.example/x.js";\n` });
			const { status, stdout } = runCli(dir);
			expect(status).toBe(1);
			expect(stdout).not.toMatch(/OK — no external references found/);
		});

		it("control: the same reference as a literal IS caught", () => {
			fixtureDir({ "app.js": `import x from "https://cdn.evil.example/x.js";` });
			expect(scanDist(dir).violations).toHaveLength(1);
		});

		it("case folding does not widen the exact-path ALLOWLIST entries (#105's own warning)", () => {
			// #105 explicitly flags the risk: folding a matched URL to lowercase
			// before testing it against ALLOWLIST could let a case-varied PATH
			// smuggle past an anchored lowercase pattern. Scheme/host folding is
			// correct (RFC 3986); path folding is not, because these patterns are
			// deliberately exact on the path (see ALLOWLIST's own comments).
			fixtureDir({ "vendor.js": `throw Error("visit HTTPS://REACT.DEV/ERRORS/185 for detail");` });

			const { violations, allowlisted } = scanDist(dir);

			// The uppercase PATH segment "/ERRORS/" is not "/errors/", so this
			// must NOT be allowlisted — only scheme+host case-folding, not path.
			expect(allowlisted).toHaveLength(0);
			expect(violations).toHaveLength(1);
		});

		it("case folding DOES let an uppercase scheme+host reach the allowlist for an exact path match", () => {
			fixtureDir({ "vendor.js": `throw Error("visit HTTPS://REACT.DEV/errors/185 for detail");` });

			const { violations, allowlisted } = scanDist(dir);

			expect(allowlisted).toHaveLength(1);
			expect(violations).toHaveLength(0);
		});

		/**
		 * STILL a residual, and correctly so — these are not encodings of a
		 * literal scheme, they are values ASSEMBLED AT RUNTIME. No text-level
		 * canonicalization (however composable) resolves a value that does not
		 * exist as a contiguous string anywhere in the source. Pinned here,
		 * deliberately, so this remains a documented and honest limitation
		 * rather than a silent gap discovered later — see the module header's
		 * "THE RESIDUAL, STATED HONESTLY".
		 */
		describe("still out of reach, and documented as such: runtime-assembled references", () => {
			const stillMissed = {
				"string concatenation": `import("htt" + "ps://cdn.evil.example/x.js");`,
				"String.fromCharCode assembly": `fetch(String.fromCharCode(104,116,116,112,115,58,47,47)+"cdn.evil.example/x.js");`,
			};

			for (const [label, body] of Object.entries(stillMissed)) {
				it(`does not (and structurally cannot) catch: ${label}`, () => {
					fixtureDir({ "app.js": Buffer.from(body) });

					const { violations, scanned } = scanDist(dir);

					expect(scanned).toHaveLength(1);
					expect(violations).toHaveLength(0);
				});
			}
		});
	});

	/**
	 * ═══════════════════════════════════════════════════════════════════════
	 * ISSUE #120 — standard image metadata (XMP/tEXt) must not false-positive.
	 * ═══════════════════════════════════════════════════════════════════════
	 */
	describe("legitimate binary-asset metadata does not false-positive (#120)", () => {
		const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

		function pngWithTextChunk(text) {
			const chunk = (type, data) => {
				const len = Buffer.alloc(4);
				len.writeUInt32BE(data.length);
				return Buffer.concat([len, Buffer.from(type, "ascii"), data, Buffer.alloc(4)]);
			};
			const ihdr = Buffer.alloc(13);
			ihdr.writeUInt32BE(1, 0);
			ihdr.writeUInt32BE(1, 4);
			ihdr[8] = 8;
			return Buffer.concat([PNG_SIG, chunk("IHDR", ihdr), chunk("tEXt", Buffer.from(text, "latin1")), chunk("IEND", Buffer.alloc(0))]);
		}

		it("passes a PNG carrying a real XMP packet (the issue's exact repro shape)", () => {
			const xmp =
				'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>' +
				'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF ' +
				'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" ' +
				'xmlns:dc="http://purl.org/dc/elements/1.1/" ' +
				'xmlns:xmp="http://ns.adobe.com/xap/1.0/">' +
				"</rdf:RDF></x:xmpmeta>";
			const bytes = pngWithTextChunk(`XML:com.adobe.xmp\0${xmp}`);

			expect(detectKnownBinaryFormat(bytes)).toBe("png");
			fixtureDir({ "logo-xmp.png": bytes });

			const { violations, skipped, allowlisted } = scanDist(dir);
			expect(skipped).toHaveLength(1);
			expect(violations).toHaveLength(0);
			expect(allowlisted.length).toBeGreaterThan(0);
			expect(runCli(dir).status).toBe(0);
		});

		it("passes a PNG carrying an operator tEXt field with a benign namespace-shaped URI", () => {
			const bytes = pngWithTextChunk("Source\0http://ns.adobe.com/photoshop/1.0/");
			fixtureDir({ "logo-text.png": bytes });
			expect(runCli(dir).status).toBe(0);
		});

		it("still FAILS when a skipped binary carries an actual non-metadata external URL", () => {
			// The metadata allowlist must stay narrow: a real CDN reference
			// planted alongside legitimate XMP must still be caught. This is the
			// regression test for over-correcting #120 into a blanket exemption.
			const xmp = 'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" src="https://cdn.evil.example/tracker.js"';
			const bytes = pngWithTextChunk(`XML:com.adobe.xmp\0${xmp}`);
			fixtureDir({ "logo-xmp.png": bytes });

			const { violations, allowlisted } = scanDist(dir);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/tracker.js");
			expect(allowlisted.length).toBeGreaterThan(0); // the XMP namespace itself is still exempted
			expect(runCli(dir).status).toBe(1);
		});

		it("does NOT exempt a metadata-namespace URI when it appears in application JS (scan context)", () => {
			// The scoping is the point of a SEPARATE list (see the module header):
			// METADATA_NAMESPACE_ALLOWLIST only applies to skip-dispositioned
			// binaries. The same literal string in a .js file is not metadata —
			// it is application code, and must be scanned exactly as strictly as
			// any other URL in that context.
			fixtureDir({ "vendor.js": `const ns = "http://ns.adobe.com/xap/1.0/";` });

			const { violations, allowlisted } = scanDist(dir);
			expect(allowlisted).toHaveLength(0);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("http://ns.adobe.com/xap/1.0/");
		});
	});
});
