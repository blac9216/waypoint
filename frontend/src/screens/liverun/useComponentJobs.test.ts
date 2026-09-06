import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useAuth } from "../../lib/auth-context";
import { fetchComponentJobCounts, fetchComponentJobsPage } from "./componentJobs";
import { useComponentJobs } from "./useComponentJobs";

/**
 * Issue #1314: `useComponentJobs` sat at 2% line coverage — entirely
 * unexercised — once `src/screens/**` was included in the coverage gate.
 * These tests pin its bounded-load contract: signed-out/no-run clears state
 * without fetching, a signed-in load fires the counts + first-page calls in
 * parallel, `loadMore` resumes from the returned cursor, and a fetch failure
 * on either path surfaces as `error` without throwing.
 */
vi.mock("../../lib/auth-context", () => ({
	useAuth: vi.fn(),
}));

vi.mock("./componentJobs", async (importOriginal) => {
	const actual = await importOriginal<typeof import("./componentJobs")>();
	return {
		...actual,
		fetchComponentJobCounts: vi.fn(),
		fetchComponentJobsPage: vi.fn(),
	};
});

const mockUseAuth = vi.mocked(useAuth);
const mockFetchCounts = vi.mocked(fetchComponentJobCounts);
const mockFetchPage = vi.mocked(fetchComponentJobsPage);

function count(state: string, n: number) {
	return { priority: 3, component_kind: "esxi", state, count: n };
}

function item(id: string) {
	return {
		id,
		job_type: "scan",
		target_id: null,
		target_name: null,
		state: "queued",
		stage: null,
		priority: 3,
		component_kind: "esxi",
		attempt_count: 0,
		created_at: null,
		started_at: null,
		finished_at: null,
	};
}

afterEach(() => {
	vi.clearAllMocks();
});

describe("useComponentJobs", () => {
	it("clears state and fetches nothing when signed out", () => {
		mockUseAuth.mockReturnValue({ status: "signed-out" } as ReturnType<typeof useAuth>);

		const { result } = renderHook(() => useComponentJobs("run-1", {}));

		expect(result.current.counts).toEqual([]);
		expect(result.current.items).toEqual([]);
		expect(result.current.loading).toBe(false);
		expect(mockFetchCounts).not.toHaveBeenCalled();
	});

	it("clears state and fetches nothing when there is no runId", () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);

		const { result } = renderHook(() => useComponentJobs(undefined, {}));

		expect(result.current.items).toEqual([]);
		expect(mockFetchCounts).not.toHaveBeenCalled();
	});

	it("loads counts and the first page in parallel when signed in with a run", async () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);
		mockFetchCounts.mockResolvedValueOnce([count("queued", 5)]);
		mockFetchPage.mockResolvedValueOnce({ items: [item("j1")], nextCursor: "cursor-1" });

		const { result } = renderHook(() => useComponentJobs("run-1", {}));

		await waitFor(() => expect(result.current.loading).toBe(false));

		expect(result.current.counts).toEqual([count("queued", 5)]);
		expect(result.current.items.map((i) => i.id)).toEqual(["j1"]);
		expect(result.current.hasMore).toBe(true);
		expect(result.current.error).toBeNull();
	});

	it("loadMore appends the next page using the returned cursor and clears loadingMore when done", async () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);
		mockFetchCounts.mockResolvedValueOnce([]);
		mockFetchPage.mockResolvedValueOnce({ items: [item("j1")], nextCursor: "cursor-1" });

		const { result } = renderHook(() => useComponentJobs("run-1", {}));
		await waitFor(() => expect(result.current.loading).toBe(false));

		mockFetchPage.mockResolvedValueOnce({ items: [item("j2")], nextCursor: null });
		await act(async () => {
			result.current.loadMore();
		});
		await waitFor(() => expect(result.current.items).toHaveLength(2));

		expect(result.current.items.map((i) => i.id)).toEqual(["j1", "j2"]);
		expect(result.current.hasMore).toBe(false);
		expect(result.current.loadingMore).toBe(false);
		expect(mockFetchPage.mock.calls[1][2]).toBe("cursor-1");
	});

	it("loadMore is a no-op once there is no next cursor", async () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);
		mockFetchCounts.mockResolvedValueOnce([]);
		mockFetchPage.mockResolvedValueOnce({ items: [], nextCursor: null });

		const { result } = renderHook(() => useComponentJobs("run-1", {}));
		await waitFor(() => expect(result.current.loading).toBe(false));

		result.current.loadMore();

		expect(mockFetchPage).toHaveBeenCalledTimes(1);
	});

	it("surfaces a load failure as error rather than throwing", async () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);
		mockFetchCounts.mockRejectedValueOnce(new Error("boom"));
		mockFetchPage.mockResolvedValueOnce({ items: [], nextCursor: null });

		const { result } = renderHook(() => useComponentJobs("run-1", {}));

		await waitFor(() => expect(result.current.loading).toBe(false));
		expect(result.current.error).toBe("boom");
	});

	it("refresh() re-triggers the initial load", async () => {
		mockUseAuth.mockReturnValue({ status: "signed-in" } as ReturnType<typeof useAuth>);
		mockFetchCounts.mockResolvedValue([]);
		mockFetchPage.mockResolvedValue({ items: [], nextCursor: null });

		const { result } = renderHook(() => useComponentJobs("run-1", {}));
		await waitFor(() => expect(result.current.loading).toBe(false));
		expect(mockFetchCounts).toHaveBeenCalledTimes(1);

		act(() => {
			result.current.refresh();
		});
		await waitFor(() => expect(mockFetchCounts).toHaveBeenCalledTimes(2));
	});
});
