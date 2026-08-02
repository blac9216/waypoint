import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { scanDist } from "./check-no-external-assets.mjs";

/**
 * Proves the external-asset guard actually guards (issue #11 acceptance
 * criterion: "demonstrated it fails when fed a deliberate external URL").
 * Also a regression test for the vendor allowlist: exactly the three known
 * inert patterns are exempted, nothing else.
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

	it("skips binary/non-text extensions (e.g. .png) even if they happened to contain the bytes", () => {
		dir = mkdtempSync(join(tmpdir(), "waypoint-fixture-"));
		writeFileSync(join(dir, "icon.png"), Buffer.from(`fake-binary https://evil.example/x`));

		const { violations } = scanDist(dir);
		expect(violations).toHaveLength(0);
	});
});
