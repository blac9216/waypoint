import { afterEach, describe, expect, it, vi } from "vitest";
import { setTokenGetter } from "../lib/api";
import { fetchAllJobEventHistory, fetchJobEventHistory } from "./jobEventHistory";

/**
 * Unit coverage for the typed `GET /runs/{id}/events/history` client (issue
 * #590, deferred from PR #684's review on issue #590: "the typed paged-history
 * client (cursor/`next_cursor` handling, `kind`/`level`/`job_id`/`limit`
 * params, and its tests per #581 AC6) should be delivered here"). Mocks
 * `fetch` directly (this module's own house pattern — `lib/api.test.ts`) so
 * assertions can inspect the exact URL/query string this client builds
 * against the real `RunEventHistoryResponse` wire shape
 * (backend/Waypoint.Api/Contracts/RunContracts.cs).
 */

const originalFetch = globalThis.fetch;

afterEach(() => {
	globalThis.fetch = originalFetch;
	vi.restoreAllMocks();
	setTokenGetter(() => null);
});

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

type FetchArgs = [url: string, init?: RequestInit];

describe("fetchJobEventHistory", () => {
	it("requests the run's history endpoint with no query params when none are given", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [], next_cursor: null }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await fetchJobEventHistory("run-1");

		expect(fetchMock).toHaveBeenCalledTimes(1);
		const [url] = fetchMock.mock.calls[0];
		expect(url).toBe("/api/v1/runs/run-1/events/history");
	});

	it("encodes job_id/kind/level/cursor/limit as query params", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [], next_cursor: null }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await fetchJobEventHistory("run-1", { jobId: "job-9", kind: "job.log,job.state", level: "error", cursor: "v1:abc", limit: 25 });

		const [url] = fetchMock.mock.calls[0];
		const parsed = new URL(url, "http://localhost");
		expect(parsed.pathname).toBe("/api/v1/runs/run-1/events/history");
		expect(parsed.searchParams.get("job_id")).toBe("job-9");
		expect(parsed.searchParams.get("kind")).toBe("job.log,job.state");
		expect(parsed.searchParams.get("level")).toBe("error");
		expect(parsed.searchParams.get("cursor")).toBe("v1:abc");
		expect(parsed.searchParams.get("limit")).toBe("25");
	});

	it("maps items to the WaypointEvent envelope shape and passes next_cursor through opaquely", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) =>
			jsonResponse({
				items: [
					{ seq: 1, ts: "2026-08-24T00:00:00Z", type: "job.log", run_id: "run-1", job_id: "job-9", data: { line: "hi" } },
					{ seq: 2, ts: "2026-08-24T00:00:01Z", type: "job.state", run_id: "run-1", job_id: null, data: { to: "done" } },
				],
				next_cursor: "v1:next",
			}),
		);
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const page = await fetchJobEventHistory("run-1");

		expect(page.nextCursor).toBe("v1:next");
		expect(page.items).toEqual([
			{ seq: 1, ts: "2026-08-24T00:00:00Z", type: "job.log", run_id: "run-1", job_id: "job-9", data: { line: "hi" } },
			{ seq: 2, ts: "2026-08-24T00:00:01Z", type: "job.state", run_id: "run-1", job_id: undefined, data: { to: "done" } },
		]);
	});

	it("widens an unrecognized event type to system.notice rather than dropping the row", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) =>
			jsonResponse({ items: [{ seq: 1, ts: "t", type: "some.future.type", run_id: null, job_id: null, data: {} }], next_cursor: null }),
		);
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const page = await fetchJobEventHistory("run-1");

		expect(page.items).toHaveLength(1);
		expect(page.items[0].type).toBe("system.notice");
	});

	it("surfaces a 404 for a nonexistent run as an ApiError", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ error: { code: "not_found", message: "Run not found." } }, 404));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await expect(fetchJobEventHistory("missing-run")).rejects.toMatchObject({ status: 404, code: "not_found" });
	});

	it("surfaces a 400 validation_error for a malformed cursor without retrying", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ error: { code: "validation_error", message: "cursor is malformed." } }, 400));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await expect(fetchJobEventHistory("run-1", { cursor: "garbage" })).rejects.toMatchObject({ status: 400, code: "validation_error" });
		expect(fetchMock).toHaveBeenCalledTimes(1);
	});
});

describe("fetchAllJobEventHistory", () => {
	it("follows next_cursor across pages until it goes null, reporting truncated: false", async () => {
		const pages = [
			{ items: [{ seq: 1, ts: "t1", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: "v1:p2" },
			{ items: [{ seq: 2, ts: "t2", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: "v1:p3" },
			{ items: [{ seq: 3, ts: "t3", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: null },
		];
		let call = 0;
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse(pages[call++]));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const result = await fetchAllJobEventHistory("run-1");

		expect(fetchMock).toHaveBeenCalledTimes(3);
		expect(result.events.map((e) => e.seq)).toEqual([1, 2, 3]);
		expect(result.truncated).toBe(false);
		expect(result.nextCursor).toBeNull();
		// Second call's cursor param must be the first page's opaque next_cursor, verbatim.
		const secondCallUrl = new URL(fetchMock.mock.calls[1][0] as string, "http://localhost");
		expect(secondCallUrl.searchParams.get("cursor")).toBe("v1:p2");
		const thirdCallUrl = new URL(fetchMock.mock.calls[2][0] as string, "http://localhost");
		expect(thirdCallUrl.searchParams.get("cursor")).toBe("v1:p3");
	});

	it("stops paging once stopAfter is reached and reports truncated: true with the resume cursor, even though next_cursor is still non-null", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) =>
			jsonResponse({ items: [{ seq: 1, ts: "t", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: "v1:more" }),
		);
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const result = await fetchAllJobEventHistory("run-1", {}, 2);

		expect(result.events).toHaveLength(2);
		expect(result.truncated).toBe(true);
		expect(result.nextCursor).toBe("v1:more");
		expect(fetchMock).toHaveBeenCalledTimes(2);
	});

	it("returns a single page's items when the first page already has a null next_cursor", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) =>
			jsonResponse({ items: [{ seq: 1, ts: "t", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: null }),
		);
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const result = await fetchAllJobEventHistory("run-1");

		expect(result.events).toHaveLength(1);
		expect(result.truncated).toBe(false);
		expect(fetchMock).toHaveBeenCalledTimes(1);
	});

	it("resumes from an explicit resumeCursor without re-fetching earlier pages (the #721 continuation contract)", async () => {
		const pages = [
			{ items: [{ seq: 11, ts: "t11", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: "v1:p12" },
			{ items: [{ seq: 12, ts: "t12", type: "job.log", run_id: "run-1", job_id: "j-1", data: {} }], next_cursor: null },
		];
		let call = 0;
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse(pages[call++]));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const result = await fetchAllJobEventHistory("run-1", {}, 1000, "v1:p11");

		expect(fetchMock).toHaveBeenCalledTimes(2);
		const firstCallUrl = new URL(fetchMock.mock.calls[0][0] as string, "http://localhost");
		expect(firstCallUrl.searchParams.get("cursor")).toBe("v1:p11");
		expect(result.events.map((e) => e.seq)).toEqual([11, 12]);
		expect(result.truncated).toBe(false);
	});
});
