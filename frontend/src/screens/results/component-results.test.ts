/**
 * Issue #745/#744 remainders: pure coverage/severity math over the
 * `GET /runs/{id}/component-results/summary` rollup shape, plus the client
 * contract for both new read endpoints. Mirrors `results-metrics.ts`'s
 * "never fabricate a count" precedent and `componentJobs.test.ts`'s
 * mocked-`apiGet` pattern.
 */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiGet } from "../../lib/api";
import {
	componentResultStatusClass,
	componentResultStatusLabel,
	fetchComponentResultsSummary,
	fetchUploadAttempts,
	totalOpenBySeverity,
	totalResultedComponents,
	unresultedComponentCount,
	type ComponentResultRollup,
} from "./component-results";

vi.mock("../../lib/api", () => ({
	apiGet: vi.fn(),
}));

const mockApiGet = vi.mocked(apiGet);

const ROLLUP: ComponentResultRollup = {
	run_id: "run-1",
	planned_component_count: 5,
	by_status: [
		{
			status: "completed",
			component_count: 2,
			cat_i_open: 1,
			cat_ii_open: 3,
			cat_iii_open: 4,
			passed_count: 10,
			not_applicable_count: 1,
			not_reviewed_count: 0,
			skipped_count: 0,
			execution_error_count: 0,
		},
		{
			status: "execution_error",
			component_count: 1,
			cat_i_open: 0,
			cat_ii_open: 0,
			cat_iii_open: 0,
			passed_count: 0,
			not_applicable_count: 0,
			not_reviewed_count: 6,
			skipped_count: 0,
			execution_error_count: 6,
		},
	],
};

describe("totalResultedComponents / unresultedComponentCount", () => {
	it("sums resulted components across every status bucket", () => {
		expect(totalResultedComponents(ROLLUP)).toBe(3);
	});

	it("reports the honest coverage gap between planned and resulted", () => {
		expect(unresultedComponentCount(ROLLUP)).toBe(2);
	});

	it("clamps the gap at 0 rather than going negative if resulted somehow exceeds planned", () => {
		const impossible: ComponentResultRollup = { ...ROLLUP, planned_component_count: 1 };
		expect(unresultedComponentCount(impossible)).toBe(0);
	});

	it("reports 0 resulted / full gap for an empty rollup, never a fabricated full-coverage read", () => {
		const empty: ComponentResultRollup = { run_id: "run-2", planned_component_count: 5, by_status: [] };
		expect(totalResultedComponents(empty)).toBe(0);
		expect(unresultedComponentCount(empty)).toBe(5);
	});
});

describe("totalOpenBySeverity", () => {
	it("sums CAT I/II/III open counts across every bucket", () => {
		expect(totalOpenBySeverity(ROLLUP)).toEqual({ catI: 1, catII: 3, catIII: 4 });
	});

	it("returns all-zero totals for an empty rollup, not undefined/NaN", () => {
		const empty: ComponentResultRollup = { run_id: "run-2", planned_component_count: 0, by_status: [] };
		expect(totalOpenBySeverity(empty)).toEqual({ catI: 0, catII: 0, catIII: 0 });
	});
});

describe("componentResultStatusLabel / componentResultStatusClass", () => {
	it("gives each of the four closed statuses a distinct label and class", () => {
		const statuses = ["completed", "completed_zero_controls", "execution_error", "skipped"];
		const labels = statuses.map(componentResultStatusLabel);
		const classes = statuses.map(componentResultStatusClass);
		expect(new Set(labels).size).toBe(4);
		expect(new Set(classes).size).toBe(4);
		expect(labels).not.toContain("completed"); // always a human label, never the raw code
	});

	it("gives completed_zero_controls (migration 0081) its own label and class, never the --unknown fallback", () => {
		// Issue #1140 reviewer touchpoint: this status must never render the raw
		// code, and must never share the generic "unrecognized status" treatment
		// with a truly unknown value -- a reader of the table alone must not
		// mistake an evaluated-nothing component for a clean/completed one.
		expect(componentResultStatusLabel("completed_zero_controls")).not.toBe("completed_zero_controls");
		expect(componentResultStatusLabel("completed_zero_controls")).not.toBe(componentResultStatusLabel("completed"));
		expect(componentResultStatusClass("completed_zero_controls")).toBe("results__cresult-status--zero-controls");
		expect(componentResultStatusClass("completed_zero_controls")).not.toBe("results__cresult-status--unknown");
	});

	it("falls back to the raw status string/an 'unknown' class for a genuinely unrecognized value, never throwing", () => {
		expect(componentResultStatusLabel("mystery")).toBe("mystery");
		expect(componentResultStatusClass("mystery")).toBe("results__cresult-status--unknown");
	});
});

describe("fetchComponentResultsSummary / fetchUploadAttempts", () => {
	beforeEach(() => {
		mockApiGet.mockReset();
	});

	it("calls GET /runs/{id}/component-results/summary", async () => {
		mockApiGet.mockResolvedValue(ROLLUP);
		const result = await fetchComponentResultsSummary("run-1");
		expect(mockApiGet).toHaveBeenCalledWith("/runs/run-1/component-results/summary");
		expect(result).toBe(ROLLUP);
	});

	it("calls GET /jobs/{id}/upload-attempts", async () => {
		mockApiGet.mockResolvedValue([]);
		await fetchUploadAttempts("job-9");
		expect(mockApiGet).toHaveBeenCalledWith("/jobs/job-9/upload-attempts");
	});
});
