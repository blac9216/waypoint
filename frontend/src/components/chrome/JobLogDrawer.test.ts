import { describe, expect, it } from "vitest";
import { clampDrawerHeight } from "./JobLogDrawer";

/** README "Global job log drawer" / "Drawer resize": "Drag-resize clamps to
 * [96px, 40% of window height]." — this is the load-bearing piece of the
 * whole drawer per the handoff's "Layout Rules Learned the Hard Way" #6. */
describe("clampDrawerHeight", () => {
	it("clamps below the 96px floor up to 96px", () => {
		expect(clampDrawerHeight(10, 1000)).toBe(96);
		expect(clampDrawerHeight(-500, 1000)).toBe(96);
	});

	it("clamps above 40vh down to 40vh", () => {
		expect(clampDrawerHeight(10000, 1000)).toBe(400);
	});

	it("passes through values inside the clamp untouched", () => {
		expect(clampDrawerHeight(200, 1000)).toBe(200);
	});

	it("re-derives the ceiling from the current viewport height (a resize can lower the ceiling below the floor)", () => {
		// A short window (e.g. 200px tall) has a 40vh ceiling of 80px, below
		// the 96px floor — the floor must still win so the drawer never
		// disappears entirely.
		expect(clampDrawerHeight(200, 200)).toBe(96);
	});

	it("boundary values are inclusive", () => {
		expect(clampDrawerHeight(96, 1000)).toBe(96);
		expect(clampDrawerHeight(400, 1000)).toBe(400);
	});
});
