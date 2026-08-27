import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useJobHistory } from "./useJobHistory";
import { fetchAllJobEventHistory } from "../../api/jobEventHistory";

/**
 * Issue #721: `fetchAllJobEventHistory` used to silently stop at an implicit
 * 5,000-event bound and `useJobHistory` presented the truncated prefix as
 * complete history, with no indicator or way to fetch the remainder. These
 * tests pin the honest-completeness contract this hook now owns: a batch
 * bound reached mid-history surfaces `truncated: true`, and a working
 * `loadMore` appends the next batch by resuming from the exact cursor the
 * previous batch returned (the fixed regression repro in the issue) rather
 * than restarting from page one.
 *
 * `api/jobEventHistory.ts` itself is mocked here (its own paging/cursor
 * behavior is covered directly by `jobEventHistory.test.ts`) so this suite
 * can deterministically drive the hook through a truncated batch without
 * needing a history long enough to exceed the real default `stopAfter`.
 */
vi.mock("../../api/jobEventHistory", () => ({
	fetchAllJobEventHistory: vi.fn(),
}));

const mockFetchAll = vi.mocked(fetchAllJobEventHistory);

function makeEvent(seq: number) {
	return { seq, ts: `t${seq}`, type: "job.log" as const, run_id: "run-1", job_id: "job-1", data: { line: `line ${seq}` } };
}

afterEach(() => {
	vi.clearAllMocks();
});

describe("useJobHistory", () => {
	it("reports truncated: false and every event for a history that fits within one batch", async () => {
		mockFetchAll.mockResolvedValueOnce({ events: [makeEvent(1), makeEvent(2)], nextCursor: null, truncated: false });

		const { result } = renderHook(() => useJobHistory("run-1", "job-1", true));

		await waitFor(() => expect(result.current.loading).toBe(false));

		expect(result.current.events.map((e) => e.seq)).toEqual([1, 2]);
		expect(result.current.truncated).toBe(false);
		expect(result.current.error).toBeNull();
		expect(mockFetchAll).toHaveBeenCalledTimes(1);
	});

	it("surfaces truncated: true when the batch bound is reached, and loadMore resumes from the returned cursor to append the remainder", async () => {
		mockFetchAll.mockResolvedValueOnce({ events: [makeEvent(1), makeEvent(2)], nextCursor: "v1:p2", truncated: true });

		const { result } = renderHook(() => useJobHistory("run-1", "job-1", true));
		await waitFor(() => expect(result.current.loading).toBe(false));

		expect(result.current.events.map((e) => e.seq)).toEqual([1, 2]);
		expect(result.current.truncated).toBe(true);

		mockFetchAll.mockResolvedValueOnce({ events: [makeEvent(3)], nextCursor: null, truncated: false });
		await act(async () => {
			result.current.loadMore();
		});
		await waitFor(() => expect(result.current.events).toHaveLength(3));

		expect(result.current.events.map((e) => e.seq)).toEqual([1, 2, 3]);
		expect(result.current.truncated).toBe(false);
		expect(mockFetchAll).toHaveBeenCalledTimes(2);
		// loadMore must resume from the exact opaque cursor the truncated batch
		// returned, not restart from page one (the issue's cursor-respecting
		// paging regression).
		expect(mockFetchAll.mock.calls[1]).toEqual(["run-1", { jobId: "job-1" }, undefined, "v1:p2"]);
	});

	it("loadMore is a no-op while not truncated (nothing more to fetch)", async () => {
		mockFetchAll.mockResolvedValueOnce({ events: [makeEvent(1)], nextCursor: null, truncated: false });

		const { result } = renderHook(() => useJobHistory("run-1", "job-1", true));
		await waitFor(() => expect(result.current.loading).toBe(false));

		result.current.loadMore();

		expect(mockFetchAll).toHaveBeenCalledTimes(1);
		expect(result.current.events).toHaveLength(1);
	});

	it("does not fetch when disabled, and clears state when re-disabled", async () => {
		mockFetchAll.mockResolvedValueOnce({ events: [makeEvent(1)], nextCursor: null, truncated: false });

		const { result, rerender } = renderHook(({ enabled }: { enabled: boolean }) => useJobHistory("run-1", "job-1", enabled), {
			initialProps: { enabled: false },
		});

		expect(result.current.events).toEqual([]);
		expect(mockFetchAll).not.toHaveBeenCalled();

		rerender({ enabled: true });
		await waitFor(() => expect(result.current.loading).toBe(false));
		expect(result.current.events).toHaveLength(1);

		rerender({ enabled: false });
		expect(result.current.events).toEqual([]);
		expect(result.current.truncated).toBe(false);
	});
});
