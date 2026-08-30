/**
 * Pure-function unit tests for results.ts's wire-shape helpers (issue #307).
 * `fetchRunArtifacts`/`fetchAttestationsApplied` themselves are exercised
 * end-to-end via ResultsScreen.test.tsx (they're thin `apiGet` wrappers);
 * this file covers `parseAttestationScope`, the one piece of real parsing
 * logic added for the `layer:ref` scope form.
 */
import { describe, expect, it } from "vitest";
import { controlsEvaluatedLabel, controlsUnderEvaluated, parseAttestationScope } from "./results";

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

describe("controlsEvaluatedLabel / controlsUnderEvaluated", () => {
	it("renders the genuine evaluated/total denominator when counts are available", () => {
		const row = { counts_available: true, controls_total: 12, controls_evaluated: 12 };
		expect(controlsEvaluatedLabel(row)).toBe("12/12");
		expect(controlsUnderEvaluated(row)).toBe(false);
	});

	it("flags an all-skipped/all-errored row (evaluated 0 of a nonzero total) as under-evaluated", () => {
		const row = { counts_available: true, controls_total: 69, controls_evaluated: 0 };
		expect(controlsEvaluatedLabel(row)).toBe("0/69");
		expect(controlsUnderEvaluated(row)).toBe(true);
	});

	it("flags a partially-evaluated row (evaluated less than total but not zero) as under-evaluated", () => {
		const row = { counts_available: true, controls_total: 10, controls_evaluated: 4 };
		expect(controlsEvaluatedLabel(row)).toBe("4/10");
		expect(controlsUnderEvaluated(row)).toBe(true);
	});

	it("renders 'n/a', never a fabricated 0/0, when counts_available is false", () => {
		const row = { counts_available: false, controls_total: undefined, controls_evaluated: undefined };
		expect(controlsEvaluatedLabel(row)).toBe("n/a");
		expect(controlsUnderEvaluated(row)).toBe(false);
	});
});
