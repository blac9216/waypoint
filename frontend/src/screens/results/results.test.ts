/**
 * Pure-function unit tests for results.ts's wire-shape helpers (issue #307).
 * `fetchRunArtifacts`/`fetchAttestationsApplied` themselves are exercised
 * end-to-end via ResultsScreen.test.tsx (they're thin `apiGet` wrappers);
 * this file covers `parseAttestationScope`, the one piece of real parsing
 * logic added for the `layer:ref` scope form.
 */
import { describe, expect, it } from "vitest";
import { parseAttestationScope } from "./results";

describe("parseAttestationScope", () => {
	it("splits a target-layer scope into layer and ref", () => {
		expect(parseAttestationScope("target:11111111-1111-1111-1111-111111111111")).toEqual({
			layer: "target",
			ref: "11111111-1111-1111-1111-111111111111",
		});
	});

	it("splits a site-layer scope into layer and ref", () => {
		expect(parseAttestationScope("site:22222222-2222-2222-2222-222222222222")).toEqual({
			layer: "site",
			ref: "22222222-2222-2222-2222-222222222222",
		});
	});

	it("treats the bare global scope as a layer with no ref", () => {
		expect(parseAttestationScope("global")).toEqual({ layer: "global", ref: null });
	});

	it("falls back to treating an unrecognized shape as the whole string, not throwing", () => {
		expect(parseAttestationScope("")).toEqual({ layer: "", ref: null });
	});
});
