import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { scanDist } from "./check-no-external-assets.mjs";

/**
 * Proves the external-asset guard actually guards (issue #11 acceptance
 * criterion: "demonstrated it fails when fed a deliberate external URL").
 * Also a regression test for the vendor allowlist: exactly the three known
 * inert patterns are exempted, nothing else — and for the file-selection
 * model itself, which used to be an extension allowlist that silently
 * skipped `.mjs`/`.cjs`/`.xml`/extensionless files (PR #65 review, finding
 * #1). Those cases are the "scans by default" describe block below; they
 * fail if anyone reintroduces an allowlist.
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
	});
});
