import { describe, expect, it } from "vitest";
import { roleAtLeast, roleGateProps, roleRank } from "./roles";

describe("roleAtLeast", () => {
	it("orders Viewer < Cyber < Operator < Admin", () => {
		expect(roleRank("Viewer")).toBeLessThan(roleRank("Cyber"));
		expect(roleRank("Cyber")).toBeLessThan(roleRank("Operator"));
		expect(roleRank("Operator")).toBeLessThan(roleRank("Admin"));
	});

	it("a role satisfies its own requirement", () => {
		expect(roleAtLeast("Cyber", "Cyber")).toBe(true);
	});

	it("a lower role does not satisfy a higher requirement", () => {
		expect(roleAtLeast("Viewer", "Operator")).toBe(false);
	});

	it("a higher role satisfies a lower requirement", () => {
		expect(roleAtLeast("Admin", "Viewer")).toBe(true);
	});
});

describe("roleGateProps", () => {
	it("returns an enabled, unstyled control when the role is sufficient", () => {
		const props = roleGateProps("Admin", "Operator");
		expect(props.disabled).toBe(false);
		expect(props.style).toBeUndefined();
	});

	it("returns a visibly-disabled control (opacity 0.42) with a reason when the role is insufficient", () => {
		const props = roleGateProps("Viewer", "Admin", "Requires Admin — configuration is not available to Viewer");
		expect(props.disabled).toBe(true);
		expect(props.style).toEqual({ opacity: 0.42 });
		expect(props.title).toBe("Requires Admin — configuration is not available to Viewer");
	});

	it("falls back to a generated reason when none is given", () => {
		const props = roleGateProps("Cyber", "Operator");
		expect(props.title).toContain("Operator");
		expect(props.title).toContain("Cyber");
	});
});
