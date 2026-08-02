import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, gzipSync } from "node:zlib";
import * as zlib from "node:zlib";
import { afterEach, describe, expect, it } from "vitest";
import { detectMagicByteFormat, isBrotliByExtension, isCompressedArtifact, scanDist } from "./check-no-external-assets.mjs";

const SCRIPT_PATH = join(dirname(fileURLToPath(import.meta.url)), "check-no-external-assets.mjs");

/**
 * Realistic-sized (~40 KB) synthetic JS bundle carrying a CDN import, used
 * by the .zst/.xz negative-control tests below. Issue #81's own
 * investigation is explicit about why size matters here: a ONE-LINE
 * `.zst`/`.xz` fixture is caught by accident, because these compressors
 * store short literals raw and the URL survives verbatim in the compressed
 * bytes — so a toy fixture "passes" even against the OLD, buggy
 * extension-list guard, which would give a false "the guard is fine"
 * reading. Padding this out to bundle-like size (well past where these
 * formats switch from raw literal storage to real entropy coding) is what
 * makes the negative control meaningful. This is asserted directly in
 * "realistic .zst/.xz fixtures do not leak the URL in cleartext" below,
 * before either fixture is used to test anything about the guard itself.
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

// node:zlib gained zstd support in Node 22.15 (this repo targets modern
// Node, but CI images vary); the `.xz` fixture shells out to the system
// `xz` binary (present in this project's dev/CI sandboxes, and effectively
// universal on Linux) because Node has no built-in xz encoder. Both are
// probed once at module load and every dependent test is skipped — not
// failed — with a clear reason when the capability is absent, per
// docs/testing.md's "never claim a check you did not execute" rule: an
// honest skip beats a flaky failure on an environment that lacks the tool.
const HAS_NATIVE_ZSTD = typeof zlib.zstdCompressSync === "function";
const HAS_XZ_BINARY = spawnSync("xz", ["--version"]).status === 0;

/** Runs the real CLI (not scanDist) against `dir` and returns its exit code
 * plus stdout/stderr, so tests can prove the whole guard — including the
 * process.exit paths in main() — behaves as claimed, not just scanDist's
 * pure return value. */
function runCli(dir) {
	const result = spawnSync(process.execPath, [SCRIPT_PATH, dir], { encoding: "utf-8" });
	return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Proves the external-asset guard actually guards (issue #11 acceptance
 * criterion: "demonstrated it fails when fed a deliberate external URL").
 * Also a regression test for the vendor allowlist: exactly the three known
 * inert patterns are exempted, nothing else — and for the file-selection
 * model itself, which used to be an extension allowlist that silently
 * skipped `.mjs`/`.cjs`/`.xml`/extensionless files (PR #65 review, finding
 * #1). Those cases are the "scans by default" describe block below; they
 * fail if anyone reintroduces an allowlist. It also covers the two residual
 * fail-open holes from issue #77: `.br`/`.gz`/`.zip` used to be treated as
 * inert binary and silently skipped (now they fail the build outright, see
 * the "fails closed on compressed artifacts" block), and the `bit.ly`
 * allowlist entry was prefix- rather than `$`-anchored.
 */
describe("check-no-external-assets", () => {
	let dir;

	afterEach(() => {
		if (dir) {
			rmSync(dir, { recursive: true, force: true });
		}
	});

	it("flags a deliberate external URL as a violation", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(
			join(dir, "index.html"),
			`<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">`,
		);

		const { violations } = scanDist(dir);

		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://fonts.googleapis.com/css2?family=Inter");
	});

	it("flags multiple violation types (CDN script + remote image) in one pass", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(
			join(dir, "app.js"),
			`import x from "https://cdn.jsdelivr.net/npm/some-lib@1/lib.js"; const img = "http://example.com/logo.png";`,
		);

		const { violations } = scanDist(dir);
		expect(violations.map((v) => v.url).sort()).toEqual(
			["http://example.com/logo.png", "https://cdn.jsdelivr.net/npm/some-lib@1/lib.js"].sort(),
		);
	});

	it("passes a clean fixture with no external URLs", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(join(dir, "index.html"), `<link rel="icon" href="/icons/favicon-32.png">`);

		const { violations } = scanDist(dir);
		expect(violations).toHaveLength(0);
	});

	it("exempts exactly the documented vendor-inert allowlist, nothing broader", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(
			join(dir, "vendor.js"),
			[
				`throw Error("Minified React error #185; visit https://react.dev/errors/185 for the full message");`,
				`el.setAttributeNS("http://www.w3.org/1999/xlink", "xlink:href", href);`,
				`console.warn("bad-precache-response: see https://bit.ly/wb-precache");`,
				// A URL that merely starts similarly must NOT be swept in by accident.
				`const evil = "https://react.dev.evil-mirror.example/errors/";`,
			].join("\n"),
		);

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(3);
		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://react.dev.evil-mirror.example/errors/");
	});

	/**
	 * Regression test for issue #77 finding (b). The bit.ly allowlist entry
	 * used to be a prefix match (`/^https:\/\/bit\.ly\/wb-precache/`), so a
	 * different shortlink that merely shares the prefix — `wb-precache-evil-
	 * shortlink` — was silently allowlisted. For a URL shortener the path IS
	 * the resource identity (unlike react.dev/w3.org, where the authority is
	 * pinned and only the path varies), so a prefix match here allows
	 * arbitrary destinations. The entry is now `$`-anchored to the exact
	 * literal workbox-window emits.
	 */
	it("flags a bit.ly shortlink that merely shares the allowlisted prefix", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(join(dir, "sw.js"), `const u = "https://bit.ly/wb-precache-evil-shortlink";`);

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(0);
		expect(violations).toHaveLength(1);
		expect(violations[0].url).toBe("https://bit.ly/wb-precache-evil-shortlink");
	});

	it("allowlists only the four exact w3.org namespace URIs, not the whole origin", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(
			join(dir, "vendor.js"),
			[
				`a = "http://www.w3.org/1999/xlink";`,
				`b = "http://www.w3.org/2000/svg";`,
				`c = "http://www.w3.org/1998/Math/MathML";`,
				`d = "http://www.w3.org/XML/1998/namespace";`,
				// Same origin, not a namespace name — must still fail.
				`e = "http://www.w3.org/StyleSheets/TR/base.css";`,
			].join("\n"),
		);

		const { violations, allowlisted } = scanDist(dir);

		expect(allowlisted).toHaveLength(4);
		expect(violations.map((v) => v.url)).toEqual(["http://www.w3.org/StyleSheets/TR/base.css"]);
	});

	it("skips known-binary extensions (e.g. .png) even if they happened to contain the bytes", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(join(dir, "icon.png"), Buffer.from(`fake-binary https://evil.example/x`));

		const { violations, skipped } = scanDist(dir);
		expect(violations).toHaveLength(0);
		expect(skipped).toHaveLength(1);
	});

	/**
	 * Regression tests for PR #65 review finding #1. The guard used to select
	 * files with an extension ALLOWLIST, so these four shapes were skipped in
	 * silence and a dist/ shipping a CDN import passed clean. The model is now
	 * "scan everything except known binaries" — if anyone reintroduces an
	 * allowlist, these fail.
	 */
	describe("scans unknown file types by default (denylist, not allowlist)", () => {
		it("flags an external import inside a .mjs chunk", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "chunk.mjs"), `import x from "https://cdn.evil.example/lib.mjs";`);

			const { violations, scanned } = scanDist(dir);

			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/lib.mjs");
			expect(scanned).toHaveLength(1);
		});

		it("flags an external URL inside a file with no extension at all", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			// e.g. anything dropped in public/ without an extension — copied
			// verbatim into dist/ by vite.
			writeFileSync(join(dir, "LICENSE"), `@font-face { src: url(https://fonts.evil.example/x.woff2); }`);

			const { violations, scanned } = scanDist(dir);

			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://fonts.evil.example/x.woff2");
			expect(scanned).toHaveLength(1);
		});

		it("flags external URLs in .cjs and .xml alongside the .css sibling that was always caught", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "shim.cjs"), `require("https://cdn.evil.example/a.cjs");`);
			writeFileSync(join(dir, "browserconfig.xml"), `<square150x150logo src="https://cdn.evil.example/b.png"/>`);
			writeFileSync(join(dir, "styles.css"), `body { background: url(https://cdn.evil.example/c.png); }`);

			const { violations } = scanDist(dir);

			expect(violations.map((v) => v.url).sort()).toEqual([
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

		it("flags an external URL inside a source map (.map is not on the binary denylist)", () => {
			// Source maps are JSON full of URLs (sources, sourcesContent) — the
			// PR #65 round-2 review specifically called this shape out as one
			// nobody's extension list would have enumerated.
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(
				join(dir, "app.js.map"),
				JSON.stringify({
					version: 3,
					sources: ["https://cdn.evil.example/original/app.ts"],
					sourcesContent: [null],
					mappings: "",
				}),
			);

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/original/app.ts");
		});

		it("matches skip/compressed extensions case-insensitively (uppercase .MJS is still scanned)", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "CHUNK.MJS"), `import x from "https://cdn.evil.example/upper.mjs";`);

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/upper.mjs");
		});

		it("flags an external URL inside a dotfile, where extname() returns \"\"", () => {
			// e.g. .well-known-thing dropped in public/ — Node's path.extname()
			// treats a leading-dot-only name as having no extension, so this
			// must land in the "scanned" bucket exactly like any other
			// extensionless file.
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, ".well-known-thing"), `see https://cdn.evil.example/dotfile for details`);

			const { violations, scanned } = scanDist(dir);

			expect(scanned).toHaveLength(1);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/dotfile");
		});
	});

	/**
	 * Regression tests for issue #77 finding (a): `.br`/`.gz`/`.zip` used to
	 * sit on SKIPPED_BINARY_EXTENSIONS, so a dist/ whose only JS payload was
	 * pre-compressed (carrying an external import in its decoded text) passed
	 * with "0 file(s) scanned" — reported success while inspecting nothing.
	 *
	 * And for issue #81: after #77, the guard was still an extension list —
	 * now COMPRESSED_EXTENSIONS = {.br, .gz, .zip} — so a format nobody had
	 * added to that list yet (`.zst`, `.xz`, `.7z`, `.lz4`, or any of those
	 * renamed to something else entirely) fell through to the default scan
	 * branch, decoded as UTF-8, matched nothing, and was reported as
	 * *scanned*. The detector is now a hybrid: magic-byte sniffing for every
	 * format that has a header, plus an extension check for Brotli alone
	 * (which has none) — see the module header comment. The guard fails
	 * closed either way: a compressed artifact's mere presence fails the
	 * build, whether or not it happens to carry a URL.
	 */
	describe("fails closed on compressed artifacts instead of silently skipping them", () => {
		describe("detectMagicByteFormat — the durable half of the detector", () => {
			it("recognizes every magic-byte format this guard defends against, regardless of filename", () => {
				// gzip and brotli use real compressor output; the rest use their
				// documented header bytes directly — the guard only ever looks at
				// these first few bytes, so a synthetic buffer with the right
				// header is exactly what a genuine file of that format looks like
				// to this function.
				expect(detectMagicByteFormat(gzipSync(Buffer.from("hello")))).toBe("gzip");
				expect(detectMagicByteFormat(Buffer.from([0x28, 0xb5, 0x2f, 0xfd, 0x00, 0x00]))).toBe("zstd");
				expect(detectMagicByteFormat(Buffer.from([0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00, 0x00]))).toBe("xz");
				expect(detectMagicByteFormat(Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x00]))).toBe("zip");
				expect(detectMagicByteFormat(Buffer.from([0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0x00]))).toBe("7z");
				expect(detectMagicByteFormat(Buffer.from([0x04, 0x22, 0x4d, 0x18, 0x00]))).toBe("lz4");
				expect(detectMagicByteFormat(Buffer.from([0x42, 0x5a, 0x68, 0x39]))).toBe("bzip2");
			});

			it("returns null for ordinary text and for buffers too short to carry a signature", () => {
				expect(detectMagicByteFormat(Buffer.from("import a from \"/local.js\";"))).toBeNull();
				expect(detectMagicByteFormat(Buffer.from([]))).toBeNull();
				expect(detectMagicByteFormat(Buffer.from([0x1f]))).toBeNull();
			});

			it("is not fooled by filename: a gzip-magic-byte buffer is detected under any name or none", () => {
				// This is the central #81 regression: detection must not depend on
				// anyone having listed the right extension. Same bytes, four
				// different names (including no extension and an unrelated one),
				// same answer.
				const gz = gzipSync(Buffer.from("import a from \"https://cdn.evil.example/x.js\";"));
				for (const name of ["payload.gz", "payload.zst", "payload.dat", "payload"]) {
					expect(isCompressedArtifact(name, gz)).toBe(true);
				}
			});
		});

		it("isBrotliByExtension is the one deliberate extension-based exception — Brotli has no magic number to sniff", () => {
			expect(isBrotliByExtension("app.js.br")).toBe(true);
			expect(isBrotliByExtension("APP.JS.BR")).toBe(true);
			expect(isBrotliByExtension("app.js")).toBe(false);
			// A real brotli stream has no detectable header — proves the negative
			// this whole exception exists to cover, so magic-byte sniffing alone
			// (without the extension check) would miss it.
			const brotli = brotliCompressSync(Buffer.from("import a from \"https://cdn.evil.example/x.js\";"));
			expect(detectMagicByteFormat(brotli)).toBeNull();
			expect(isCompressedArtifact("app.js.br", brotli)).toBe(true);
		});

		it("scanDist reports a .br file as compressed by extension, not scanned or skipped", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			writeFileSync(join(dir, "app.js.br"), brotliCompressSync(Buffer.from(payload)));

			const { violations, scanned, skipped, compressed } = scanDist(dir);

			expect(compressed).toEqual([{ file: join(dir, "app.js.br"), format: "brotli" }]);
			expect(scanned).toHaveLength(0);
			expect(skipped).toHaveLength(0);
			// It is never scanned, so it can never be a "violation" either — its
			// mere presence is the failure, reported through `compressed`.
			expect(violations).toHaveLength(0);
		});

		it("scanDist reports a .gz file as compressed by magic bytes, not scanned or skipped", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			writeFileSync(join(dir, "app.js.gz"), gzipSync(Buffer.from(payload)));

			const { scanned, skipped, compressed } = scanDist(dir);

			expect(compressed).toEqual([{ file: join(dir, "app.js.gz"), format: "gzip" }]);
			expect(scanned).toHaveLength(0);
			expect(skipped).toHaveLength(0);
		});

		it("scanDist catches a gzip payload under an extension nobody put on any list (the #81 shape)", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			// Neither ".weird-ext" nor ".dat" has ever appeared in
			// SKIPPED_BINARY_EXTENSIONS or any compressed-extension list — that's
			// the point. Magic-byte detection doesn't care.
			writeFileSync(join(dir, "chunk.weird-ext"), gzipSync(Buffer.from(payload)));

			const { violations, scanned, compressed } = scanDist(dir);

			expect(compressed).toEqual([{ file: join(dir, "chunk.weird-ext"), format: "gzip" }]);
			expect(scanned).toHaveLength(0);
			expect(violations).toHaveLength(0);
		});

		/**
		 * The issue #81 negative control, reproduced from its own repro steps:
		 * a ~40 KB JS-bundle-shaped fixture carrying a CDN import, compressed
		 * with zstd and xz. First a sanity check that the fixture is realistic
		 * enough to matter (the URL does NOT survive in cleartext — unlike a
		 * one-line fixture, which the issue notes is caught by accident), then
		 * the actual proof that scanDist/the CLI now fail closed on it.
		 */
		describe.skipIf(!HAS_NATIVE_ZSTD)("realistic .zst fixture (issue #81 repro)", () => {
			it("does not leak the URL in cleartext once compressed (fixture sanity check)", () => {
				const compressed = zlib.zstdCompressSync(Buffer.from(REALISTIC_BUNDLE));
				expect(compressed.toString("utf-8")).not.toContain(REALISTIC_BUNDLE_URL);
			});

			it("scanDist reports it as compressed (format zstd), never scanned", () => {
				dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
				writeFileSync(join(dir, "bundle.js.zst"), zlib.zstdCompressSync(Buffer.from(REALISTIC_BUNDLE)));

				const { violations, scanned, compressed } = scanDist(dir);

				expect(compressed).toEqual([{ file: join(dir, "bundle.js.zst"), format: "zstd" }]);
				expect(scanned).toHaveLength(0);
				expect(violations).toHaveLength(0);
			});

			it("CLI fails the build instead of reporting a false OK", () => {
				dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
				writeFileSync(join(dir, "bundle.js.zst"), zlib.zstdCompressSync(Buffer.from(REALISTIC_BUNDLE)));

				const { status, stderr } = runCli(dir);

				expect(status).toBe(1);
				expect(stderr).toMatch(/1 compressed artifact\(s\) found/);
				expect(stderr).toMatch(/bundle\.js\.zst \(zstd\)/);
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
				const compressed = xzCompressSync(Buffer.from(REALISTIC_BUNDLE));
				expect(compressed.toString("utf-8")).not.toContain(REALISTIC_BUNDLE_URL);
			});

			it("scanDist reports it as compressed (format xz), never scanned", () => {
				dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
				writeFileSync(join(dir, "bundle.js.xz"), xzCompressSync(Buffer.from(REALISTIC_BUNDLE)));

				const { violations, scanned, compressed } = scanDist(dir);

				expect(compressed).toEqual([{ file: join(dir, "bundle.js.xz"), format: "xz" }]);
				expect(scanned).toHaveLength(0);
				expect(violations).toHaveLength(0);
			});

			it("CLI fails the build instead of reporting a false OK", () => {
				dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
				writeFileSync(join(dir, "bundle.js.xz"), xzCompressSync(Buffer.from(REALISTIC_BUNDLE)));

				const { status, stderr } = runCli(dir);

				expect(status).toBe(1);
				expect(stderr).toMatch(/1 compressed artifact\(s\) found/);
				expect(stderr).toMatch(/bundle\.js\.xz \(xz\)/);
			});

			it("CLI: a dist/ with both realistic .zst and .xz payloads (issue #81's exact repro shape) fails, not '2 file(s) scanned' OK", () => {
				dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
				writeFileSync(join(dir, "bundle.js.xz"), xzCompressSync(Buffer.from(REALISTIC_BUNDLE)));
				if (HAS_NATIVE_ZSTD) {
					writeFileSync(join(dir, "bundle.js.zst"), zlib.zstdCompressSync(Buffer.from(REALISTIC_BUNDLE)));
				}

				const { status, stderr } = runCli(dir);

				expect(status).toBe(1);
				expect(stderr).toMatch(/compressed artifact\(s\) found/);
				// The exact false-OK output issue #81 reproduced against main.
				expect(stderr).not.toMatch(/OK — no external references found/);
			});
		});

		it("CLI: exits 0 (\"0 file(s) scanned\") on the real build with no compressed output", () => {
			// Sanity check that a clean, uncompressed fixture is unaffected —
			// the counterpart to the failure cases above and below.
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "index.html"), `<link rel="icon" href="/icons/favicon-32.png">`);

			const { status, stdout } = runCli(dir);

			expect(status).toBe(0);
			expect(stdout).toMatch(/OK — no external references found/);
		});

		it("CLI: a dist/ containing only app.js.br + app.js.gz (issue #77 repro) fails the build, not '0 file(s) scanned' OK", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			writeFileSync(join(dir, "app.js.br"), brotliCompressSync(Buffer.from(payload)));
			writeFileSync(join(dir, "app.js.gz"), gzipSync(Buffer.from(payload)));

			const { status, stderr } = runCli(dir);

			expect(status).toBe(1);
			expect(stderr).toMatch(/2 compressed artifact\(s\) found/);
			expect(stderr).toMatch(/app\.js\.br \(brotli\)/);
			expect(stderr).toMatch(/app\.js\.gz \(gzip\)/);
		});

		it("CLI: a .zip artifact (real PK magic bytes) fails the build", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "bundle.zip"), Buffer.from("PK\x03\x04not a real zip but has the extension"));

			const { status, stderr } = runCli(dir);

			expect(status).toBe(1);
			expect(stderr).toMatch(/1 compressed artifact\(s\) found/);
			expect(stderr).toMatch(/bundle\.zip \(zip\)/);
		});

		it("a plain text file merely named .gz/.zip (no real magic bytes) is scanned, not failed — content decides, not the name", () => {
			// The mirror image of the #81 fix: just as a real compressed payload
			// under an unrecognized extension must fail, a file that only LOOKS
			// compressed by name (no genuine header) must not be punished for a
			// misleading extension. Detection reads bytes, not filenames.
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "not-really.gz"), `import a from "https://cdn.evil.example/x.js";`);

			const { violations, scanned, compressed } = scanDist(dir);

			expect(compressed).toHaveLength(0);
			expect(scanned).toHaveLength(1);
			expect(violations).toHaveLength(1);
			expect(violations[0].url).toBe("https://cdn.evil.example/x.js");
		});
	});
});
