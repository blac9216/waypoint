import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { RouterProvider } from "../../lib/router";
import { GenericJobDetail } from "./detailRenderers";
import type { LiveJobRow, LiveRunGroup } from "./livejobs";
import { useJobHistory } from "./useJobHistory";

/**
 * Issue #721: the terminal-job log panel used to render whatever
 * `useJobHistory` handed back with no indication that it might be an
 * incomplete prefix. These tests pin `GenericJobDetail`'s UI contract:
 * `truncated: true` renders a truncation indicator + a working "Load more
 * history" control that calls `loadMore`; `truncated: false` renders neither.
 */
vi.mock("./useJobHistory", () => ({
	useJobHistory: vi.fn(),
}));

const mockUseJobHistory = vi.mocked(useJobHistory);

function job(overrides: Partial<LiveJobRow> = {}): LiveJobRow {
	return {
		job_id: "job-1",
		run_id: "run-1",
		job_type: "unknown-future-type",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "done",
		stage: null,
		attempt_count: 1,
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:01Z",
		finished_at: "2026-08-24T00:05:00Z",
		lastLogLine: null,
		logLines: [],
		...overrides,
	};
}

function group(overrides: Partial<LiveRunGroup> = {}): LiveRunGroup {
	return {
		run_id: "run-1",
		run_type: "scan",
		state: "completed",
		paused: false,
		blocked: false,
		blocked_reason: null,
		scope: '{"site_id":"s-1"}',
		initiated_by: "j.moreno",
		created_at: "2026-08-24T00:00:00Z",
		started_at: "2026-08-24T00:00:00Z",
		completed_at: "2026-08-24T00:05:00Z",
		job_count: 1,
		job_count_completed: 1,
		job_count_failed: 0,
		jobs: [],
		...overrides,
	};
}

function renderDetail() {
	return render(
		<RouterProvider>
			<GenericJobDetail job={job()} group={group()} />
		</RouterProvider>,
	);
}

describe("GenericJobDetail terminal history (issue #721)", () => {
	it("shows a truncation indicator and a Load more history control when truncated, and calls loadMore on click", () => {
		const loadMore = vi.fn();
		mockUseJobHistory.mockReturnValue({
			events: [{ seq: 1, ts: "t1", type: "job.log", run_id: "run-1", job_id: "job-1", data: { line: "hi" } }],
			loading: false,
			error: null,
			truncated: true,
			loadMore,
		});

		renderDetail();

		expect(screen.getByRole("status")).toHaveTextContent("Showing the first 1 events — more history exists.");
		const button = screen.getByRole("button", { name: "Load more history" });
		fireEvent.click(button);
		expect(loadMore).toHaveBeenCalledTimes(1);
	});

	it("shows no truncation indicator when the full history was retrieved", () => {
		mockUseJobHistory.mockReturnValue({
			events: [{ seq: 1, ts: "t1", type: "job.log", run_id: "run-1", job_id: "job-1", data: { line: "hi" } }],
			loading: false,
			error: null,
			truncated: false,
			loadMore: vi.fn(),
		});

		renderDetail();

		expect(screen.queryByRole("status")).not.toBeInTheDocument();
		expect(screen.queryByRole("button", { name: "Load more history" })).not.toBeInTheDocument();
	});

	it("disables the Load more history button while a batch is loading", () => {
		mockUseJobHistory.mockReturnValue({
			events: [{ seq: 1, ts: "t1", type: "job.log", run_id: "run-1", job_id: "job-1", data: { line: "hi" } }],
			loading: true,
			error: null,
			truncated: true,
			loadMore: vi.fn(),
		});

		renderDetail();

		expect(screen.getByRole("button", { name: "Loading…" })).toBeDisabled();
	});
});
