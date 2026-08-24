import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { LiveJobsScreen } from "./LiveJobsScreen";

/**
 * Issue #708/#689 coverage: History mode's server-filtered/cursor-paged run
 * browsing, windowing (compliance runs excluded from the default view but
 * reachable via the explicit toggle), tombstone rendering for a
 * history-deleted run, and the Active/History mode toggle itself (including
 * the "Live Jobs" -> "Jobs" title rename, epic #706's scope addition).
 * Fixtures are the real wire shapes `RunsController`/`RunContracts.cs` send,
 * matching `LiveJobsScreen.test.tsx`'s discipline.
 */

interface RunWireFixture {
	id: string;
	run_type: string;
	state: string;
	paused: boolean;
	blocked: boolean;
	blocked_reason: string | null;
	scope: string;
	credential_id: string | null;
	initiated_by: string | null;
	created_at: string;
	started_at: string | null;
	completed_at: string | null;
	job_count: number;
	job_count_queued: number;
	job_count_running: number;
	job_count_completed: number;
	job_count_failed: number;
	job_count_blocked: number;
}

const HISTORY_RUN: RunWireFixture = {
	id: "run-old-1",
	run_type: "discover",
	state: "completed",
	paused: false,
	blocked: false,
	blocked_reason: null,
	scope: "{}",
	credential_id: null,
	initiated_by: "j.moreno",
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

const HISTORY_RUN_JOB = {
	id: "job-old-1",
	run_id: "run-old-1",
	job_type: "discover",
	target_id: null,
	target_name: "discover-target",
	state: "done",
	stage: null,
	priority: 4,
	attempt_count: 1,
	created_at: "2026-01-01T00:00:00Z",
	started_at: "2026-01-01T00:00:00Z",
	finished_at: "2026-01-01T00:05:00Z",
};

interface MockOptions {
	historyRuns?: RunWireFixture[];
	historyNextCursor?: string | null;
	/** `run_id -> tombstone status | 404` for `GET /runs/{id}/history`. */
	tombstones?: Record<string, { outcome: string; actor: string; prior_state: string; occurred_at: string } | undefined>;
	jobsByRun?: Record<string, unknown[]>;
}

function installFetchMock(opts: MockOptions) {
	const historyRuns = opts.historyRuns ?? [HISTORY_RUN];
	const jobsByRun = opts.jobsByRun ?? { "run-old-1": [HISTORY_RUN_JOB] };

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const accept = new Headers(init?.headers).get("Accept");

		// Active-mode seed (LiveJobsScreen always mounts useLiveJobs regardless
		// of which mode is initially selected) -- empty active set by default.
		if (url.startsWith("/api/v1/runs?")) {
			return new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url.startsWith("/api/v1/runs/history")) {
			return new Response(
				JSON.stringify({ items: historyRuns, next_cursor: opts.historyNextCursor ?? null }),
				{ status: 200, headers: { "Content-Type": "application/json" } },
			);
		}
		const historyStatusMatch = /^\/api\/v1\/runs\/([^/]+)\/history$/.exec(url);
		if (historyStatusMatch) {
			const tombstone = opts.tombstones?.[historyStatusMatch[1]];
			if (!tombstone) {
				return new Response(JSON.stringify({ error: { code: "not_found", message: "not found" } }), { status: 404 });
			}
			return new Response(
				JSON.stringify({ run_id: historyStatusMatch[1], ...tombstone }),
				{ status: 200, headers: { "Content-Type": "application/json" } },
			);
		}
		const jobsMatch = /^\/api\/v1\/runs\/([^/]+)\/jobs$/.exec(url);
		if (jobsMatch) {
			const jobs = jobsByRun[jobsMatch[1]] ?? [];
			return new Response(JSON.stringify(jobs), { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url === "/api/v1/auth/me") {
			return new Response(JSON.stringify({ username: "j.moreno", role: "Admin" }), { status: 200 });
		}
		if (accept === "text/event-stream") {
			return { ok: true, status: 200, body: { getReader: () => ({ read: () => new Promise(() => {}), releaseLock() {} }) } } as unknown as Response;
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithAuth() {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({ token: "tok", username: "j.moreno", role: "Admin", expiresAt: new Date(Date.now() + 3600_000).toISOString() }),
	);
	return render(
		<AuthProvider>
			<RouterProvider>
				<LiveJobsScreen />
			</RouterProvider>
		</AuthProvider>,
	);
}

describe("Jobs workspace History mode (issue #708/#689)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		sessionStorage.clear();
		window.history.pushState(null, "", "/live-jobs");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		sessionStorage.clear();
	});

	it("renames the workspace title from 'Live Jobs' to 'Jobs' (epic #706 scope addition)", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByRole("heading", { name: "Jobs" })).toBeInTheDocument());
		expect(screen.queryByText("Live Jobs")).not.toBeInTheDocument();
	});

	it("opens directly into History mode via ?mode=history and lists terminal runs", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());
		expect(screen.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
	});

	it("switches from Active to History mode via the toggle and calls GET /runs/history", async () => {
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("No active runs or jobs right now.")).toBeInTheDocument());
		fireEvent.click(screen.getByRole("tab", { name: "History" }));

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());
		expect(globalThis.fetch).toHaveBeenCalledWith(expect.stringContaining("/api/v1/runs/history"), expect.anything());
	});

	it("windows out compliance runs (scan/remediate) by default but keeps them reachable via the toggle", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());

		// Default request must exclude scan/remediate from run_type.
		const defaultCall = (globalThis.fetch as unknown as { mock: { calls: [string][] } }).mock.calls.find(([u]) =>
			u.startsWith("/api/v1/runs/history"),
		)!;
		expect(defaultCall[0]).toContain("run_type=");
		expect(defaultCall[0]).not.toContain("run_type=scan");
		const decoded = decodeURIComponent(defaultCall[0]);
		expect(decoded).not.toContain("scan");
		expect(decoded).not.toContain("remediate");

		// Flipping the toggle re-requests with no run_type restriction (widened
		// to include compliance types) -- windowing is a default filter, not a
		// deletion, so this is just a normal filtered re-query.
		fireEvent.click(screen.getByLabelText(/Include compliance runs/));
		await waitFor(() => {
			const calls = (globalThis.fetch as unknown as { mock: { calls: [string][] } }).mock.calls;
			const widened = calls.find(([u]) => u.startsWith("/api/v1/runs/history") && !u.includes("run_type="));
			expect(widened).toBeDefined();
		});
	});

	it("includes the non-compliance types migration 0042 added in the default run_type filter", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());

		const defaultCall = (globalThis.fetch as unknown as { mock: { calls: [string][] } }).mock.calls.find(([u]) =>
			u.startsWith("/api/v1/runs/history"),
		)!;
		const decoded = decodeURIComponent(defaultCall[0]);
		// credential-test/tool-install/purge are browsable non-compliance history and
		// must be in the default (compliance-excluded) view -- not silently hidden.
		expect(decoded).toContain("credential-test");
		expect(decoded).toContain("tool-install");
		expect(decoded).toContain("purge");
	});

	it("shows the honest tombstone state for a history-deleted run instead of its detail", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({
			tombstones: {
				"run-old-1": { outcome: "Completed", actor: "admin-1", prior_state: "completed", occurred_at: "2026-02-01T00:00:00Z" },
			},
		});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());
		fireEvent.click(screen.getByText("run-old-1"));

		await waitFor(() => expect(screen.getByText(/operational history was deleted by admin-1/)).toBeInTheDocument());
		// The job list/detail must not render for a tombstoned run.
		expect(screen.queryByText("discover-target")).not.toBeInTheDocument();
	});

	it("renders run detail with expandable job rows for a non-deleted history run", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({});
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("run-old-1")).toBeInTheDocument());
		fireEvent.click(screen.getByText("run-old-1"));

		await waitFor(() => expect(screen.getByText("discover-target")).toBeInTheDocument());

		const detailPane = screen.getByText("discover-target").closest(".live-jobs__detail-pane") as HTMLElement;
		expect(within(detailPane).getByText("discover-target")).toBeInTheDocument();
	});

	it("shows a Load more control when the server reports more matching history", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({ historyNextCursor: "v1:opaque-cursor" });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Load more")).toBeInTheDocument());
	});

	it("shows an honest empty state when no history matches the filters", async () => {
		window.history.pushState(null, "", "/live-jobs?mode=history");
		installFetchMock({ historyRuns: [] });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("No matching run history.")).toBeInTheDocument());
	});
});
