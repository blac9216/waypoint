import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, gzipSync } from "node:zlib";
import { afterEach, describe, expect, it } from "vitest";
import { isCompressedArtifact, scanDist } from "./check-no-external-assets.mjs";

const SCRIPT_PATH = join(dirname(fileURLToPath(import.meta.url)), "check-no-external-assets.mjs");

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
	 * Regression tests for issue #77 finding (a). `.br`/`.gz`/`.zip` used to
	 * sit on SKIPPED_BINARY_EXTENSIONS, so a dist/ whose only JS payload was
	 * pre-compressed (carrying an external import in its decoded text) passed
	 * with "0 file(s) scanned" — reported success while inspecting nothing.
	 * The guard now fails closed instead: a compressed artifact's mere
	 * presence fails the build, whether or not it happens to carry a URL.
	 */
	describe("fails closed on compressed artifacts instead of silently skipping them", () => {
		it("isCompressedArtifact recognizes .br, .gz and .zip, case-insensitively", () => {
			expect(isCompressedArtifact("app.js.br")).toBe(true);
			expect(isCompressedArtifact("app.js.gz")).toBe(true);
			expect(isCompressedArtifact("bundle.zip")).toBe(true);
			expect(isCompressedArtifact("APP.JS.BR")).toBe(true);
			expect(isCompressedArtifact("app.js")).toBe(false);
		});

		it("scanDist reports a .br file as compressed, not scanned or skipped", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			writeFileSync(join(dir, "app.js.br"), brotliCompressSync(Buffer.from(payload)));

			const { violations, scanned, skipped, compressed } = scanDist(dir);

			expect(compressed).toEqual([join(dir, "app.js.br")]);
			expect(scanned).toHaveLength(0);
			expect(skipped).toHaveLength(0);
			// It is never scanned, so it can never be a "violation" either — its
			// mere presence is the failure, reported through `compressed`.
			expect(violations).toHaveLength(0);
		});

		it("scanDist reports a .gz file as compressed, not scanned or skipped", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			const payload = `import a from "https://cdn.evil.example/payload.js"; console.log(a);\n`;
			writeFileSync(join(dir, "app.js.gz"), gzipSync(Buffer.from(payload)));

			const { scanned, skipped, compressed } = scanDist(dir);

			expect(compressed).toEqual([join(dir, "app.js.gz")]);
			expect(scanned).toHaveLength(0);
			expect(skipped).toHaveLength(0);
		});

		it("CLI: exits 0 (\"0 file(s) scanned\") on the real build with no compressed output", () => {
			// Sanity check that a clean, uncompressed fixture is unaffected —
			// the counterpart to the two CLI failure cases below.
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
			expect(stderr).toMatch(/app\.js\.br/);
			expect(stderr).toMatch(/app\.js\.gz/);
		});

		it("CLI: a .zip artifact alone also fails the build", () => {
			dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
			writeFileSync(join(dir, "bundle.zip"), Buffer.from("PK\x03\x04not a real zip but has the extension"));

			const { status, stderr } = runCli(dir);

			expect(status).toBe(1);
			expect(stderr).toMatch(/1 compressed artifact\(s\) found/);
		});
	});
});
