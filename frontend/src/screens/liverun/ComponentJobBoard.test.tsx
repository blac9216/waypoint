/**
 * Issue #757: the component board's scale contract at the UI layer —
 * counters render from the server-side grouped counts alone; item events
 * load ONLY for the selected item (through PR #931's events/truncated/
 * loadMore contract, scoped by jobId); per-item controls appear only where
 * legal for the item's state.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchAllJobEventHistory } from "../../api/jobEventHistory";
import { useAuth } from "../../lib/auth-context";
import { ComponentJobBoard } from "./ComponentJobBoard";
import { bulkCancelJobs, bulkRetryJobs, type ComponentJobCount, type ComponentJobItem } from "./componentJobs";
import { useComponentJobs } from "./useComponentJobs";

vi.mock("./useComponentJobs", () => ({
	useComponentJobs: vi.fn(),
}));

vi.mock("../../api/jobEventHistory", () => ({
	fetchAllJobEventHistory: vi.fn(),
}));

vi.mock("../../lib/auth-context", () => ({
	useAuth: vi.fn(),
}));

vi.mock("./componentJobs", async (importOriginal) => {
	const actual = await importOriginal<typeof import("./componentJobs")>();
	return {
		...actual,
		bulkCancelJobs: vi.fn(),
		bulkRetryJobs: vi.fn(),
	};
});

const mockUseComponentJobs = vi.mocked(useComponentJobs);
const mockFetchHistory = vi.mocked(fetchAllJobEventHistory);
const mockUseAuth = vi.mocked(useAuth);
const mockBulkCancel = vi.mocked(bulkCancelJobs);
const mockBulkRetry = vi.mocked(bulkRetryJobs);

function item(overrides: Partial<ComponentJobItem> = {}): ComponentJobItem {
	return {
		id: "job-1",
		job_type: "scan",
		target_id: "t-1",
		target_name: "esxi-01.example.internal",
		state: "failed",
		stage: null,
		priority: 4,
		component_kind: "esxi",
		attempt_count: 1,
		created_at: "2026-08-27T00:00:00Z",
		started_at: "2026-08-27T00:00:01Z",
		finished_at: "2026-08-27T00:05:00Z",
		...overrides,
	};
}

const COUNTS: ComponentJobCount[] = [
	{ priority: 1, component_kind: "vcenter", state: "done", count: 12 },
	{ priority: 4, component_kind: "esxi", state: "queued", count: 9_500 },
	{ priority: 4, component_kind: "esxi", state: "failed", count: 500 },
];

function arrange(items: ComponentJobItem[]) {
	mockUseAuth.mockReturnValue({
		user: { username: "op", role: "Operator" },
		token: "tok",
		status: "signed-in",
		error: null,
		login: vi.fn(),
		localAuthAvailable: true,
		startOidcLogin: vi.fn(),
		stepUpOidcLogin: vi.fn(),
		logout: vi.fn(),
	});
	mockUseComponentJobs.mockReturnValue({
		counts: COUNTS,
		items,
		hasMore: false,
		loading: false,
		loadingMore: false,
		error: null,
		loadMore: vi.fn(),
		refresh: vi.fn(),
	});
	mockFetchHistory.mockResolvedValue({ events: [], nextCursor: null, truncated: false });
}

describe("ComponentJobBoard (issue #757)", () => {
	beforeEach(() => {
		mockUseComponentJobs.mockReset();
		mockFetchHistory.mockReset();
		mockUseAuth.mockReset();
		mockBulkCancel.mockReset();
		mockBulkRetry.mockReset();
	});

	it("renders grouped counters from server-side counts (never from item rows)", () => {
		// Item rows deliberately DISAGREE with the counts (only one row loaded vs
		// 10,012 counted) -- the counters must come from the counts endpoint.
		arrange([item()]);
		render(<ComponentJobBoard runId="run-1" />);

		expect(screen.getByText("10012 component jobs")).toBeInTheDocument();
		expect(screen.getByText("PRIORITY 1 · 12")).toBeInTheDocument();
		expect(screen.getByText("PRIORITY 4 · 10000")).toBeInTheDocument();
	});

	it("loads events only for the selected item, scoped by jobId", async () => {
		arrange([item({ id: "job-1" }), item({ id: "job-2", target_name: "esxi-02.example.internal" })]);
		render(<ComponentJobBoard runId="run-1" />);

		// Nothing selected: no history fetch at all.
		expect(mockFetchHistory).not.toHaveBeenCalled();

		fireEvent.click(screen.getByText("esxi-02.example.internal"));
		await waitFor(() => expect(mockFetchHistory).toHaveBeenCalledTimes(1));
		expect(mockFetchHistory).toHaveBeenCalledWith("run-1", { jobId: "job-2" }, expect.any(Number));
	});

	it("shows the truncation notice and continues from the returned cursor", async () => {
		arrange([item({ id: "job-1" })]);
		mockFetchHistory.mockResolvedValueOnce({
			events: [{ seq: 1, ts: "t1", type: "job.log", run_id: "run-1", job_id: "job-1", data: { line: "l1" } }],
			nextCursor: "v1:next",
			truncated: true,
		});
		render(<ComponentJobBoard runId="run-1" />);
		fireEvent.click(screen.getByText("esxi-01.example.internal"));

		const notice = await screen.findByRole("status");
		expect(notice.textContent).toContain("more history exists");

		mockFetchHistory.mockResolvedValueOnce({ events: [], nextCursor: null, truncated: false });
		fireEvent.click(screen.getByText("Load more history"));
		await waitFor(() =>
			expect(mockFetchHistory).toHaveBeenLastCalledWith("run-1", { jobId: "job-1" }, expect.any(Number), "v1:next"),
		);
	});

	it("offers Retry only for a failed item and Cancel only for a cancellable state", async () => {
		arrange([
			item({ id: "job-failed", state: "failed", target_name: "failed-host" }),
			item({ id: "job-running", state: "running", target_name: "running-host" }),
			item({ id: "job-done", state: "done", target_name: "done-host" }),
		]);
		render(<ComponentJobBoard runId="run-1" />);

		fireEvent.click(screen.getByText("failed-host"));
		expect(await screen.findByText("Retry")).toBeInTheDocument();
		expect(screen.queryByText("Cancel")).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("running-host"));
		expect(await screen.findByText("Cancel")).toBeInTheDocument();
		expect(screen.queryByText("Retry")).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("done-host"));
		expect(await screen.findByText("No controls for this state.")).toBeInTheDocument();
	});

	it("state chips toggle the state filter fed back into useComponentJobs", async () => {
		arrange([item()]);
		render(<ComponentJobBoard runId="run-1" />);

		const failedChip = screen.getByRole("button", { name: /failed 500/ });
		fireEvent.click(failedChip);

		await waitFor(() => {
			const lastCall = mockUseComponentJobs.mock.calls.at(-1)!;
			expect(lastCall[1]).toMatchObject({ state: "failed" });
		});
	});

	// -- issue #757: bulk operations -----------------------------------------

	it("checking rows reveals the bulk toolbar; bulk cancel sends exactly the checked ids", async () => {
		arrange([
			item({ id: "job-1", target_name: "host-1" }),
			item({ id: "job-2", target_name: "host-2" }),
			item({ id: "job-3", target_name: "host-3" }),
		]);
		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
		mockBulkCancel.mockResolvedValue({
			resolvedCount: 2,
			items: [
				{ jobId: "job-1", outcome: "cancelled" },
				{ jobId: "job-2", outcome: "not_cancellable" },
			],
		});
		render(<ComponentJobBoard runId="run-1" />);

		expect(screen.queryByLabelText("Bulk actions")).not.toBeInTheDocument();

		fireEvent.click(screen.getByLabelText("Select host-1"));
		fireEvent.click(screen.getByLabelText("Select host-2"));

		expect(screen.getByText("2 selected")).toBeInTheDocument();
		fireEvent.click(screen.getByText("Bulk cancel"));

		await waitFor(() => expect(mockBulkCancel).toHaveBeenCalledTimes(1));
		expect(mockBulkCancel).toHaveBeenCalledWith("run-1", { jobIds: ["job-1", "job-2"] });
		expect(confirmSpy).toHaveBeenCalled();

		// Honest per-item tally, not a collapsed all-or-nothing message.
		const result = await screen.findByLabelText("Bulk action results");
		expect(result.textContent).toContain("2 resolved");
		expect(result.textContent).toContain("1 cancelled");
		expect(result.textContent).toContain("1 not_cancellable");

		// Selection clears and the "3 selected" checkbox for job-3 was never checked.
		expect(screen.queryByText("Bulk actions")).not.toBeInTheDocument();
		confirmSpy.mockRestore();
	});

	it("bulk retry does not require confirmation and forwards checked ids", async () => {
		arrange([item({ id: "job-1", target_name: "host-1", state: "failed" })]);
		mockBulkRetry.mockResolvedValue({ resolvedCount: 1, items: [{ jobId: "job-1", outcome: "queued" }] });
		render(<ComponentJobBoard runId="run-1" />);

		fireEvent.click(screen.getByLabelText("Select host-1"));
		fireEvent.click(screen.getByText("Bulk retry"));

		await waitFor(() => expect(mockBulkRetry).toHaveBeenCalledWith("run-1", { jobIds: ["job-1"] }));
	});

	it("bulk cancel declined at the confirm prompt never calls the API", () => {
		arrange([item({ id: "job-1", target_name: "host-1" })]);
		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);
		render(<ComponentJobBoard runId="run-1" />);

		fireEvent.click(screen.getByLabelText("Select host-1"));
		fireEvent.click(screen.getByText("Bulk cancel"));

		expect(mockBulkCancel).not.toHaveBeenCalled();
		confirmSpy.mockRestore();
	});

	it("a role below the control gate renders the bulk buttons disabled", () => {
		mockUseAuth.mockReturnValue({
			user: { username: "v", role: "Viewer" },
			token: "tok",
			status: "signed-in",
			error: null,
			login: vi.fn(),
			localAuthAvailable: true,
			startOidcLogin: vi.fn(),
			stepUpOidcLogin: vi.fn(),
			logout: vi.fn(),
		});
		mockUseComponentJobs.mockReturnValue({
			counts: COUNTS,
			items: [item({ id: "job-1", target_name: "host-1" })],
			hasMore: false,
			loading: false,
			loadingMore: false,
			error: null,
			loadMore: vi.fn(),
			refresh: vi.fn(),
		});
		mockFetchHistory.mockResolvedValue({ events: [], nextCursor: null, truncated: false });
		render(<ComponentJobBoard runId="run-1" />);

		fireEvent.click(screen.getByLabelText("Select host-1"));

		expect(screen.getByText("Bulk cancel")).toBeDisabled();
		expect(screen.getByText("Bulk retry")).toBeDisabled();
	});
});
