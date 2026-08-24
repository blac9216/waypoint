import { afterEach, describe, expect, it, vi } from "vitest";
import { setTokenGetter } from "../lib/api";
import { fetchRunHistory } from "./runHistory";

/**
 * Unit coverage for the typed `GET /runs/history` client (issue #708/#689,
 * epic #706). Mocks `fetch` directly, mirroring `jobEventHistory.test.ts`'s
 * house pattern, against the real `RunHistoryListResponse`/`RunResponse`
 * wire shape (backend/Waypoint.Api/Contracts/RunContracts.cs).
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

describe("fetchRunHistory", () => {
	it("requests /runs/history with no query params when none are given", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [], next_cursor: null }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await fetchRunHistory();

		expect(fetchMock).toHaveBeenCalledTimes(1);
		const [url] = fetchMock.mock.calls[0];
		expect(url).toBe("/api/v1/runs/history");
	});

	it("encodes state/run_type/since/until/cursor/limit as query params", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [], next_cursor: null }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		await fetchRunHistory({
			state: "completed,aborted",
			runType: "discover,download",
			since: "2026-01-01T00:00:00Z",
			until: "2026-06-01T00:00:00Z",
			cursor: "v1:abc",
			limit: 25,
		});

		const [url] = fetchMock.mock.calls[0];
		const parsed = new URL(url, "http://localhost");
		expect(parsed.pathname).toBe("/api/v1/runs/history");
		expect(parsed.searchParams.get("state")).toBe("completed,aborted");
		expect(parsed.searchParams.get("run_type")).toBe("discover,download");
		expect(parsed.searchParams.get("since")).toBe("2026-01-01T00:00:00Z");
		expect(parsed.searchParams.get("until")).toBe("2026-06-01T00:00:00Z");
		expect(parsed.searchParams.get("cursor")).toBe("v1:abc");
		expect(parsed.searchParams.get("limit")).toBe("25");
	});

	it("maps items and next_cursor through opaquely", async () => {
		const item = {
			id: "run-1",
			run_type: "discover",
			state: "completed",
			scope: "{}",
			initiated_by: "tester",
			created_at: "2026-01-01T00:00:00Z",
			started_at: "2026-01-01T00:00:00Z",
			completed_at: "2026-01-01T00:05:00Z",
			job_count: 1,
			job_count_queued: 0,
			job_count_running: 0,
			job_count_completed: 1,
			job_count_failed: 0,
			job_count_blocked: 0,
		};
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [item], next_cursor: "v1:xyz" }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const page = await fetchRunHistory();

		expect(page.items).toEqual([item]);
		expect(page.nextCursor).toBe("v1:xyz");
	});

	it("a null next_cursor maps to null, not undefined (never a silent truncation)", async () => {
		const fetchMock = vi.fn(async (..._args: FetchArgs) => jsonResponse({ items: [], next_cursor: null }));
		globalThis.fetch = fetchMock as unknown as typeof fetch;

		const page = await fetchRunHistory();

		expect(page.nextCursor).toBeNull();
	});
});
