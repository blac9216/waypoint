/**
 * Pure helpers in dashboard.ts — issue #526 covers `complianceTone`, the
 * SITE POSTURE compliance bar/percentage color mapping (prototype screen 2:
 * `--ok` >=90, `--warn` >=82, else `--bad`).
 */
import { describe, expect, it } from "vitest";
import { complianceTone, isComplianceRun } from "./dashboard";

describe("complianceTone", () => {
	it("returns ok at the 90 boundary", () => {
		expect(complianceTone(90)).toBe("ok");
	});

	it("returns warn just below the 90 boundary", () => {
		expect(complianceTone(89.9)).toBe("warn");
	});

	it("returns warn at the 82 boundary", () => {
		expect(complianceTone(82)).toBe("warn");
	});

	it("returns bad just below the 82 boundary", () => {
		expect(complianceTone(81.9)).toBe("bad");
	});

	it("returns ok above 90", () => {
		expect(complianceTone(100)).toBe("ok");
	});

	it("returns bad well below 82", () => {
		expect(complianceTone(60)).toBe("bad");
	});

	it("returns bad for the null no-scan-data case", () => {
		expect(complianceTone(null)).toBe("bad");
	});
});

describe("isComplianceRun", () => {
	it("accepts scan and remediate", () => {
		expect(isComplianceRun("scan")).toBe(true);
		expect(isComplianceRun("remediate")).toBe(true);
	});

	it("rejects operational run types (issue #717)", () => {
		for (const runType of ["discover", "credential-test", "content-pull", "content-import", "catalog-index", "tool-install", "purge"]) {
			expect(isComplianceRun(runType)).toBe(false);
		}
	});
});
