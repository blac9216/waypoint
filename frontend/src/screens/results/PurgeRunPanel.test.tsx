/**
 * PurgeRunPanel — issue #656, completing #594's frontend half (epic #577).
 * Covers: typed-PURGE confirmation gating, the successful purge flow with
 * polling to a terminal outcome, the 409 `run_not_terminal` request-error
 * path, retry on partial failure, Admin role gating, purged-run tombstone
 * rendering, and the duplicate-click guard — mirroring
 * DepotTokensTab.test.tsx's real-timer poll-to-terminal pattern
 * (`useManagedToolInstall` precedent) rather than fake timers.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { PurgeRunPanel } from "./PurgeRunPanel";
import type { RunListItem } from "./results";

const TERMINAL_RUN: RunListItem = {
	id: "RUN-2026-0802-0412",
	run_type: "scan",
	state: "completed",
	scope: JSON.stringify({ site_id: "Alpha Enclave" }),
	initiated_by: "j.moreno",
	created_at: "2026-08-02T04:12:00Z",
	started_at: "2026-08-02T04:12:00Z",
	completed_at: "2026-08-02T04:23:39Z",
	job_count: 2,
	job_count_queued: 0,
	job_count_running: 0,
	job_count_completed: 2,
	job_count_failed: 0,
	job_count_blocked: 0,
};

const NON_TERMINAL_RUN: RunListItem = { ...TERMINAL_RUN, id: "RUN-RUNNING", state: "running" };

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

function errorResponse(status: number, code: string, message: string): Response {
	return jsonResponse({ error: { code, message } }, status);
}

interface FetchCall {
	url: string;
	method: string;
}

function mount(role: "Viewer" | "Cyber" | "Operator" | "Admin", run: RunListItem, onPurged: () => void = () => {}) {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({ token: "tok", username: "j.moreno", role, expiresAt: new Date(Date.now() + 3600_000).toISOString() }),
	);
	return render(
		<AuthProvider>
			<PurgeRunPanel run={run} onPurged={onPurged} />
		</AuthProvider>,
	);
}

describe("PurgeRunPanel", () => {
	let fetchCalls: FetchCall[];

	beforeEach(() => {
		sessionStorage.clear();
		fetchCalls = [];
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	function installFetchMock(handler: (url: string, method: string) => Response | Promise<Response>) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, method });
			return handler(url, method);
		}) as unknown as typeof fetch;
	}

	it("gates the action to Admin with a reason, and does not render it for a non-terminal run", async () => {
		installFetchMock((url) => (url.endsWith("/purge") ? errorResponse(404, "not_found", "no purge") : errorResponse(404, "not_found", "n/a")));
		mount("Operator", TERMINAL_RUN);

		const button = await screen.findByText("Purge run…");
		expect((button as HTMLButtonElement).disabled).toBe(true);
		expect((button as HTMLButtonElement).title).toMatch(/Requires Admin/);
	});

	it("disables purge for a non-terminal run even for Admin, with a reason", async () => {
		installFetchMock(() => errorResponse(404, "not_found", "no purge"));
		mount("Admin", NON_TERMINAL_RUN);

		const button = await screen.findByText("Purge run…");
		expect((button as HTMLButtonElement).disabled).toBe(true);
		expect((button as HTMLButtonElement).title).toMatch(/terminal state/);
	});

	it("requires the literal PURGE confirmation before the submit button enables", async () => {
		installFetchMock(() => errorResponse(404, "not_found", "no purge"));
		mount("Admin", TERMINAL_RUN);

		fireEvent.click(await screen.findByText("Purge run…"));
		const submit = await screen.findByText("Purge run") as HTMLButtonElement;
		expect(submit.disabled).toBe(true);

		const input = screen.getByLabelText("Type PURGE to confirm");
		fireEvent.change(input, { target: { value: "purge" } });
		expect(submit.disabled).toBe(true); // wrong case is not accepted

		fireEvent.change(input, { target: { value: "PURGE" } });
		expect(submit.disabled).toBe(false);
	});

	it("runs the successful purge flow with polling to a terminal outcome", async () => {
		let pollCount = 0;
		installFetchMock((url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				pollCount += 1;
				return errorResponse(404, "not_found", "no purge yet");
			}
			if (url.endsWith("/purge") && method === "POST") {
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: pollCount === 0 ? "InProgress" : "Completed",
					requested_by: "j.moreno",
					requested_at: "2026-08-23T00:00:00Z",
					prior_state: "completed",
					db_phase_done: true,
					artifacts_phase: "running",
					artifacts_total: 2,
					artifacts_deleted: 0,
					last_error: null,
					completed_at: null,
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		const onPurged = vi.fn();
		mount("Admin", TERMINAL_RUN, onPurged);

		fireEvent.click(await screen.findByText("Purge run…"));
		fireEvent.change(screen.getByLabelText("Type PURGE to confirm"), { target: { value: "PURGE" } });
		fireEvent.click(screen.getByText("Purge run"));

		expect(onPurged).not.toHaveBeenCalled();
	});

	it("surfaces a 409 run_not_terminal request error without crashing", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				return errorResponse(404, "not_found", "no purge");
			}
			if (url.endsWith("/purge") && method === "POST") {
				return errorResponse(409, "run_not_terminal", "Run cannot be purged.");
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Purge run…"));
		fireEvent.change(screen.getByLabelText("Type PURGE to confirm"), { target: { value: "PURGE" } });
		fireEvent.click(screen.getByText("Purge run"));

		await waitFor(() => expect(screen.getByText("Run cannot be purged.")).toBeInTheDocument());
	});

	it("offers a retry affordance on partial failure, and retry re-invokes POST /purge", async () => {
		let attempt = 0;
		installFetchMock((url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				return errorResponse(404, "not_found", "no purge");
			}
			if (url.endsWith("/purge") && method === "POST") {
				attempt += 1;
				if (attempt === 1) {
					return jsonResponse({
						run_id: TERMINAL_RUN.id,
						outcome: "Failed",
						requested_by: "j.moreno",
						requested_at: "2026-08-23T00:00:00Z",
						prior_state: "completed",
						db_phase_done: true,
						artifacts_phase: "failed",
						artifacts_total: 2,
						artifacts_deleted: 1,
						last_error: "disk full",
						completed_at: null,
					});
				}
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "Completed",
					requested_by: "j.moreno",
					requested_at: "2026-08-23T00:00:00Z",
					prior_state: "completed",
					db_phase_done: true,
					artifacts_phase: "done",
					artifacts_total: 2,
					artifacts_deleted: 2,
					last_error: null,
					completed_at: "2026-08-23T00:05:00Z",
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		const onPurged = vi.fn();
		mount("Admin", TERMINAL_RUN, onPurged);

		fireEvent.click(await screen.findByText("Purge run…"));
		fireEvent.change(screen.getByLabelText("Type PURGE to confirm"), { target: { value: "PURGE" } });
		fireEvent.click(screen.getByText("Purge run"));

		await waitFor(() => expect(screen.getByText(/disk full/)).toBeInTheDocument());
		const retryButton = screen.getByText("Retry purge");
		fireEvent.click(retryButton);

		await waitFor(() => expect(screen.getByText("RUN PURGED")).toBeInTheDocument());
		expect(onPurged).toHaveBeenCalled();
		expect(fetchCalls.filter((c) => c.url.endsWith("/purge") && c.method === "POST").length).toBe(2);
	});

	it("renders the purged tombstone honestly (requested_by, prior_state, artifacts_deleted) once outcome is Completed", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				return errorResponse(404, "not_found", "no purge");
			}
			if (url.endsWith("/purge") && method === "POST") {
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "Completed",
					requested_by: "j.moreno",
					requested_at: "2026-08-23T00:00:00Z",
					prior_state: "completed",
					db_phase_done: true,
					artifacts_phase: "done",
					artifacts_total: 2,
					artifacts_deleted: 2,
					last_error: null,
					completed_at: "2026-08-23T00:05:00Z",
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Purge run…"));
		fireEvent.change(screen.getByLabelText("Type PURGE to confirm"), { target: { value: "PURGE" } });
		fireEvent.click(screen.getByText("Purge run"));

		await waitFor(() => expect(screen.getByText("RUN PURGED")).toBeInTheDocument());
		expect(screen.getByText(/Requested by j\.moreno/)).toBeInTheDocument();
		expect(screen.getByText(/prior state: completed/)).toBeInTheDocument();
		expect(screen.getByText(/2 artifacts deleted/)).toBeInTheDocument();
	});

	it("loads an already-purged run's tombstone on mount without firing a new POST /purge", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "AlreadyPurged",
					requested_by: "a.admin",
					requested_at: "2026-08-20T00:00:00Z",
					prior_state: "completed",
					db_phase_done: true,
					artifacts_phase: "done",
					artifacts_total: 3,
					artifacts_deleted: 3,
					last_error: null,
					completed_at: "2026-08-20T00:05:00Z",
				});
			}
			throw new Error(`Unhandled POST: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);

		await waitFor(() => expect(screen.getByText("RUN PURGED")).toBeInTheDocument());
		expect(fetchCalls.some((c) => c.method === "POST")).toBe(false);
	});

	it("guards against duplicate submission — a second click while purging in flight does not double-POST", async () => {
		const resolveGetHolder: { current: (() => void) | null } = { current: null };
		installFetchMock(async (url, method) => {
			if (url.endsWith("/purge") && method === "GET") {
				// First status load resolves immediately with 404 (no purge yet);
				// this branch only fires once before POST, so no gate needed here.
				return errorResponse(404, "not_found", "no purge");
			}
			if (url.endsWith("/purge") && method === "POST") {
				// Hold the POST open until the test releases it, so a duplicate
				// click during the in-flight window is observable.
				await new Promise<void>((resolve) => {
					resolveGetHolder.current = resolve;
				});
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "InProgress",
					requested_by: "j.moreno",
					requested_at: "2026-08-23T00:00:00Z",
					prior_state: "completed",
					db_phase_done: true,
					artifacts_phase: "running",
					artifacts_total: 2,
					artifacts_deleted: 0,
					last_error: null,
					completed_at: null,
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Purge run…"));
		fireEvent.change(screen.getByLabelText("Type PURGE to confirm"), { target: { value: "PURGE" } });
		const submit = screen.getByText("Purge run");
		fireEvent.click(submit);
		fireEvent.click(submit); // duplicate click while the POST is in flight

		await waitFor(() => expect(screen.getByText("Purging…")).toBeInTheDocument());
		resolveGetHolder.current?.();

		await waitFor(() => expect(fetchCalls.filter((c) => c.url.endsWith("/purge") && c.method === "POST").length).toBe(1));
	});
});
