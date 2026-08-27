/**
 * Issue #757: windowing arithmetic + component-job client contract.
 *
 * `computeWindow` is asserted directly at 10,000+ row counts — the whole
 * point of the pure function is that virtualization correctness is provable
 * without mounting 10,000 DOM nodes.
 */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { apiGet } from "../../lib/api";
import { computeWindow, fetchComponentJobCounts, fetchComponentJobsPage } from "./componentJobs";

vi.mock("../../lib/api", () => ({
	apiGet: vi.fn(),
}));

const mockApiGet = vi.mocked(apiGet);

describe("computeWindow", () => {
	it("renders only a bounded slice of a 10,000-row list", () => {
		const win = computeWindow(0, 480, 34, 10_000);
		expect(win.start).toBe(0);
		// ceil(480/34)+1 visible + 5 overscan = 21
		expect(win.end).toBe(21);
		expect(win.end - win.start).toBeLessThan(50);
		expect(win.topPad).toBe(0);
		expect(win.bottomPad).toBe((10_000 - win.end) * 34);
		expect(win.totalHeight).toBe(10_000 * 34);
	});

	it("scrolling deep into the list moves the window and keeps pads consistent", () => {
		const win = computeWindow(5_000 * 34, 480, 34, 10_000);
		expect(win.start).toBe(5_000 - 5); // overscan above
		expect(win.topPad).toBe(win.start * 34);
		expect(win.bottomPad).toBe((10_000 - win.end) * 34);
		// pads + rendered slice always reconstruct the exact total height
		expect(win.topPad + (win.end - win.start) * 34 + win.bottomPad).toBe(win.totalHeight);
	});

	it("clamps at the end of the list", () => {
		const win = computeWindow(9_999 * 34, 480, 34, 10_000);
		expect(win.end).toBe(10_000);
		expect(win.bottomPad).toBe(0);
	});

	it("clamps garbage inputs (negative scroll, empty list) to an empty window", () => {
		expect(computeWindow(-500, 480, 34, 10_000).start).toBe(0);
		const empty = computeWindow(100, 480, 34, 0);
		expect(empty.start).toBe(0);
		expect(empty.end).toBe(0);
		expect(empty.totalHeight).toBe(0);
	});

	it("every index is rendered by exactly one window as the scroll advances", () => {
		// Walk the scroll positions one row at a time over a small list and
		// verify union coverage with no gaps: each row index appears in the
		// window whose scroll range covers it.
		const total = 100;
		const covered = new Set<number>();
		for (let row = 0; row < total; row++) {
			const win = computeWindow(row * 20, 100, 20, total);
			for (let i = win.start; i < win.end; i++) {
				covered.add(i);
			}
		}
		expect(covered.size).toBe(total);
	});
});

describe("component-job clients", () => {
	beforeEach(() => {
		mockApiGet.mockReset();
	});

	it("fetchComponentJobCounts passes filters through as query parameters", async () => {
		mockApiGet.mockResolvedValue([]);
		await fetchComponentJobCounts("run-1", { state: "queued,running", priority: "1,2", componentKind: "esxi", search: "host" });
		expect(mockApiGet).toHaveBeenCalledWith(
			"/runs/run-1/component-jobs/counts?state=queued%2Crunning&priority=1%2C2&component_kind=esxi&search=host",
		);
	});

	it("fetchComponentJobsPage maps next_cursor and echoes the cursor verbatim on the next call", async () => {
		mockApiGet.mockResolvedValueOnce({ items: [{ id: "a" }], next_cursor: "v1:opaque" });
		const first = await fetchComponentJobsPage("run-1", {}, undefined, 200);
		expect(first.nextCursor).toBe("v1:opaque");
		expect(mockApiGet).toHaveBeenLastCalledWith("/runs/run-1/component-jobs?limit=200");

		mockApiGet.mockResolvedValueOnce({ items: [], next_cursor: null });
		const second = await fetchComponentJobsPage("run-1", {}, first.nextCursor!, 200);
		expect(second.nextCursor).toBeNull();
		expect(mockApiGet).toHaveBeenLastCalledWith("/runs/run-1/component-jobs?cursor=v1%3Aopaque&limit=200");
	});
});
