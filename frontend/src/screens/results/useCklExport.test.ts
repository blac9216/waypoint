import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { RunJobItem, RunListItem } from "./results";
import { useCklExport } from "./useCklExport";

/**
 * Issue #1314: `useCklExport` sat at ~10% line coverage once `src/screens/**`
 * was included in the gate — the export path itself (a raw, hand-authenticated
 * `fetch` per job, zipped client-side) was never exercised. These tests pin
 * the bearer-token attach, the "one target's failure must not abort the
 * bundle" contract in the module's own doc comment, and the no-op guard when
 * there is no run to export.
 */
const RUN: RunListItem = {
	id: "run-1",
	run_type: "scan",
	state: "completed",
	scope: "site",
	initiated_by: null,
	created_at: "2026-01-01T00:00:00Z",
	started_at: null,
	completed_at: null,
	job_count: 2,
	job_count_queued: 0,
	job_count_running: 0,
	job_count_completed: 2,
} as RunListItem;

function job(id: string, targetName: string | null): RunJobItem {
	return {
		id,
		job_type: "scan",
		target_id: null,
		target_name: targetName,
		state: "completed",
		stage: null,
		attempt_count: 1,
		created_at: "2026-01-01T00:00:00Z",
		started_at: null,
		finished_at: null,
	};
}

afterEach(() => {
	vi.unstubAllGlobals();
	vi.clearAllMocks();
});

describe("useCklExport", () => {
	it("handleExport is a no-op when there is no run", async () => {
		const fetchMock = vi.fn();
		vi.stubGlobal("fetch", fetchMock);

		const { result } = renderHook(() => useCklExport(null, [job("j1", "esxi-01")], "token-a"));
		await act(async () => {
			await result.current.handleExport();
		});

		expect(fetchMock).not.toHaveBeenCalled();
		expect(result.current.exporting).toBe(false);
	});

	it("attaches the bearer token, fetches each job's CKL, and builds a downloadable zip", async () => {
		const fetchMock = vi.fn().mockResolvedValue({
			ok: true,
			arrayBuffer: async () => new TextEncoder().encode("<CHECKLIST/>").buffer,
		});
		vi.stubGlobal("fetch", fetchMock);
		const createObjectURL = vi.fn(() => "blob:mock-url");
		const revokeObjectURL = vi.fn();
		vi.stubGlobal("URL", { ...URL, createObjectURL, revokeObjectURL });

		const { result } = renderHook(() => useCklExport(RUN, [job("j1", "esxi-01")], "token-a"));
		await act(async () => {
			await result.current.handleExport();
		});

		expect(fetchMock).toHaveBeenCalledTimes(1);
		const [, init] = fetchMock.mock.calls[0];
		expect(init.headers.Authorization).toBe("Bearer token-a");
		expect(createObjectURL).toHaveBeenCalled();
		expect(revokeObjectURL).toHaveBeenCalledWith("blob:mock-url");
		expect(result.current.exporting).toBe(false);
	});

	it("omits the Authorization header when there is no token", async () => {
		const fetchMock = vi.fn().mockResolvedValue({ ok: true, arrayBuffer: async () => new ArrayBuffer(0) });
		vi.stubGlobal("fetch", fetchMock);
		vi.stubGlobal("URL", { ...URL, createObjectURL: vi.fn(() => "blob:mock-url"), revokeObjectURL: vi.fn() });

		const { result } = renderHook(() => useCklExport(RUN, [job("j1", "esxi-01")], null));
		await act(async () => {
			await result.current.handleExport();
		});

		const [, init] = fetchMock.mock.calls[0];
		expect(init.headers.Authorization).toBeUndefined();
	});

	it("skips a job whose fetch is not ok, and continues the bundle for the rest (must not abort on one failure)", async () => {
		const fetchMock = vi
			.fn()
			.mockResolvedValueOnce({ ok: false, arrayBuffer: async () => new ArrayBuffer(0) })
			.mockResolvedValueOnce({ ok: true, arrayBuffer: async () => new TextEncoder().encode("<CHECKLIST/>").buffer });
		vi.stubGlobal("fetch", fetchMock);
		vi.stubGlobal("URL", { ...URL, createObjectURL: vi.fn(() => "blob:mock-url"), revokeObjectURL: vi.fn() });

		const { result } = renderHook(() => useCklExport(RUN, [job("j1", "esxi-01"), job("j2", "esxi-02")], "t"));
		await act(async () => {
			await result.current.handleExport();
		});

		expect(fetchMock).toHaveBeenCalledTimes(2);
		expect(result.current.exporting).toBe(false);
	});

	it("swallows a thrown fetch error for one job and still resolves (must not abort on one failure)", async () => {
		const fetchMock = vi
			.fn()
			.mockRejectedValueOnce(new Error("network down"))
			.mockResolvedValueOnce({ ok: true, arrayBuffer: async () => new TextEncoder().encode("<CHECKLIST/>").buffer });
		vi.stubGlobal("fetch", fetchMock);
		vi.stubGlobal("URL", { ...URL, createObjectURL: vi.fn(() => "blob:mock-url"), revokeObjectURL: vi.fn() });

		const { result } = renderHook(() => useCklExport(RUN, [job("j1", "esxi-01"), job("j2", "esxi-02")], "t"));

		await act(async () => {
			await result.current.handleExport();
		});

		expect(result.current.exporting).toBe(false);
	});

	it("sets exporting true while the download is in flight and false once it settles", async () => {
		let resolveFetch: (v: unknown) => void = () => {};
		const fetchMock = vi.fn(
			() =>
				new Promise((resolve) => {
					resolveFetch = resolve;
				}),
		);
		vi.stubGlobal("fetch", fetchMock);
		vi.stubGlobal("URL", { ...URL, createObjectURL: vi.fn(() => "blob:mock-url"), revokeObjectURL: vi.fn() });

		const { result } = renderHook(() => useCklExport(RUN, [job("j1", "esxi-01")], "t"));

		let exportPromise!: Promise<void>;
		act(() => {
			exportPromise = result.current.handleExport();
		});
		await waitFor(() => expect(result.current.exporting).toBe(true));

		resolveFetch({ ok: true, arrayBuffer: async () => new ArrayBuffer(0) });
		await act(async () => {
			await exportPromise;
		});

		expect(result.current.exporting).toBe(false);
	});
});
